# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Novabit
"""Engine tests for engine/novamotion.vpy, no GPU needed.

Runs the real script on synthetic clips with real VapourSynth + MVTools, and swaps the
GPU-only parts (RIFE ncnn, vs-mlrt TensorRT) for stand-ins that behave like them
(frame counts, formats, padding). Covers frame-rate maths, display capping, the
fallback chain, demo mode, and mpv's unbounded clip length.

    pip install vapoursynth vapoursynth-mvtools
    python tests/engine/test_engine.py engine/novamotion.vpy
"""
import json, os, sys, tempfile
from fractions import Fraction
import vapoursynth as vs
core = vs.core
SCRIPT = sys.argv[1]
src_text = open(SCRIPT).read()

def run(cfg, fps=23.976, display=60.0, fmt=vs.YUV420P8, w=1280, h=720, frames=48):
    d = tempfile.mkdtemp()
    p = os.path.join(d, "engine.json"); json.dump(cfg, open(p, "w"))
    os.environ["NOVAMOTION_ENGINE_CONFIG"] = p
    # moving gradient so motion search has something to track
    base = core.std.BlankClip(width=w, height=h, format=fmt, length=frames, fpsnum=24000, fpsden=1001)
    clip = core.std.FrameEval(base, lambda n: core.std.BlankClip(base, color=[(n*5) % 200 + 20, 128, 128], length=frames))
    g = {"video_in": clip, "container_fps": fps, "display_fps": display, "__file__": SCRIPT}
    vs.clear_outputs()
    exec(compile(src_text, SCRIPT, "exec"), g)
    out = vs.get_output(0)
    out = out.clip if hasattr(out, "clip") else out
    for i in (0, 1, out.num_frames // 2, out.num_frames - 1):
        out.get_frame(i)
    return out

def check(ok, what):
    print(("PASS " if ok else "FAIL ") + what); globals().setdefault("fails", 0)
    if not ok: globals()["fails"] += 1

o = run({"engine": "mvtools", "target": "display"})
check(o.fps == vs.core.std.BlankClip(fpsnum=60).fps and abs(o.num_frames - 48*60/23.976) < 2, f"mvtools 23.976 -> display 60: {o.fps} {o.num_frames}f")
o = run({"engine": "mvtools", "target": "x2"})
check(o.fps.numerator/o.fps.denominator - 47.952 < 0.01 and o.num_frames in (95, 96), f"x2: {o.fps} {o.num_frames}f")
o = run({"engine": "mvtools", "target": 120, "mv_quality": "quality"}, display=120)
check(o.fps.numerator == 120, f"target 120 on 120 Hz: {o.fps}")
o = run({"engine": "mvtools", "target": 120}, display=60)
check(o.fps.numerator == 60, f"target 120 on 60 Hz capped to 60: {o.fps}")
o = run({"engine": "mvtools", "target": "x3"}, display=60)
check(o.fps.numerator == 60, f"x3 on 60 Hz capped to 60 (was the 75 fps waste): {o.fps}")
o = run({"engine": "mvtools", "target": "x2"}, display=0)
check(o.fps.numerator == 50 and o.fps.denominator == 1 or abs(o.fps.numerator/o.fps.denominator - 47.952) < 0.01, f"x2 with unknown display uncapped: {o.fps}")
o = run({"engine": "mvtools", "target": "display"}, fps=59.94)
check(abs(o.fps.numerator/o.fps.denominator - 59.94) < 0.01, f"60fps source passes through: {o.fps}")
o = run({"engine": "mvtools", "target": "display"}, display=59.94)
check(o.fps.numerator == 60000 and o.fps.denominator == 1001, f"59.94 Hz display -> 60000/1001: {o.fps}")
o = run({"engine": "mvtools", "target": "display"}, display=0)
check(o.fps.numerator == 60, f"unknown display assumes 60: {o.fps}")
o = run({"enabled": False})
check(o.num_frames == 48, "disabled = passthrough")
o = run({"engine": "rife", "target": "display"})   # no rife plugin here -> falls back
check(o.fps.numerator == 60, "rife missing falls back to mvtools")
o = run({"engine": "mvtools", "target": "display", "demo": True})
check(o.width == 1280 and o.fps.numerator == 60, f"demo side by side: {o.width}x{o.height} {o.fps}")
o = run({"engine": "mvtools", "target": "display", "demo": True, "demo_mode": "split"})
check(o.width == 1280 and o.fps.numerator == 60, f"demo split: {o.width}x{o.height} {o.fps}")
for mode in ("side", "split"):
    o = run({"engine": "mvtools", "target": "display", "demo": True, "demo_mode": mode})
    p_ = o.get_frame(5).props
    check(Fraction(p_["_DurationNum"], p_["_DurationDen"]) == Fraction(1, 60),
          f"demo {mode}: frames last 1/60 s, so the player shows 60 fps (got {p_['_DurationNum']}/{p_['_DurationDen']})")
# Side by side shows the same middle part of the picture twice, with a bright bar between
ramp = core.std.BlankClip(width=640, height=64, format=vs.GRAY8, length=4, fpsnum=24)
def _paint(n, f):                                         # brightness = x / 3: tells columns apart
    out = f.copy(); plane = out[0]
    for y in range(plane.shape[0]):
        for x in range(plane.shape[1]):
            plane[y, x] = x // 3
    return out
ramp = core.std.ModifyFrame(ramp, ramp, _paint)
exec_globals = {}
exec(compile(src_text.split("def build(")[0], SCRIPT, "exec"), exec_globals)
side = exec_globals["demo_split"](ramp, exec_globals["repeat_to_fps"](ramp, Fraction(24), Fraction(48)), Fraction(24), Fraction(48), "side")
row = lambda c, x: c.get_frame(0)[0][10, x]
check(side.width == 640 and abs(row(side, 0) - row(side, 324)) <= 1 and row(side, 320) > 200,
      f"demo side by side: both halves start at the same column ({row(side, 0)} vs {row(side, 324)}), bar between ({row(side, 320)})")
split = exec_globals["demo_split"](ramp, ramp, Fraction(24), Fraction(24), "split")
check(split.width == 640 and row(split, 0) == 0 and row(split, 330) == 330 // 3,
      f"demo split: left half from the left, right half from the right ({row(split, 330)})")
o = run({"engine": "mvtools", "target": "display"}, fmt=vs.YUV420P10, w=3840, h=2160, frames=6)
check(o.format.id == vs.YUV420P10 and o.fps.numerator == 60, "4K 10-bit mvtools")
o = run({"engine": "mvtools", "target": "display"}, w=720, h=480)
check(o.fps.numerator == 60, "SD source")
# --- RIFE path with a stand-in plugin: MVTools fakes RIFE's signature on RGB input
import types
src_rife = src_text.replace('core.rife.RIFE(', 'fake_rife(').replace('hasattr(core, "rife")', 'True')
def fake_rife(rgb, fps_num, fps_den, model_path, **kw):
    assert os.path.isfile(os.path.join(model_path, "flownet.param")), model_path
    y = core.resize.Bicubic(rgb, format=vs.YUV444P16, matrix_s="709")
    sup = core.mv.Super(y); bw = core.mv.Analyse(sup, isb=True); fw = core.mv.Analyse(sup, isb=False)
    out = core.mv.FlowFPS(y, sup, bw, fw, num=fps_num, den=fps_den)
    return core.resize.Bicubic(out, format=vs.RGBS, matrix_in_s="709")
mdir = tempfile.mkdtemp(); os.makedirs(os.path.join(mdir, "models", "rife-v4.22_lite_ensembleFalse"))
open(os.path.join(mdir, "models", "rife-v4.22_lite_ensembleFalse", "flownet.param"), "w").close()
fake_script = os.path.join(mdir, "novamotion.vpy")
def run_rife(cfg, **kw):
    global src_text, SCRIPT
    saved = (src_text, SCRIPT); src_text, SCRIPT = src_rife, fake_script
    try:
        import builtins; builtins.fake_rife = fake_rife
        return run(cfg, **kw)
    finally:
        src_text, SCRIPT = saved
o = run_rife({"engine": "rife", "target": "display", "rife_model": "missing-model"})
check(o.format.id == vs.YUV420P8 and o.fps.numerator == 60, f"rife path: YUV out (mpv rejects RGB), missing model falls back to installed one ({o.format.name} {o.fps})")
o = run_rife({"engine": "rife", "target": "display", "demo": True})
check(o.format.id == vs.YUV420P8 and o.width == 1280, "rife + demo halves stack")
o = run_rife({"engine": "rife", "target": "display/2"}, display=143.856)
check(abs(o.fps.numerator / o.fps.denominator - 71.928) < 0.01, f"display/2 on 144 Hz: {o.fps}")
o = run_rife({"engine": "rife", "target": "display/2"}, display=60)
check(o.fps.numerator == 60, f"display/2 on 60 Hz keeps 60 (half would add nothing): {o.fps}")
o = run_rife({"engine": "rife", "target": "display"}, fmt=vs.YUV420P10, frames=6)
check(o.format.id == vs.YUV420P10, "10-bit source stays 10-bit YUV")

# --- TensorRT path with a fake vs-mlrt (RIFEMerge stand-in returns clipa)
import sys as _sys, types as _types, enum as _enum, time as _time
fake = _types.ModuleType("vsmlrt")
class RIFEModel(_enum.IntEnum):
    v4_6 = 46; v4_22_lite = 4221; v4_26 = 426
fake.RIFEModel = RIFEModel
calls = []
class _Backend:
    @staticmethod
    def TRT(**kw): calls.append(("backend", kw)); return kw
fake.Backend = _Backend
def RIFEMerge(clipa, clipb, mask, model=None, backend=None, **kw):
    calls.append(("merge", clipa.width, clipa.height, clipa.num_frames, model, clipa.format.name, mask.format.name))
    assert clipa.width % 64 == 0 and clipa.height % 64 == 0, "not padded"
    assert clipa.num_frames == clipb.num_frames == mask.num_frames
    # tint by time point so tests can check the phase: out = a*(1-t) + b*t on plane 0
    # like the real vs-mlrt: output is 32-bit float even for 16-bit input
    return core.resize.Point(clipa, format=vs.RGBS)
fake.RIFEMerge = RIFEMerge
fake.models_path = tempfile.mkdtemp()
os.makedirs(os.path.join(fake.models_path, "rife"))
for f in ("rife_v4.6.onnx", "rife_v4.22_lite.onnx"):
    open(os.path.join(fake.models_path, "rife", f), "w").close()
_sys.modules["vsmlrt"] = fake
eng_dir = os.path.dirname(os.path.abspath(SCRIPT))
_sys.path.insert(0, eng_dir)
import novamotion_trt
novamotion_trt.available = lambda: True
novamotion_trt.ENGINE_DIR = tempfile.mkdtemp()

calls.clear()
o = run({"engine": "rife_trt", "target": "display", "rife_model": "rife-v4.22_lite_ensembleFalse"}, frames=10)
m = [c for c in calls if c[0] == "merge"]
check(o.fps.numerator == 60 and o.width == 1280 and o.height == 720 and o.format.id == vs.YUV420P8,
      f"trt: 24000/1001 -> 60, padded & cropped back: {o.width}x{o.height} {o.fps} {o.format.name}")
check(m and m[0][1:3] == (1280, 768) and m[0][4] == RIFEModel.v4_22_lite and m[0][5] == "RGBH" and m[0][6] == "GrayH",
      f"trt: RIFEMerge gets padded RGBH pairs + GrayH time mask, lite model: {m[:1]}")
b = [c for c in calls if c[0] == "backend"][0][1]
check(b["fp16"] and b["num_streams"] == 2 and b["engine_folder"] == novamotion_trt.ENGINE_DIR, "trt: fp16, 2 streams, cached engines")

o = run({"engine": "rife_trt", "target": "display"}, fps=25.0, display=60, frames=60)
durs = {Fraction(o.get_frame(n).props["_DurationNum"], o.get_frame(n).props["_DurationDen"]) for n in range(24)}
check(durs == {Fraction(1, 60)}, f"trt 25 -> 60: every frame lasts 1/60 s, copied ones too ({sorted(durs)})")
o = run({"engine": "mvtools", "target": "display"}, fps=25.0, display=60, frames=60)
durs = {Fraction(o.get_frame(n).props["_DurationNum"], o.get_frame(n).props["_DurationDen"]) for n in range(24)}
check(durs == {Fraction(1, 60)}, f"mvtools 25 -> 60: every frame lasts 1/60 s ({sorted(durs)})")

# The mpv case: a clip of effectively unbounded length must set up instantly
calls.clear()
t0 = _time.perf_counter()
o = run({"engine": "rife_trt", "target": "display"}, fps=25.0, display=60, frames=500_000_000)
dt = _time.perf_counter() - t0
check(dt < 5 and o.fps.numerator == 60, f"trt: unbounded mpv-style clip (500M frames) sets up in {dt:.2f}s")

# Phase maths: 25 -> 60 is 12 out per 5 in; frames 0, 12, 24 land exactly on source frames
r = novamotion_trt.Fraction(60) / novamotion_trt.Fraction(25)
p_, q_ = r.numerator, r.denominator
exact = [n for n in range(36) if (n * q_) % p_ == 0]
check((p_, q_) == (12, 5) and exact == [0, 12, 24], f"trt: 25->60 phases p={p_} q={q_}, exact frames {exact}")

# Scene cuts: frames 0-4 dark, 5-9 bright. At 25 -> 50 each in-between frame sits at t = 0.5;
# the one between frames 4 and 5 crosses the cut, so it must show a real frame, not a blend.
dark = core.std.BlankClip(width=320, height=180, format=vs.RGBH, length=5, fpsnum=25, color=[0.1, 0.1, 0.1])
bright = core.std.BlankClip(dark, color=[0.9, 0.9, 0.9])
cutclip = dark + bright
def luma(c, n):
    return c.get_frame(n)[0][0, 0]
merged_only = novamotion_trt.interpolate(cutclip, novamotion_trt.Fraction(25), novamotion_trt.Fraction(50), "rife-v4.6", 0, scene_detect=False)
with_cuts = novamotion_trt.interpolate(cutclip, novamotion_trt.Fraction(25), novamotion_trt.Fraction(50), "rife-v4.6", 0, scene_detect=True)
check(with_cuts.num_frames == merged_only.num_frames == 20 and with_cuts.format.id == merged_only.format.id,
      f"trt scene cuts: same length and format ({with_cuts.num_frames}, {with_cuts.format.name})")
check(luma(with_cuts, 9) > 0.8 and luma(merged_only, 9) < 0.2,
      f"trt scene cuts: the frame across the cut shows the next shot ({luma(with_cuts, 9):.2f}) instead of the merge ({luma(merged_only, 9):.2f})")
check(all(abs(luma(with_cuts, n) - luma(merged_only, n)) < 1e-3 for n in (1, 3, 5, 7, 11, 13)),
      "trt scene cuts: frames within a shot are untouched")
check(abs(luma(with_cuts, 8) - 0.1) < 0.01 and abs(luma(with_cuts, 10) - 0.9) < 0.01,
      "trt scene cuts: exact source frames still copied")
import json as _json
man = os.path.join(novamotion_trt.ENGINE_DIR, "novamotion-engines.json")
check(os.path.isfile(man) and "320x192" in _json.load(open(man)) and "1280x768" in _json.load(open(man)),
      f"trt: engine manifest records each padded size ({sorted(_json.load(open(man))) if os.path.isfile(man) else 'missing'})")

calls.clear()
o = run({"engine": "rife_trt", "target": "display", "rife_model": "rife-v4.26_ensembleFalse"}, frames=10)
m = [c for c in calls if c[0] == "merge"]
check(m and m[0][4] == RIFEModel.v4_6, f"trt: model without ONNX (4.26) falls back to 4.6")
novamotion_trt.available = lambda: False
o = run({"engine": "rife_trt", "target": "display"}, frames=10)
check(o.fps.numerator == 60, "trt missing -> falls back (Vulkan RIFE / MVTools)")
print("ALL PASSED" if not globals().get("fails") else f"{fails} FAILED")
sys.exit(1 if globals().get("fails") else 0)
