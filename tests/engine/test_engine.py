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
o = run({"engine": "mvtools", "target": "display"}, fps=25.33783783783784)
check(o.fps.numerator == 60 and o.fps.denominator == 1 and abs(o.num_frames - 48 * 60 / 25) < 2,
      f"a slightly-off container rate (25.34) is taken as 25: {o.fps} {o.num_frames}f")
o = run({"engine": "mvtools", "target": "display"}, fps=29.97)
check(abs(o.fps.numerator / o.fps.denominator - 60) < 0.1, f"29.97 still exact: {o.fps}")
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
    # like the real vs-mlrt: 32-bit float out unless asked for half floats
    return core.resize.Point(clipa, format=vs.RGBH if (backend or {}).get("output_format") == 1 else vs.RGBS)
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
o = run({"engine": "rife_trt", "target": "display", "trt_model": "rife-v4.22_lite"}, frames=10)
m = [c for c in calls if c[0] == "merge"]
check(o.fps.numerator == 60 and o.width == 1280 and o.height == 720 and o.format.id == vs.YUV420P8,
      f"trt: 24000/1001 -> 60, padded & cropped back: {o.width}x{o.height} {o.fps} {o.format.name}")
check(m and m[0][1:3] == (1280, 768) and m[0][4] == RIFEModel.v4_22_lite and m[0][5] == "RGBH" and m[0][6] == "GrayH",
      f"trt: RIFEMerge gets padded RGBH pairs + GrayH time mask, model from settings: {m[:1]}")
b = [c for c in calls if c[0] == "backend"][0][1]
check(b["fp16"] and b["output_format"] == 1 and b["num_streams"] == 2 and b["engine_folder"] == novamotion_trt.ENGINE_DIR,
      "trt: fp16 in and out, 2 streams by default, cached engines")

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
# Fast, even motion (a pan, a sports broadcast) must not count as a cut: every frame differs a lot
pan = core.std.BlankClip(width=320, height=180, format=vs.RGBH, length=12, fpsnum=25)
pan = core.std.FrameEval(pan, lambda n: core.std.BlankClip(pan, color=[0.2 + 0.3 * (n % 2)] * 3))
marks = [novamotion_trt.scene_changes(pan).get_frame(n).props["_SceneChangePrev"] for n in range(12)]
check(sum(marks) == 0, f"scene cuts: fast even motion is not a cut ({marks})")
marks = [novamotion_trt.scene_changes(cutclip).get_frame(n).props["_SceneChangePrev"] for n in range(10)]
check(marks == [0, 0, 0, 0, 0, 1, 0, 0, 0, 0], f"scene cuts: a real cut is found exactly once ({marks})")

# Repeated frames: 24 -> 48 (x2) over a ball moving one step per frame, except that frame 3
# repeats frame 2 (as in many sports uploads). A blending stand-in for RIFE shows the effect.
def blend_merge(clipa, clipb, mask, model=None, backend=None, **kw):
    a = core.resize.Point(clipa, format=vs.RGBS); b = core.resize.Point(clipb, format=vs.RGBS)
    m = core.resize.Point(mask, format=vs.GRAYS)
    return core.std.MaskedMerge(a, b, m, first_plane=True)
real_merge = fake.RIFEMerge
fake.RIFEMerge = blend_merge
pos = [0.0, 0.1, 0.2, 0.2, 0.4, 0.5, 0.6, 0.7]            # frame 3 repeats frame 2
moving = core.std.BlankClip(width=128, height=64, format=vs.RGBH, length=len(pos), fpsnum=24)
moving = core.std.FrameEval(moving, lambda n: core.std.BlankClip(moving, color=[pos[n]] * 3))
def track(clip, n):
    return round(float(clip.get_frame(n)[0][0, 0]), 3)
plain = novamotion_trt.interpolate(moving, Fraction(24), Fraction(48), "rife-v4.6", 0, scene_detect=False, dedup=False)
fixed = novamotion_trt.interpolate(moving, Fraction(24), Fraction(48), "rife-v4.6", 0, scene_detect=False, dedup=True)
p_ = [track(plain, n) for n in range(4, 10)]
f_ = [track(fixed, n) for n in range(4, 10)]
steps = lambda xs: [round(b - a, 3) for a, b in zip(xs, xs[1:])]
check(0.0 in steps(p_), f"dedup: without it the motion stops at the repeated frame ({p_})")
check(all(abs(st - 0.05) < 0.002 for st in steps(f_)), f"dedup: with it the motion keeps an even pace ({f_}, steps {steps(f_)})")

