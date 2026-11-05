# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Novabit
"""Tests for engine/parallax.py with a stand-in depth model (brightness as depth), on the processor.

    pip install numpy pillow onnxruntime onnx
    python tests/engine/test_parallax.py [path/to/real/depth.onnx]
"""
import os
import sys
import tempfile

import numpy as np
from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "..", "engine"))
import parallax  # noqa: E402

failures = 0


def check(ok, what):
    global failures
    print(("ok   " if ok else "FAIL ") + what)
    if not ok:
        failures += 1


def fake_model(path):
    """pixel_values [1,3,H,W] -> predicted_depth [1,H,W]: the mean of the channels, like the real one's shapes."""
    import onnx
    from onnx import TensorProto, helper
    x = helper.make_tensor_value_info("pixel_values", TensorProto.FLOAT, [1, 3, "h", "w"])
    y = helper.make_tensor_value_info("predicted_depth", TensorProto.FLOAT, [1, "h", "w"])
    axes = helper.make_tensor("axes", TensorProto.INT64, [1], [1])
    node = helper.make_node("ReduceMean", ["pixel_values", "axes"], ["predicted_depth"], keepdims=0)
    graph = helper.make_graph([node], "fake_depth", [x], [y], initializer=[axes])
    model = helper.make_model(graph, opset_imports=[helper.make_opsetid("", 18)])
    model.ir_version = 9
    onnx.save(model, path)


def picture(path, w=640, h=360):
    """A dark-to-light sky and a bright disc in front: brightness is depth for the fake model."""
    img = Image.new("RGB", (w, h))
    px = img.load()
    for y in range(h):
        for x in range(w):
            v = 40 + int(60 * y / h)
            px[x, y] = (v, v + 10, v + 30)
    ImageDraw.Draw(img).ellipse((w * 0.35, h * 0.3, w * 0.6, h * 0.8), fill=(240, 220, 200))
    img.save(path)


def frames(path, w, h):
    data = np.fromfile(path, dtype=np.uint8)
    return data.reshape(-1, h, w, 3)


tmp = tempfile.mkdtemp(prefix="nm-parallax-")
model = sys.argv[1] if len(sys.argv) > 1 else os.path.join(tmp, "fake.onnx")
if len(sys.argv) <= 1:
    fake_model(model)
img = os.path.join(tmp, "in.png")
picture(img)

# ---- sizes
check(parallax.output_size(4000, 3000, 1920, 1080) == (1440, 1080), "size: a 4:3 photo fits within 1080p")
check(parallax.output_size(1080, 1920, 1920, 1080) == (1080, 1920), "size: a portrait picture keeps its orientation")
check(parallax.output_size(641, 361, 1920, 1080) == (640, 360), "size: small pictures aren't enlarged, sizes even")
mw, mh = parallax.model_size(1920, 1080)
check(mw % 14 == 0 and mh % 14 == 0 and mw == 518, f"size: the depth model sees multiples of 14 ({mw}x{mh})")

# ---- depth helpers
d = np.zeros((20, 20), np.float32)
d[8:12, 8:12] = 1
g = parallax.grow_near(d, 2)
check(g[6, 6] == 1 and g[3, 3] == 0, "depth: near parts grow outward by the radius")
b = parallax.box_blur(np.ones((10, 10), np.float32), 3)
check(np.allclose(b, 1), "depth: blur keeps a flat depth flat")

# ---- moves
for m in parallax.MOVES:
    zoom = parallax.margin_zoom(m, 1.0, 16 / 9)
    check(1.0 < zoom < 1.2, f"move {m}: frames enlarged by {zoom:.3f} to hide the edges")
sx0, sy0, z0 = parallax.camera("sway", 0.0, 1.0)
sx1, sy1, z1 = parallax.camera("sway", 1.0, 1.0)
check(abs(sx0 - sx1) < 1e-9 and abs(z0 - z1) < 1e-9, "move sway: ends where it began, so it loops")

# ---- the whole run, raw frames out
for move in ("push", "sway", "drift"):
    out = os.path.join(tmp, f"{move}.rgb")
    code = parallax.main(["--image", img, "--out", out, "--move", move, "--seconds", "1", "--fps", "12",
                          "--model", model, "--cpu"])
    f = frames(out, 640, 360)
    check(code == 0 and f.shape == (12, 360, 640, 3), f"run {move}: 12 frames of 640x360 ({f.shape})")
    first, last = f[0].astype(int), f[-1].astype(int)
    moved = np.abs(first - last).mean()
    check(moved > 0.5, f"run {move}: the picture moves ({moved:.2f})")
    edges = np.concatenate([f[:, 0].reshape(-1, 3), f[:, -1].reshape(-1, 3), f[:, :, 0].reshape(-1, 3), f[:, :, -1].reshape(-1, 3)])
    check(edges.min() >= 30, f"run {move}: no empty edges (darkest edge pixel {edges.min()})")
    if move == "sway":
        step = np.abs(f[0].astype(int) - f[1].astype(int)).mean()
        wrap = np.abs(f[-1].astype(int) - f[0].astype(int)).mean()
        check(wrap < step * 2.5 + 0.5, f"run sway: the last frame leads back into the first ({wrap:.2f} vs step {step:.2f})")

# The disc, being nearer, moves more than the sky on a drift
f = frames(os.path.join(tmp, "drift.rgb"), 640, 360)
disc = np.abs(f[0, 150:250, 200:400].astype(int) - f[-1, 150:250, 200:400].astype(int)).mean()
sky = np.abs(f[0, 5:60, 5:200].astype(int) - f[-1, 5:60, 5:200].astype(int)).mean()
check(disc > sky, f"run drift: the near disc moves more than the far sky ({disc:.1f} vs {sky:.1f})")

print()
print("FAILED" if failures else "PASSED", f"({failures} failures)")
sys.exit(1 if failures else 0)
