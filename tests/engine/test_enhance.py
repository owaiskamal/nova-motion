# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Novabit
"""Tests for Enhance (engine/enhance.py through engine/novamotion.vpy), no GPU needed.

vs-mlrt is replaced by a stand-in whose models enlarge by their own factor (x4 or x2) with a plain
resize, so the whole export chain runs on real VapourSynth + MVTools: sizes, formats, frame counts,
model choice, the before/after crop, detail and grain.

    pip install vapoursynth vapoursynth-mvtools
    python tests/engine/test_enhance.py engine/novamotion.vpy
"""
import enum
import json
import os
import sys
import tempfile
import types
from dataclasses import dataclass

import vapoursynth as vs

core = vs.core
SCRIPT = os.path.abspath(sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(__file__), "..", "..", "engine", "novamotion.vpy"))
sys.path.insert(0, os.path.dirname(SCRIPT))
import enhance  # noqa: E402

# The shipped VapourSynth's std.Merge refuses half floats (RGBH); newer ones accept them, so the
# tests make Merge as strict as the shipped one
_merge = core.std.Merge


def strict_merge(a, b, weight=0.5, **kw):
    for c in (a, b):
        if c.format.sample_type == vs.FLOAT and c.format.bits_per_sample == 16:
            raise vs.Error("Merge: Input clip must be constant format 8..16 bit integer or 32 bit float, passed RGBH.")
    return _merge(a, b, weight=weight, **kw)


class _Std:
    def __getattr__(self, name):
        return strict_merge if name == "Merge" else getattr(core.std, name)


class _Core:
    def __getattr__(self, name):
        return _Std() if name == "std" else getattr(core, name)


enhance.core = _Core()

fails = 0


def check(ok, what):
    global fails
    print(("PASS " if ok else "FAIL ") + what)
    if not ok:
        fails += 1


# ---------------------------------------------------------------- vs-mlrt stand-in
MODELS = tempfile.mkdtemp(prefix="nm-models-")
calls = []


