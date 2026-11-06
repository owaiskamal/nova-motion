# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Novabit
"""Converts the photo restoration models to ONNX, with PyTorch and each project's own network code
(downloaded at a pinned commit by .github/workflows/enhance-models.yml, not copied into this repo):

    SCUNet (Apache-2.0, https://github.com/cszn/SCUNet): blind denoising of real photos
        scunet_color_real_psnr.pth, scunet_color_real_gan.pth
    NAFNet (MIT, https://github.com/megvii-research/NAFNet): denoising (SIDD, real phone-camera noise)
        and deblurring (GoPro: camera shake and soft focus; REDS: blur with JPEG damage)

    python tools/restore_to_onnx.py scunet  network_scunet.py         scunet_color_real_psnr.pth out.onnx
    python tools/restore_to_onnx.py nafnet-reds-64  NAFNet_arch.py    NAFNet-REDS-width64.pth    out.onnx

The ONNX takes "input" as 1 x 3 x H x W float RGB in 0..1 and gives the restored picture at the same
size; NAFNet takes any size, SCUNet only --size square (384 by default). The result is checked against
PyTorch on picture-like inputs of two sizes.
"""
import argparse
import importlib.util
import sys
import types

import torch
from torch import nn

NAFNETS = {
    # name: (width, enc_blk_nums, middle_blk_num, dec_blk_nums), from NAFNet's options/test/*.yml
    "nafnet-sidd-64": (64, [2, 2, 4, 8], 12, [2, 2, 2, 2]),
    "nafnet-sidd-32": (32, [2, 2, 4, 8], 12, [2, 2, 2, 2]),
    "nafnet-gopro-64": (64, [1, 1, 1, 28], 1, [1, 1, 1, 1]),
    "nafnet-gopro-32": (32, [1, 1, 1, 28], 1, [1, 1, 1, 1]),
    "nafnet-reds-64": (64, [1, 1, 1, 28], 1, [1, 1, 1, 1]),
}


class LayerNorm2d(nn.Module):
    """NAFNet's channel LayerNorm as plain operations (its own is an autograd.Function, which doesn't
    export), rearranged to stay within half precision. TensorRT runs the models in FP16 (largest number
    65504), and NAFNet REDS's deep blocks have channels thousands apart: squaring those differences
    reached 35 million, so the variance overflowed and every tile came out as a green checkerboard. Here
    the differences are first divided by their largest, which changes nothing in the result:

        (x - mu) / sqrt(var + eps)  ==  d / sqrt(mean(d * d) + eps / k^2),  d = (x - mu) / k,  any k > 0

    and with k = max(|x - mu|, sqrt(eps)) every value along the way stays between 0 and the channel count."""

    def __init__(self, channels, eps=1e-6):
        super().__init__()
        self.weight = nn.Parameter(torch.ones(channels))
        self.bias = nn.Parameter(torch.zeros(channels))
        self.eps = eps

    def forward(self, x):
        mu = x.mean(1, keepdim=True)
        diff = x - mu
        k = diff.abs().amax(1, keepdim=True).clamp_min(self.eps ** 0.5)
        d = diff / k
        var = (d * d).mean(1, keepdim=True)
        y = d / (var + self.eps / (k * k)).sqrt()
        return self.weight.view(1, -1, 1, 1) * y + self.bias.view(1, -1, 1, 1)


def stub(name, **attrs):
    m = types.ModuleType(name)
    m.__dict__.update(attrs)
    sys.modules[name] = m
    return m


def stubs():
    """The few imports the network files make that inference doesn't need (training helpers, FLOP counting)."""
    class DropPath(nn.Identity):
        def __init__(self, *a, **k):
            super().__init__()
    stub("thop", profile=lambda *a, **k: (0, 0))
    stub("timm"); stub("timm.models")
    stub("timm.models.layers", trunc_normal_=nn.init.trunc_normal_, DropPath=DropPath)
    for name in ("basicsr", "basicsr.models", "basicsr.models.archs", "basicsr.utils"):
        stub(name)
    stub("basicsr.models.archs.arch_util", LayerNorm2d=LayerNorm2d)
    stub("basicsr.models.archs.local_arch", Local_Base=type("Local_Base", (), {}))


def load_module(path):
    stubs()
    spec = importlib.util.spec_from_file_location("network", path)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def build(kind, code, weights):
    mod = load_module(code)
    state = torch.load(weights, map_location="cpu", weights_only=True)
    if kind == "scunet":
        net = mod.SCUNet(in_nc=3, config=[4, 4, 4, 4, 4, 4, 4], dim=64)
    else:
        width, enc, middle, dec = NAFNETS[kind]
        net = mod.NAFNet(img_channel=3, width=width, middle_blk_num=middle, enc_blk_nums=enc, dec_blk_nums=dec)
        state = state.get("params", state)
    net.load_state_dict(state, strict=True)
    return net.eval()


