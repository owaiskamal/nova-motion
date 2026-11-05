# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Novabit
"""Checks an RRDBNet ONNX made by realesrgan_to_onnx.py against the same weights run in PyTorch, with
the network written out here as BasicSR defines it (the architecture Real-ESRGAN x4plus uses).

    pip install torch onnxruntime numpy
    python tools/check_rrdb_onnx.py RealESRGAN_x4plus.pth RealESRGAN_x4plus.onnx
"""
import os
import sys

import numpy as np
import onnxruntime as ort
import torch
from torch import nn
from torch.nn import functional as F

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from realesrgan_to_onnx import describe_rrdb, load_pth  # noqa: E402


class RDB(nn.Module):
    def __init__(self, f, g):
        super().__init__()
        self.conv1 = nn.Conv2d(f, g, 3, 1, 1)
        self.conv2 = nn.Conv2d(f + g, g, 3, 1, 1)
        self.conv3 = nn.Conv2d(f + 2 * g, g, 3, 1, 1)
        self.conv4 = nn.Conv2d(f + 3 * g, g, 3, 1, 1)
        self.conv5 = nn.Conv2d(f + 4 * g, f, 3, 1, 1)
        self.lrelu = nn.LeakyReLU(0.2, inplace=True)

    def forward(self, x):
        x1 = self.lrelu(self.conv1(x))
        x2 = self.lrelu(self.conv2(torch.cat((x, x1), 1)))
        x3 = self.lrelu(self.conv3(torch.cat((x, x1, x2), 1)))
        x4 = self.lrelu(self.conv4(torch.cat((x, x1, x2, x3), 1)))
        x5 = self.conv5(torch.cat((x, x1, x2, x3, x4), 1))
        return x5 * 0.2 + x


class RRDB(nn.Module):
    def __init__(self, f, g):
        super().__init__()
        self.rdb1, self.rdb2, self.rdb3 = RDB(f, g), RDB(f, g), RDB(f, g)

    def forward(self, x):
        return self.rdb3(self.rdb2(self.rdb1(x))) * 0.2 + x


class RRDBNet(nn.Module):
    def __init__(self, blocks, f, g):
        super().__init__()
        self.conv_first = nn.Conv2d(3, f, 3, 1, 1)
        self.body = nn.Sequential(*[RRDB(f, g) for _ in range(blocks)])
        self.conv_body = nn.Conv2d(f, f, 3, 1, 1)
        self.conv_up1 = nn.Conv2d(f, f, 3, 1, 1)
        self.conv_up2 = nn.Conv2d(f, f, 3, 1, 1)
        self.conv_hr = nn.Conv2d(f, f, 3, 1, 1)
        self.conv_last = nn.Conv2d(f, 3, 3, 1, 1)
        self.lrelu = nn.LeakyReLU(0.2, inplace=True)

    def forward(self, x):
        feat = self.conv_first(x)
        feat = feat + self.conv_body(self.body(feat))
        feat = self.lrelu(self.conv_up1(F.interpolate(feat, scale_factor=2, mode="nearest")))
        feat = self.lrelu(self.conv_up2(F.interpolate(feat, scale_factor=2, mode="nearest")))
        return self.conv_last(self.lrelu(self.conv_hr(feat)))


def main(pth, onnx_path):
    state = load_pth(pth)
    blocks, f, g = describe_rrdb(state)
    net = RRDBNet(blocks, f, g).eval()
    net.load_state_dict({k: torch.from_numpy(v) for k, v in state.items()}, strict=True)
    x = np.random.default_rng(1).random((1, 3, 48, 64), dtype=np.float32)
    with torch.no_grad():
        want = net(torch.from_numpy(x)).numpy()
    got = ort.InferenceSession(onnx_path, providers=["CPUExecutionProvider"]).run(None, {"input": x})[0]
    diff = float(np.abs(want - got).max())
    print(f"{onnx_path}: {want.shape}, largest difference from PyTorch {diff:.2e}")
    return 0 if want.shape == got.shape and diff < 1e-3 else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1], sys.argv[2]))
