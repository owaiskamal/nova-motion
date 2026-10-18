# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Novabit
# NovaMotion engine: real-time frame interpolation for mpv.
#
# mpv runs this script through --vf=vapoursynth=<this file> and injects:
#   video_in, video_in_dw, video_in_dh, container_fps, display_fps, display_res
#
# Settings come from engine.json (written by the NovaMotion app or the mpv
# hotkeys). Location: $NOVAMOTION_ENGINE_CONFIG, else %APPDATA%\NovaMotion\engine.json.

import json
import os
import sys
from fractions import Fraction

import vapoursynth as vs

core = vs.core

HERE = os.path.dirname(os.path.abspath(globals().get("__file__", "novamotion.vpy")))
MODELS_DIR = os.path.join(HERE, "models")
if HERE not in sys.path:
    sys.path.insert(0, HERE)      # for novamotion_trt.py next to this script

# Diagnostics. Inside mpv there is no console, so stderr goes nowhere: log to a file instead.
# If building the filter takes > 15 s, faulthandler writes every thread's stack to the log.
import faulthandler
import time
_DEBUG_PATH = os.path.join(os.environ.get("APPDATA", HERE), "NovaMotion", "engine-debug.log")
try:
    os.makedirs(os.path.dirname(_DEBUG_PATH), exist_ok=True)
    _debug = open(_DEBUG_PATH, "a", encoding="utf-8", buffering=1)
except OSError:
    _debug = None
os.environ["NOVAMOTION_DEBUG_LOG"] = _DEBUG_PATH
_T0 = time.perf_counter()


def dbg(msg):
    if _debug:
        _debug.write(f"{time.strftime('%H:%M:%S')} +{time.perf_counter() - _T0:5.1f}s  {msg}\n")

DEFAULTS = {
    "enabled": True,
    "engine": "rife",            # rife_trt (NVIDIA TensorRT) | rife (Vulkan) | mvtools
    "target": "display",         # display | display/2 | x2 | <number, e.g. 60 or 120>
    "max_source_fps": 31.0,      # never interpolate content above this rate
    "rife_model": "rife-v4.6_ensembleFalse",
    "rife_gpu": 0,
    "rife_threads": 4,
    "scene_detect": True,
    "mv_quality": "balanced",    # fast | balanced | quality
    "demo": False,               # before/after: original on the left, smoothed on the right
    "demo_mode": "side",         # side: the same middle part of the picture twice | split: left and right halves
}


def load_settings():
    path = os.environ.get("NOVAMOTION_ENGINE_CONFIG") or os.path.join(
        os.environ.get("APPDATA", HERE), "NovaMotion", "engine.json")
    cfg = dict(DEFAULTS)
    try:
        with open(path, "r", encoding="utf-8") as f:
            cfg.update(json.load(f))
    except (OSError, ValueError):
        pass
    return cfg


def as_fraction(fps):
    # Snap to broadcast rates: 23.976 -> 24000/1001, 59.94 -> 60000/1001, 25 -> 25/1
    fps = float(fps)
    whole = round(fps)
    if abs(fps - whole) < 0.005:
        return Fraction(whole)
    ntsc = round(fps * 1.001)
    if abs(fps - ntsc * 1000 / 1001) < 0.005:
        return Fraction(ntsc * 1000, 1001)
    return Fraction(fps).limit_denominator(1001)


def source_fps(clip):
    cfps = globals().get("container_fps") or 0
    if cfps and cfps > 0:
        return as_fraction(cfps)
    if clip.fps_num > 0:
        return Fraction(clip.fps_num, clip.fps_den)
    return Fraction(24000, 1001)


def display_rate():
    dfps = globals().get("display_fps") or 0
    return as_fraction(dfps) if dfps and dfps > 1 else None   # mpv reports 0 until it knows the monitor