class Clamped(nn.Module):
    def __init__(self, net):
        super().__init__()
        self.net = net

    def forward(self, x):
        return self.net(x).clamp(0, 1)


def picture(h, w):
    """A picture-like input (smooth shapes and a little noise): pure noise drives the deep NAFNets to
    extremes where float rounding alone differs by a few thousandths."""
    x = torch.nn.functional.interpolate(torch.rand(1, 3, h // 32, w // 32), size=(h, w), mode="bicubic")
    return (x + 0.03 * torch.randn_like(x)).clamp(0, 1)


def fp16_range(path, x):
    """Every tensor inside the network stays within half precision (TensorRT runs it in FP16): the
    largest value of each, on a picture-like input, below 65504 with room to spare."""
    import onnx
    import onnxruntime as ort
    base = onnx.load(path)
    names = [n.output[0] for n in base.graph.node if n.op_type not in ("Shape", "Constant", "Gather", "Unsqueeze", "Concat", "Cast")]
    opts = ort.SessionOptions()
    opts.graph_optimization_level = ort.GraphOptimizationLevel.ORT_DISABLE_ALL
    worst = (0.0, "")
    for i in range(0, len(names), 150):
        m = onnx.ModelProto()
        m.CopyFrom(base)
        part = names[i:i + 150]
        for n in part:
            m.graph.output.append(onnx.ValueInfoProto(name=n))
        outs = ort.InferenceSession(m.SerializeToString(), opts, providers=["CPUExecutionProvider"]).run(part, {"input": x.numpy()})
        for n, v in zip(part, outs):
            if getattr(v, "dtype", None) is not None and v.dtype.kind == "f" and v.size:
                worst = max(worst, (float(abs(v).max()), n))
    print(f"{path}: largest value inside {worst[0]:.0f} ({worst[1]})")
    if worst[0] > 16384:
        sys.exit(f"{path}: {worst[1]} reaches {worst[0]:.0f}, too close to half precision's limit (65504)")


def main(argv=None):
    p = argparse.ArgumentParser()
    p.add_argument("kind", choices=["scunet", *NAFNETS])
    p.add_argument("code", help="the network's .py file at the pinned commit")
    p.add_argument("weights")
    p.add_argument("out")
    p.add_argument("--size", type=int, default=384)
    a = p.parse_args(argv)
    if a.size % 64:
        p.error("--size must be a multiple of 64")
    net = Clamped(build(a.kind, a.code, a.weights))
    torch.manual_seed(0)
    x = picture(a.size, a.size)
    # NAFNet pads to its own multiple of 16 inside the graph, so it takes any size, like the other
    # models; SCUNet's shifted windows are fixed to the size it was exported at
    dynamic = a.kind != "scunet"
    axes = {"input": {2: "H", 3: "W"}, "output": {2: "H", 3: "W"}} if dynamic else None
    with torch.no_grad():
        torch.onnx.export(net, (x,), a.out, input_names=["input"], output_names=["output"], opset_version=17,
                          dynamic_axes=axes, dynamo=False)
    import onnxruntime as ort
    # Without ONNX Runtime's fused kernels: they reorder float maths, and the 36 blocks of the deblur
    # networks magnify that to a few hundredths here and there; the check is of the conversion itself
    opts = ort.SessionOptions()
    opts.graph_optimization_level = ort.GraphOptimizationLevel.ORT_DISABLE_ALL
    session = ort.InferenceSession(a.out, opts, providers=["CPUExecutionProvider"])
    for h, w in [(a.size, a.size)] + ([(a.size - 64, a.size + 72)] if dynamic else []):
        x = picture(h, w)
        with torch.no_grad():
            want = net(x).numpy()
        got = session.run(None, {"input": x.numpy()})[0]
        diff, mean = float(abs(got - want).max()), float(abs(got - want).mean())
        print(f"{a.out}: {a.kind} at {w}x{h}, difference from PyTorch: largest {diff:.2e}, average {mean:.2e}")
        if got.shape != want.shape or diff > 1e-2 or mean > 1e-4:
            sys.exit(f"{a.out} doesn't match PyTorch")
    if dynamic:
        fp16_range(a.out, picture(a.size, a.size))


if __name__ == "__main__":
    main()