# A small object moving over a still background is motion, not a repeat (cricket: still camera, moving ball)
def dot_frame(n, f):
    out = f.copy(); pl = out[0]
    for y in range(40, 52):
        for x in range(60 + 12 * n, 72 + 12 * n):
            pl[y, x] = 0.9
    return out
field = core.std.BlankClip(width=640, height=360, format=vs.GRAYH, length=8, fpsnum=24, color=[0.3])
field = core.std.ModifyFrame(field, field, dot_frame)
field = core.resize.Point(field, format=vs.RGBH)
flags = [novamotion_trt.frame_info(field).get_frame(n).props["Dup0"] for n in range(8)]
check(sum(flags) == 0, f"dedup: a small moving ball on a still background is not a repeat ({flags})")
twice = field[0:3] + field[2] + field[3:7]                  # frame 3 repeats frame 2
flags = [novamotion_trt.frame_info(twice).get_frame(n).props["Dup0"] for n in range(8)]
check(flags == [0, 0, 0, 1, 0, 0, 0, 0], f"dedup: the repeated frame is still found ({flags})")
fake.RIFEMerge = real_merge

import json as _json
man = os.path.join(novamotion_trt.ENGINE_DIR, "novamotion-engines.json")
check(os.path.isfile(man) and "320x192" in _json.load(open(man)) and "1280x768" in _json.load(open(man)),
      f"trt: engine manifest records each padded size ({sorted(_json.load(open(man))) if os.path.isfile(man) else 'missing'})")

# Choosing the model from this PC's measurements (tuning.json)
ch = novamotion_trt.choose_model
fast_pc = {"sizes": {"1920x1088": {"v4_6": 110.0, "v4_22_lite": 170.0}}}
slow_pc = {"sizes": {"1920x1088": {"v4_6": 62.0, "v4_22_lite": 95.0}}}
check(ch("auto", 1920, 1088, Fraction(25), Fraction(60), 0, {})[0] == "v4_6", "tune: without measurements the default model is used")
check(ch("auto", 1920, 1088, Fraction(25), Fraction(60), 0, fast_pc)[0] == "v4_6", "tune: a fast PC gets the full model at 1080p")
check(ch("auto", 1920, 1088, Fraction(25), Fraction(60), 0, slow_pc)[0] == "v4_22_lite", "tune: a PC that can't keep up at 25 -> 60 gets the lite model")
check(ch("auto", 1280, 768, Fraction(25), Fraction(60), 0, slow_pc)[0] == "v4_6", "tune: the same PC keeps the full model at 720p (fewer pixels)")
check(ch("auto", 1920, 1088, Fraction(25), Fraction(60), 1, fast_pc)[0] == "v4_22_lite", "tune: a step down from the player picks the next lighter model")
check(ch("auto", 1920, 1088, Fraction(25), Fraction(60), 0, {}, gpu_class="entry")[0] == novamotion_trt.installed_models()[-1],
      "tune: an untuned GTX-class card starts from the lightest installed model")
check(ch("rife-v4.22_lite", 1920, 1088, Fraction(25), Fraction(60), 0, fast_pc)[0] == "v4_22_lite", "tune: a model chosen in settings wins")
check(abs(novamotion_trt.inferences_per_second(Fraction(25), Fraction(60)) - 55) < 1e-9
      and abs(novamotion_trt.inferences_per_second(Fraction(24), Fraction(60)) - 48) < 1e-9, "tune: 25 -> 60 needs 55 AI frames a second, 24 -> 60 needs 48")
os.environ["NOVAMOTION_ENGINE_STATE"] = os.path.join(tempfile.mkdtemp(), "engine-state.json")
o = run({"engine": "rife_trt", "target": "display"}, frames=10)
st = json.load(open(os.environ["NOVAMOTION_ENGINE_STATE"]))
check(st.get("model") == "v4_6" and st.get("engine") == "rife_trt", f"trt: engine state written for the app ({st})")

calls.clear()
o = run({"engine": "rife_trt", "target": "display", "trt_model": "rife-v4.26"}, frames=10)
m = [c for c in calls if c[0] == "merge"]
check(m and m[0][4] == RIFEModel.v4_6, f"trt: model without ONNX (4.26) falls back to 4.6")
novamotion_trt.available = lambda: False
o = run({"engine": "rife_trt", "target": "display"}, frames=10)
check(o.fps.numerator == 60, "trt missing -> falls back (Vulkan RIFE / MVTools)")
print("ALL PASSED" if not globals().get("fails") else f"{fails} FAILED")
sys.exit(1 if globals().get("fails") else 0)