def fake_vsmlrt(with_pro=True):
    m = types.ModuleType("vsmlrt")
    m.models_path = MODELS

    class Backend:
        @dataclass
        class TRT:
            fp16: bool = False
            device_id: int = 0
            num_streams: int = 1
            output_format: int = 0

        @dataclass
        class NCNN_VK:
            fp16: bool = False
            device_id: int = 0
            num_streams: int = 1

        @dataclass
        class OV_GPU:
            fp16: bool = False
            device_id: int = 0

    m.Backend = Backend

    class DPIRModel(enum.IntEnum):
        drunet_deblocking_color = 3

    m.DPIRModel = DPIRModel

    def calc_tilesize(tiles, tilesize, width, height, multiple, overlap_w, overlap_h):
        if tiles is None:
            return (width, height), (0, 0)
        return (-(-width // tiles) + overlap_w, -(-height // tiles) + overlap_h), (overlap_w, overlap_h)

    def inference(clip, path, overlap=(0, 0), tilesize=None, backend=None):
        assert tilesize is None or (tilesize[0] <= clip.width and tilesize[1] <= clip.height), "tile bigger than the picture"
        name = os.path.basename(path)
        scale = 4 if ("x4" in name or "animevideov3" in name) else 2
        calls.append(("inference", name, clip.format.name, tilesize, type(backend).__name__, clip.width, clip.height))
        assert clip.format.color_family == vs.RGB and clip.format.sample_type == vs.FLOAT
        return core.resize.Point(clip, clip.width * scale, clip.height * scale)

    def DPIR(clip, strength, model, tiles=None, tilesize=None, overlap=None, backend=None):
        calls.append(("dpir", clip.width, clip.height))
        assert clip.width % 8 == 0 and clip.height % 8 == 0, "DPIR needs multiples of 8"
        return clip

    m.calc_tilesize, m.inference, m.DPIR = calc_tilesize, inference, DPIR
    sys.modules["vsmlrt"] = m
    for sub, files in {"enhance": ["realesr-general-x4v3", "realesr-general-mid-x4v3", "realesr-general-wdn-x4v3"],
                       "cugan": ["up2x-latest-no-denoise", "up2x-latest-conservative", "up2x-latest-denoise3x"]
                       + (["pro-conservative-up2x", "pro-denoise3x-up2x", "pro-no-denoise3x-up2x"] if with_pro else []),
                       "RealESRGANv2": ["realesr-animevideov3"], "dpir": ["drunet_deblocking_color"]}.items():
        os.makedirs(os.path.join(MODELS, sub), exist_ok=True)
        for f in files:
            open(os.path.join(MODELS, sub, f + ".onnx"), "w").close()
    return m


fake_vsmlrt()
# A "TensorRT" core: the backend choice looks for core.trt; the stand-in only needs to exist
has_trt = hasattr(core, "trt")


def backend_override(kind):
    m = sys.modules["vsmlrt"]
    enhance.backend = (lambda cfg, w, h: m.Backend.TRT(fp16=True)) if kind == "trt" else (lambda cfg, w, h: m.Backend.NCNN_VK(fp16=True))


backend_override("trt")

# ---------------------------------------------------------------- the rules
check(enhance.settings({}) is None and enhance.settings({"enhance": {"preset": "off"}}) is None, "off unless a preset is chosen")
s = enhance.settings({"enhance": {"preset": "film", "height": 2160, "clean": "nonsense", "detail": 5}})
check(s["clean"] == "light" and s["detail"] == 1.0 and s["grain"] == 0, f"settings cleaned up: {s}")
check(enhance.output_size(1920, 1080, 2160) == (3840, 2160), "1080p to 4K")
check(enhance.output_size(1440, 1080, 2160) == (2880, 2160), "4:3 keeps its shape")
check(enhance.output_size(1920, 800, 2160) == (5184, 2160), "a wide film is sized by height")
check(enhance.output_size(3840, 2160, 1080) == (3840, 2160), "never smaller than the video")
check(enhance.output_size(1280, 720, 0) == (1280, 720), "0: the video's own size")


def plan(preset, clean, w, h, height):
    p, k = enhance.model_plan({"preset": preset, "clean": clean, "height": height}, w, h, MODELS)
    return os.path.basename(p), k


check(plan("film", "off", 1920, 1080, 2160) == ("realesr-general-wdn-x4v3.onnx", 4), "film, no clean-up: keeps grain (wdn)")
check(plan("film", "strong", 1920, 1080, 2160) == ("realesr-general-x4v3.onnx", 4), "film, strong clean-up: x4v3")
check(plan("anime", "light", 1920, 1080, 2160) == ("pro-conservative-up2x.onnx", 2), "anime 2x: Real-CUGAN Pro")
check(plan("anime", "light", 640, 360, 2160) == ("realesr-animevideov3.onnx", 4), "anime 6x: AnimeVideo v3 at 4x")
os.remove(os.path.join(MODELS, "cugan", "pro-conservative-up2x.onnx"))
check(plan("anime", "light", 1920, 1080, 2160) == ("up2x-latest-conservative.onnx", 2), "anime without Pro: Real-CUGAN 1")


# ---------------------------------------------------------------- the whole export chain
def run(cfg, fps=24.0, w=640, h=360, frames=24, fmt=vs.YUV420P8):
    d = tempfile.mkdtemp()
    p = os.path.join(d, "engine.json")
    json.dump(cfg, open(p, "w"))
    os.environ["NOVAMOTION_ENGINE_CONFIG"] = p
    base = core.std.BlankClip(width=w, height=h, format=fmt, length=frames, fpsnum=int(fps), fpsden=1)
    clip = core.std.FrameEval(base, lambda n: core.std.BlankClip(base, color=[(n * 7) % 200 + 20, 128, 128], length=frames))
    g = {"video_in": clip, "container_fps": fps, "display_fps": 0, "__file__": SCRIPT}
    vs.clear_outputs()
    exec(compile(open(SCRIPT).read(), SCRIPT, "exec"), g)
    out = vs.get_output(0)
    out = out.clip if hasattr(out, "clip") else out
    for i in (0, 1, out.num_frames // 2):
        out.get_frame(i)
    return out


calls.clear()
o = run({"engine": "mvtools", "target": 60, "source_frames": 24, "enhance": {"preset": "film", "height": 1080, "clean": "light"}})
check((o.width, o.height) == (1920, 1080) and o.format.id == vs.YUV420P8, f"export: 360p film to 1080p, same format ({o.width}x{o.height} {o.format.name})")
check(o.fps.numerator == 60, f"export: still smoothed to 60 ({o.fps})")
check(any(c[0] == "inference" and c[1] == "realesr-general-mid-x4v3.onnx" and c[2] == "RGBH" for c in calls),
      f"export: the light model on half floats for TensorRT ({calls[:1]})")

check(enhance.settings({"enhance": {"preset": "film"}})["strength"] == "balanced" and enhance.MIX["film"]["balanced"] == 0.5,
      "film: half AI, half the original by default (no painted look)")
o = run({"engine": "mvtools", "target": 60, "source_frames": 24, "enhance": {"preset": "film", "height": 0, "clean": "strong"}})
check((o.width, o.height) == (640, 360), f"export: at the video's own size the model's result is scaled back ({o.width}x{o.height})")

calls.clear()
o = run({"engine": "mvtools", "target": 60, "source_frames": 24, "enhance": {"preset": "anime", "height": 1440, "fix_compression": True}},
        w=1000, h=562)
check(("dpir", 1000, 568) in calls, f"export: compression fixed at the video's size, padded to 8 ({[c for c in calls if c[0] == 'dpir']})")
check((o.width, o.height) == (2562, 1440), f"export: anime 562p to 1440p ({o.width}x{o.height})")

o = run({"engine": "mvtools", "target": 60, "source_frames": 24, "enabled": False, "enhance": {"preset": "film", "height": 720}})
check((o.width, o.height) == (1280, 720) and o.fps.numerator == 24, f"export: upscale only, no smoothing ({o.fps})")

o = run({"engine": "mvtools", "target": 60, "source_frames": 24, "demo": True, "demo_labels": True,
         "enhance": {"preset": "film", "height": 2160}}, w=1920, h=1080)
check((o.width, o.height) == (1920 + 16, 1080), f"before/after: full-size crops of the middle, side by side ({o.width}x{o.height})")
check(o.fps.numerator == 60, "before/after: at the smoothed rate")

o = run({"engine": "mvtools", "target": 60, "source_frames": 24, "enhance": {"preset": "film", "height": 720, "detail": 1, "grain": 0.5}})
check((o.width, o.height) == (1280, 720), "detail and grain keep the size")

backend_override("ncnn")
calls.clear()
o = run({"engine": "mvtools", "target": 60, "source_frames": 24, "enhance": {"preset": "film", "height": 720}})
check(any(c[0] == "inference" and c[2] == "RGBS" for c in calls), "Vulkan (ncnn): full floats")

calls.clear()
o = run({"engine": "mvtools", "target": 60, "source_frames": 24, "enhance": {"preset": "film", "height": 2160}},
        w=1920, h=1080)
tiled = [c for c in calls if c[0] == "inference"]
check(tiled and tiled[0][3] == (640, 384), f"every video: one fixed tile size, so one engine per model ({tiled[:1]})")
calls.clear()
o = run({"engine": "mvtools", "target": 60, "source_frames": 24, "enhance": {"preset": "film", "height": 1080}},
        w=480, h=270)
small = [c for c in calls if c[0] == "inference"]
check(small and small[0][3] == (640, 384) and small[0][5:] == (640, 384) and (o.width, o.height) == (1920, 1080),
      f"a picture smaller than a tile: padded to it, so the one engine serves it; padding cropped off ({small[:1]}, {o.width}x{o.height})")
calls.clear()
o = run({"engine": "mvtools", "target": 60, "source_frames": 12, "enhance": {"preset": "film", "height": 1280}}, w=480, h=640, frames=12)
fed = [c for c in calls if c[0] == "inference"]
check(fed and fed[0][3] == (640, 384) and (o.width, o.height) == (960, 1280),
      f"a 480 x 640 portrait clip: the same tile, finished at 960 x 1280 ({fed[:1]}, {o.width}x{o.height})")
check(enhance.tiles(1920, 1080) == ((640, 384), (16, 16)) and enhance.tiles(640, 1080) == ((640, 384), (0, 16)),
      "tiles overlap only where the picture is split")

# A source already at (or above) the chosen size: the model works at half the finished size
check(enhance.ai_input_size(3840, 2160, 3840, 2160) == (1920, 1080), "4K to 4K: the model works at 1080p")
check(enhance.ai_input_size(1920, 1080, 3840, 2160) == (1920, 1080), "1080p to 4K: the model works at the video's size")
check(enhance.ai_input_size(1280, 720, 3840, 2160) == (1280, 720), "720p to 4K: the video's size")
calls.clear()
o = run({"engine": "mvtools", "target": 60, "source_frames": 24, "enhance": {"preset": "film", "height": 1440}},
        w=2560, h=1440, frames=12)
fed = [c for c in calls if c[0] == "inference"]
check((o.width, o.height) == (2560, 1440) and fed and fed[0][3] == (640, 384), f"1440p at 1440p: finished at 1440p, the model fed half size ({fed[:1]})")

# Previews enhance only the part they show, and it comes out as in the whole frame
calls.clear()
e = {"preset": "film", "height": 2160, "detail": 0.7, "grain": 0, "fix_compression": True}
prev = run({"engine": "mvtools", "target": 60, "source_frames": 12, "demo": True, "demo_mode": "zoom", "enhance": e}, w=1920, h=1080, frames=12)
fed = [c for c in calls if c[0] == "inference"]
fixed = [c for c in calls if c[0] == "dpir"]
check(fed and fed[0][5] < 1920 // 2 and fed[0][6] < 1080 * 2 // 3 and fixed and fixed[0][1] < 1920,
      f"preview: only the part it shows is cleaned and enlarged ({fed[:1]}, {fixed[:1]})")
whole = run({"engine": "mvtools", "target": 60, "source_frames": 12, "enhance": e}, w=1920, h=1080, frames=12)
x, y, cw, ch = enhance.demo_view(whole.width, whole.height)
a = core.std.Crop(whole, left=x, right=whole.width - x - cw, top=y, bottom=whole.height - y - ch)
b = core.std.Crop(prev, left=prev.width - cw, right=0)
diff = core.std.PlaneStats(a[5], b[5]).get_frame(0).props["PlaneStatsDiff"]
check((a.width, a.height) == (b.width, b.height) and diff < 0.002,
      f"preview: the enhanced part matches the same part of a full export (difference {diff:.5f})")

# Compare: split (the app's default) shows the whole frame at the finished size, the right half enhanced
calls.clear()
split = run({"engine": "mvtools", "target": 60, "source_frames": 12, "demo": True, "demo_mode": "split", "enhance": e}, w=1920, h=1080, frames=12)
fed = [c for c in calls if c[0] == "inference"]
check((split.width, split.height) == (whole.width, whole.height) and fed and fed[0][5] < 1920 * 2 // 3,
      f"split: the whole frame at its finished size, only the right half (and a margin) enhanced ({split.width}x{split.height}, {fed[:1]})")
q = whole.width * 3 // 4
a = core.std.Crop(whole, left=q, right=0)
b = core.std.Crop(split, left=q, right=0)
diff = core.std.PlaneStats(a[5], b[5]).get_frame(0).props["PlaneStatsDiff"]
check(diff < 0.002, f"split: its right side is the full export's (difference {diff:.5f})")
side = run({"engine": "mvtools", "target": 60, "source_frames": 12, "demo": True, "demo_mode": "whole", "enhance": e}, w=1920, h=1080, frames=12)
check(side.width <= 7680 and side.width >= 7600 and side.height >= 2100,
      f"side by side: both whole frames, within 7680 px ({side.width}x{side.height})")

# Frame preview: one source frame, enhanced, as the before/after crop
o = run({"engine": "mvtools", "target": 60, "source_frames": 1, "enabled": False, "demo": True, "demo_labels": True,
         "demo_mode": "zoom", "enhance": {"preset": "film", "height": 2160}}, w=1920, h=1080)
check(o.num_frames >= 1 and (o.width, o.height) == (1936, 1080) and o.fps.numerator == 24,
      f"frame preview: the before/after crop of an unsmoothed frame ({o.width}x{o.height}, {o.fps})")

calls.clear()
o = run({"engine": "mvtools", "target": 60, "source_frames": 12, "enhance": {"preset": "film", "height": 2160, "fix_compression": True}},
        w=2560, h=1600, frames=12)
check(not [c for c in calls if c[0] == "dpir"], "fix compression: skipped above 1440p (no blocks worth it, far too slow)")
check(enhance.tiles(1920, 1080, tile=enhance.DPIR_TILE) == ((384, 256), (16, 16)), "fix compression: small tiles, so its build fits 4 GB cards")

# Live playback never runs Enhance
os.environ["NOVAMOTION_ENGINE_CONFIG"] = os.path.join(tempfile.mkdtemp(), "e.json")
json.dump({"engine": "mvtools", "target": 60, "enhance": {"preset": "film", "height": 2160}}, open(os.environ["NOVAMOTION_ENGINE_CONFIG"], "w"))
base = core.std.BlankClip(width=640, height=360, format=vs.YUV420P8, length=24, fpsnum=24)
g = {"video_in": base, "container_fps": 24.0, "display_fps": 60.0, "__file__": SCRIPT}
vs.clear_outputs()
exec(compile(open(SCRIPT).read(), SCRIPT, "exec"), g)
live = vs.get_output(0)
live = live.clip if hasattr(live, "clip") else live
check((live.width, live.height) == (640, 360), "live playback: no Enhance")

print()
print("ALL PASSED" if not fails else f"{fails} FAILED")
sys.exit(1 if fails else 0)
