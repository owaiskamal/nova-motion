# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Novabit
"""Tests for engine/photo.py (Enhance a photo), no GPU needed: vs-mlrt is replaced by a stand-in whose
models enlarge by their own factor with a plain resize, so the whole chain runs on real VapourSynth.

    pip install vapoursynth numpy pillow
    python tests/engine/test_photo.py
"""
import contextlib
import enum
import io
import os
import sys
import tempfile
import types
from dataclasses import dataclass

import vapoursynth as vs

ENGINE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "engine")
sys.path.insert(0, ENGINE)
import enhance  # noqa: E402
import photo  # noqa: E402
from PIL import Image  # noqa: E402

core = vs.core
fails = 0


def check(ok, what):
    global fails
    print(("PASS " if ok else "FAIL ") + what)
    if not ok:
        fails += 1


MODELS = tempfile.mkdtemp(prefix="nm-photo-models-")
calls = []
m = types.ModuleType("vsmlrt")
m.models_path = MODELS


class Backend:
    @dataclass
    class TRT:
        fp16: bool = False


m.Backend = Backend


class DPIRModel(enum.IntEnum):
    drunet_deblocking_color = 3


m.DPIRModel = DPIRModel


def inference(clip, path, overlap=(0, 0), tilesize=None, backend=None):
    name = os.path.basename(path)
    scale = 4 if ("x4" in name or "animevideov3" in name) else 2
    calls.append(("inference", name, clip.width, clip.height))
    return core.resize.Point(clip, clip.width * scale, clip.height * scale)


def DPIR(clip, strength, model, tiles=None, tilesize=None, overlap=None, backend=None):
    calls.append(("dpir", clip.width, clip.height))
    return clip


m.inference, m.DPIR = inference, DPIR
sys.modules["vsmlrt"] = m
enhance.backend = lambda cfg, w, h: Backend.TRT(fp16=True)
for sub, files in {"enhance": ["realesr-general-x4v3", "realesr-general-mid-x4v3", "realesr-general-wdn-x4v3"],
                   "cugan": ["up2x-latest-no-denoise", "up2x-latest-conservative", "up2x-latest-denoise3x"],
                   "RealESRGANv2": ["realesr-animevideov3"], "dpir": ["drunet_deblocking_color"]}.items():
    os.makedirs(os.path.join(MODELS, sub), exist_ok=True)
    for f in files:
        open(os.path.join(MODELS, sub, f + ".onnx"), "w").close()

d = tempfile.mkdtemp(prefix="nm-photo-")