def target_fps(cfg, src):
    t = cfg.get("target", "display")
    d = display_rate()
    if t in ("display", "display/2"):
        d = d or Fraction(60)
        # Half the refresh rate keeps an even cadence on 120-240 Hz screens at a fraction of the GPU cost
        return d / 2 if t == "display/2" and d >= 100 else d   # on 60 Hz, half (30) would be worse
    if isinstance(t, str) and t.lower().startswith("x"):
        want = src * int(t[1:] or 2)
    else:
        want = as_fraction(t)
    # Frames above the refresh rate are generated and then thrown away by the player:
    # pure wasted GPU work (measured: x3 on a 60 Hz TV dropped 15 of every 75 frames).
    return min(want, d) if d else want


def matrix_for(clip):
    # Most HD/UHD sources are BT.709; SD is BT.601 (170m)
    return "709" if clip.height >= 600 or clip.width >= 1100 else "170m"


def interpolate_rife_trt(clip, cfg, src, dst):
    import novamotion_trt
    if not novamotion_trt.available():
        raise vs.Error("NovaMotion: TensorRT engine not installed (run setup\\Get-TensorRT.ps1)")
    matrix = matrix_for(clip)
    # Half-float RGB: what the fp16 engine wants, and half the memory traffic of RGBS
    rgb = core.resize.Bicubic(clip, format=vs.RGBH, matrix_in_s=matrix)
    smooth = novamotion_trt.interpolate(rgb, src, dst, cfg["rife_model"], cfg["rife_gpu"], bool(cfg["scene_detect"]))
    return core.resize.Bicubic(smooth, format=clip.format.id, matrix_s=matrix, dither_type="ordered")


def interpolate_rife(clip, cfg, src, dst):
    matrix = matrix_for(clip)
    rgb = core.resize.Bicubic(clip, format=vs.RGBS, matrix_in_s=matrix)
    model_path = os.path.join(MODELS_DIR, cfg["rife_model"])
    if not os.path.isfile(os.path.join(model_path, "flownet.param")):
        # Configured model not installed: use the best one that is (4.6 is the safe default)
        found = sorted(d for d in os.listdir(MODELS_DIR)
                       if os.path.isfile(os.path.join(MODELS_DIR, d, "flownet.param"))) if os.path.isdir(MODELS_DIR) else []
        if not found:
            raise vs.Error("NovaMotion: no RIFE models installed in " + MODELS_DIR)
        model_path = os.path.join(MODELS_DIR, next((d for d in found if d.startswith("rife-v4.6")), found[-1]))
    smooth = core.rife.RIFE(
        rgb,
        fps_num=dst.numerator, fps_den=dst.denominator,
        model_path=model_path,
        gpu_id=int(cfg["rife_gpu"]),
        # Fewer than 4 frames in flight leaves a modern GPU mostly idle (measured: ~32 fps on an RTX 4070 with 2)
        gpu_thread=max(int(cfg["rife_threads"]), 4),
        sc=bool(cfg["scene_detect"]),
        uhd=clip.width > 2560,
    )
    # mpv's VapourSynth bridge only accepts YUV back, so return the source format.
    # Ordered dither is SIMD-fast; error diffusion was costing a CPU core at high frame rates.
    return core.resize.Bicubic(smooth, format=clip.format.id, matrix_s=matrix, dither_type="ordered")


MV_PRESETS = {
    #           pel  blk  search  truemotion
    "fast":     (1,  16,  3,      False),
    "balanced": (2,  16,  3,      True),
    "quality":  (2,  8,   3,      True),
}


