# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Novabit
"""Converts Real-ESRGAN's compact models (SRVGGNetCompact, e.g. realesr-general-x4v3, BSD-3-Clause)
from their PyTorch .pth files to ONNX, without PyTorch: the .pth is a zip of a pickle and raw tensor
data, read here with numpy, and the network is small enough to write out layer by layer.

    python tools/realesrgan_to_onnx.py realesr-general-x4v3.pth realesr-general-x4v3.onnx [--blend other.pth 0.5]

--blend mixes the weights of two models of the same shape, as Real-ESRGAN's own "denoise strength"
does with realesr-general-x4v3 and its -wdn (denoise) twin.

The ONNX takes "input" as N x 3 x H x W float RGB in 0..1 and gives N x 3 x 4H x 4W, the layout vs-mlrt
expects. Network (Real-ESRGAN's SRVGGNetCompact): conv 3->64, PReLU, num_conv x (conv 64->64, PReLU),
conv 64->3*s*s, pixel shuffle by s, plus the input enlarged s times by nearest neighbour.
"""
import argparse
import pickle
import re
import sys
import zipfile

import numpy as np


class _Storage:
    def __init__(self, dtype, key):
        self.dtype, self.key = dtype, key


_DTYPES = {"FloatStorage": np.float32, "HalfStorage": np.float16, "DoubleStorage": np.float64}


def load_pth(path):
    """The state dict of a .pth as {name: numpy array}."""
    z = zipfile.ZipFile(path)
    root = z.namelist()[0].split("/")[0]

    def rebuild(storage, offset, size, stride, *_):
        data = np.frombuffer(z.read(f"{root}/data/{storage.key}"), dtype=storage.dtype)[offset:]
        if not len(size):
            return np.array(data[0])
        return np.array(np.lib.stride_tricks.as_strided(
            data, shape=tuple(size), strides=tuple(s * data.itemsize for s in stride)))

    class Unpickler(pickle.Unpickler):
        def find_class(self, module, name):
            if module == "torch._utils" and name in ("_rebuild_tensor_v2", "_rebuild_tensor"):
                return rebuild
            if module == "torch" and name in _DTYPES:
                return name
            if module == "collections" and name == "OrderedDict":
                import collections
                return collections.OrderedDict
            raise pickle.UnpicklingError(f"unexpected {module}.{name} in the .pth")

        def persistent_load(self, pid):
            # ('storage', storage_type, key, location, numel)
            _, kind, key, _, _ = pid
            return _Storage(_DTYPES[kind if isinstance(kind, str) else kind.__name__], key)

    state = Unpickler(z.open(f"{root}/data.pkl")).load()
    for k in ("params_ema", "params", "state_dict"):
        if isinstance(state, dict) and k in state:
            state = state[k]
            break
    return {k: v.astype(np.float32) for k, v in state.items()}


def describe(state):
    """(number of body convolutions, features, scale) from the weights."""
    convs = sorted(int(m.group(1)) for k in state if (m := re.fullmatch(r"body\.(\d+)\.weight", k)) and state[k].ndim == 4)
    first, last = state[f"body.{convs[0]}.weight"], state[f"body.{convs[-1]}.weight"]
    feat = first.shape[0]
    scale = int(round((last.shape[0] / 3) ** 0.5))
    return convs, feat, scale


def build_onnx(state, opset=17):
    from onnx import TensorProto, helper, numpy_helper
    convs, feat, scale = describe(state)
    nodes, inits = [], []
    x = "input"
    for i, idx in enumerate(convs):
        w, b = state[f"body.{idx}.weight"], state[f"body.{idx}.bias"]
        inits += [numpy_helper.from_array(w, f"w{idx}"), numpy_helper.from_array(b, f"b{idx}")]
        y = f"conv{idx}"
        nodes.append(helper.make_node("Conv", [x, f"w{idx}", f"b{idx}"], [y], pads=[1, 1, 1, 1], kernel_shape=[3, 3]))
        x = y
        act = f"body.{idx + 1}.weight"
        if i < len(convs) - 1:
            if act not in state:
                raise ValueError(f"expected a PReLU after body.{idx}")
            slope = state[act].reshape(-1, 1, 1)
            inits.append(numpy_helper.from_array(slope, f"a{idx}"))
            nodes.append(helper.make_node("PRelu", [x, f"a{idx}"], [f"act{idx}"]))
            x = f"act{idx}"
    nodes.append(helper.make_node("DepthToSpace", [x], ["shuffled"], blocksize=scale, mode="CRD"))
    inits.append(numpy_helper.from_array(np.array([1, 1, scale, scale], np.float32), "scales"))
    nodes.append(helper.make_node("Resize", ["input", "", "scales"], ["base"], mode="nearest"))
    nodes.append(helper.make_node("Add", ["shuffled", "base"], ["output"]))
    graph = helper.make_graph(
        nodes, "SRVGGNetCompact",
        [helper.make_tensor_value_info("input", TensorProto.FLOAT, ["N", 3, "H", "W"])],
        [helper.make_tensor_value_info("output", TensorProto.FLOAT, ["N", 3, "H4", "W4"])],
        initializer=inits)
    model = helper.make_model(graph, opset_imports=[helper.make_opsetid("", opset)], producer_name="nova-motion")
    model.ir_version = 8
    return model, scale


def main(argv=None):
    p = argparse.ArgumentParser()
    p.add_argument("pth")
    p.add_argument("onnx")
    p.add_argument("--blend", nargs=2, metavar=("OTHER_PTH", "WEIGHT"),
                   help="mix in another model: (1 - weight) * this + weight * other")
    a = p.parse_args(argv)
    import onnx
    state = load_pth(a.pth)
    if a.blend:
        other, w = load_pth(a.blend[0]), float(a.blend[1])
        if other.keys() != state.keys() or any(other[k].shape != state[k].shape for k in state):
            raise SystemExit("the two models don't have the same layers")
        state = {k: (1 - w) * state[k] + w * other[k] for k in state}
    model, scale = build_onnx(state)
    onnx.checker.check_model(model)
    onnx.save(model, a.onnx)
    print(f"{a.onnx}: {len(describe(state)[0])} convolutions, x{scale}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