def picture(name, w, h, mode="RGB", **save):
    img = Image.new(mode, (w, h))
    px = img.load()
    for y in range(h):
        for x in range(w):
            c = ((x * 255) // w, (y * 255) // h, ((x + y) * 7) % 256)
            px[x, y] = c + ((x * 255) // w,) if mode == "RGBA" else c
    path = os.path.join(d, name)
    img.save(path, **save)
    return path


def run(image, out, settings):
    calls.clear()
    lines = io.StringIO()
    with contextlib.redirect_stdout(lines):
        code = photo.main(["--image", image, "--out", out, "--settings", settings])
    return code, lines.getvalue()


# ---------------------------------------------------------------- sizes
check(photo.target(1000, 600, 2) == (2, 1200), "2x: twice the height")
s, h = photo.target(4000, 3000, 4)
check(abs(s - 8192 / 4000) < 1e-9 and h == 6144, f"kept within 8K on the long side ({s:.3f}, {h})")
check(photo.target(800, 600, 1) == (1, 600), "1x: the same size, cleaned")

# ---------------------------------------------------------------- a picture with transparency
src = picture("logo.png", 300, 200, "RGBA")
out = os.path.join(d, "logo (NOVA Enhance 2x).png")
code, text = run(src, out, '{"preset": "film", "scale": 2, "clean": "light"}')
res = Image.open(out) if os.path.exists(out) else None
check(code == 0 and res is not None and res.size == (600, 400), f"2x: 300 x 200 becomes 600 x 400 ({res and res.size})")
check(res is not None and res.mode == "RGBA" and res.getpixel((599, 10))[3] > 240 and res.getpixel((0, 10))[3] < 10,
      "transparency kept, enlarged with the picture")
fed = [c for c in calls if c[0] == "inference"]
check(fed and fed[0][1] == "realesr-general-mid-x4v3.onnx" and fed[0][2:] == (640, 384),
      f"the whole picture goes to the model (padded to its tile): {fed[:1]}")
check("done " + out in text and "stage enhance" in text, "says its stages and where it saved")
check(not os.path.exists(out + ".part"), "no half-written file left")
a = Image.open(src).convert("RGB").resize((600, 400), Image.BICUBIC)
diff = sum(abs(p - q) for p, q in zip(a.getpixel((300, 200)), res.convert("RGB").getpixel((300, 200))))
check(diff < 30, f"colours survive the round trip through YUV (difference {diff})")

# ---------------------------------------------------------------- Max detail
code, text = run(src, os.path.join(d, "max.png"), '{"preset": "film", "scale": 2, "model": "max"}')
check(code == 0 and "aren't installed" in text and [c for c in calls if c[0] == "inference"][0][1] == "realesr-general-mid-x4v3.onnx",
      "Max detail without its models: says so and uses the standard ones")
open(os.path.join(MODELS, "enhance", "RealESRGAN_x4plus.onnx"), "w").close()
open(os.path.join(MODELS, "enhance", "RealESRGAN_x4plus_anime_6B.onnx"), "w").close()
run(src, os.path.join(d, "max2.png"), '{"preset": "film", "scale": 2, "model": "max"}')
check([c for c in calls if c[0] == "inference"][0][1] == "RealESRGAN_x4plus.onnx", "Max detail, film: Real-ESRGAN x4plus")
run(src, os.path.join(d, "max3.png"), '{"preset": "anime", "scale": 2, "model": "max"}')
check([c for c in calls if c[0] == "inference"][0][1] == "RealESRGAN_x4plus_anime_6B.onnx", "Max detail, anime: x4plus anime")

# ---------------------------------------------------------------- JPEG in and out, compression, rotation
jpg = picture("phone.jpg", 320, 240, quality=60, exif=b"Exif\x00\x00MM\x00*\x00\x00\x00\x08\x00\x01\x01\x12\x00\x03\x00\x00\x00\x01\x00\x06\x00\x00\x00\x00\x00\x00")
out = os.path.join(d, "phone.jpg.out.jpg")
code, text = run(jpg, out, '{"preset": "film", "scale": 2, "fix_compression": true}')
res = Image.open(out) if os.path.exists(out) else None
check(code == 0 and res is not None and res.size == (480, 640) and res.format == "JPEG",
      f"a phone photo turned upright by its EXIF, saved as JPEG ({res and res.size})")
check(any(c[0] == "dpir" for c in calls) and "stage clean" in text, "Fix compression cleans JPEG blocks first")

code, text = run(src, os.path.join(d, "same.png"), '{"preset": "anime", "scale": 1}')
res = Image.open(os.path.join(d, "same.png"))
check(code == 0 and res.size == (300, 200), "1x: the same size, enhanced")

# ---------------------------------------------------------------- before/after at 100%
cmp_path = os.path.join(d, "cmp.png")
code, text = run(src, os.path.join(d, "c.png"), '{"preset": "film", "scale": 2}')
lines = io.StringIO()
with contextlib.redirect_stdout(lines):
    code = photo.main(["--image", src, "--out", os.path.join(d, "c.png"), "--compare", cmp_path, "--spot", "0.9,0.1", "--compare-only"])
c = Image.open(cmp_path) if os.path.exists(cmp_path) else None
check(code == 0 and c is not None and c.size[1] == 400 and c.size[0] > 1200 and "compare " + cmp_path in lines.getvalue(),
      f"before | after side by side at 100% ({c and c.size})")
check("model realesr-general-mid-x4v3.onnx" in text, "says which model it used")

# ---------------------------------------------------------------- a 12 MP phone photo reaches the model whole
calls.clear()
e = photo.photo_settings({"preset": "film", "scale": 1, "model": "max"})
e["height"] = 4000
enhance.upscale(core.std.BlankClip(width=3000, height=4000, format=vs.YUV444P16), {}, e, "709")
fed = [c for c in calls if c[0] == "inference"]
check(fed and fed[0][1] == "RealESRGAN_x4plus.onnx" and fed[0][2:] == (3000, 4000),
      f"3000 x 4000 at the same size: the model sees all of it, not a halved copy ({fed[:1]})")

print()
print("ALL PASSED" if not fails else f"{fails} FAILED")
sys.exit(1 if fails else 0)