def interpolate_mvtools(clip, cfg, src, dst):
    pel, blk, search, truemotion = MV_PRESETS.get(cfg["mv_quality"], MV_PRESETS["balanced"])
    if clip.width > 2560:             # 4K: bigger blocks keep it real-time
        pel, blk = 1, 32
    elif clip.width <= 1024 and blk > 8:
        blk = 8
    sup = core.mv.Super(clip, pel=pel, hpad=blk, vpad=blk)
    common = dict(blksize=blk, overlap=blk // 2, search=search, truemotion=truemotion)
    bw = core.mv.Analyse(sup, isb=True, **common)
    fw = core.mv.Analyse(sup, isb=False, **common)
    return core.mv.FlowFPS(clip, sup, bw, fw, num=dst.numerator, den=dst.denominator,
                           mask=2, ml=100)


def repeat_to_fps(clip, src, dst):
    """Plain frame repetition to dst fps (used for the demo's 'before' half)."""
    length = int(clip.num_frames * dst / src)
    base = core.std.BlankClip(clip, length=length, fpsnum=dst.numerator, fpsden=dst.denominator)
    ratio = src / dst

    def pick(n):
        return clip[min(int(n * ratio), clip.num_frames - 1)]

    return core.std.FrameEval(base, pick)


def divider(like, width=4):
    """A bright vertical bar in the clip's own format, to mark where before ends and after begins."""
    bar = core.std.Crop(like, right=like.width - width)
    fmt = bar.format
    if fmt.sample_type == vs.FLOAT:
        exprs = ["0.95"] if fmt.color_family != vs.YUV else ["0.95", "0"]
    else:
        peak = (1 << fmt.bits_per_sample) - 1
        if fmt.color_family == vs.YUV:
            exprs = [str(235 << (fmt.bits_per_sample - 8)), str(128 << (fmt.bits_per_sample - 8))]
        else:
            exprs = [str(int(peak * 0.95))]
    return core.std.Expr(bar, exprs)


def demo_split(original, smooth, src, dst, mode="side"):
    """Before/after in one picture, at the output frame rate (the original's frames are repeated).

    side:  the middle half of the picture, twice: the same motion side by side, which makes the
           difference obvious (default)
    split: the left half from the original, the right half smoothed (the classic wipe)
    """
    before = repeat_to_fps(original, src, dst)
    if before.format.id != smooth.format.id:   # RIFE hands back RGB; match it so the halves can be stacked
        before = core.resize.Bicubic(before, format=smooth.format.id, matrix_in_s=matrix_for(original))
    w = original.width
    half = (w // 2) & ~3
    bar_w = 4
    if mode == "split":
        left = core.std.Crop(before, right=w - half)
        right = core.std.Crop(smooth, left=half + bar_w)
    else:
        start = ((w - half) // 2) & ~3               # the middle half of the frame
        left = core.std.Crop(before, left=start, right=w - start - half)
        right = core.std.Crop(smooth, left=start, right=w - start - (w - half - bar_w))
    return core.std.StackHorizontal([left, divider(left, bar_w), right])


def build(clip):
    cfg = load_settings()
    dbg(f"--- start: {clip.width}x{clip.height} {clip.format.name} container_fps={globals().get('container_fps')} "
        f"display_fps={globals().get('display_fps')} engine={cfg['engine']} target={cfg['target']} "
        f"host={'mpv' if 'video_in_dw' in globals() else 'other'}")
    src = source_fps(clip)
    clip = core.std.AssumeFPS(clip, fpsnum=src.numerator, fpsden=src.denominator)

    if not cfg.get("enabled", True) or float(src) > float(cfg["max_source_fps"]):
        return clip

    dst = target_fps(cfg, src)
    if float(dst) < float(src) * 1.1:   # nothing meaningful to add
        return clip

    # Fallback chain: TensorRT -> Vulkan RIFE -> MVTools, so playback always works
    chain = {"rife_trt": [interpolate_rife_trt, interpolate_rife],
             "rife": [interpolate_rife]}.get(cfg["engine"], [])
    smooth = None
    for engine in chain:
        if engine is interpolate_rife and not hasattr(core, "rife"):
            continue
        try:
            dbg(f"trying {engine.__name__} {src} -> {dst}")
            smooth = engine(clip, cfg, src, dst)
            dbg(f"{engine.__name__} ready")
            break
        except Exception as e:     # any engine failure must fall through to the next one
            dbg(f"{engine.__name__} unavailable: {type(e).__name__}: {e}")
    if smooth is None:
        smooth = interpolate_mvtools(clip, cfg, src, dst)

    if cfg.get("demo"):
        smooth = demo_split(clip, smooth, src, dst, cfg.get("demo_mode", "side"))
    return smooth


if _debug:
    faulthandler.dump_traceback_later(15, repeat=True, file=_debug)
try:
    build(video_in).set_output()  # noqa: F821  (video_in is injected by mpv)
    dbg("filter built, handing frames to the player")
finally:
    faulthandler.cancel_dump_traceback_later()
