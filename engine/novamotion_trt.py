# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Novabit
"""RIFE on NVIDIA TensorRT (via vs-mlrt) for NovaMotion.

Shared by the player script (novamotion.vpy) and the setup prebuild step, so both
ask TensorRT for exactly the same engine. TensorRT engines are tied to the GPU,
the resolution and these options; a mismatch means a multi-minute rebuild on play.
"""
import math
import os
import re
import sys
import time
from fractions import Fraction

import vapoursynth as vs

core = vs.core

HERE = os.path.dirname(os.path.abspath(__file__))
ENGINE_DIR = os.path.join(HERE, "trt-engines")

# RIFE v4.x needs frame sizes that are multiples of 64; pad, interpolate, crop back.
PAD = 64


def available():
    """True when the TensorRT plugin and vsmlrt.py are installed."""
    if not hasattr(core, "trt"):
        return False
    try:
        import vsmlrt  # noqa: F401
    except ImportError:
        return False
    return True


def model_enum(name):
    """'rife-v4.22_lite_ensembleFalse' -> RIFEModel.v4_22_lite (falls back to v4.6)."""
    from vsmlrt import RIFEModel
    m = re.search(r"v4[._](\d+)(?:[-_](lite|heavy))?", name or "")
    if m:
        attr = f"v4_{m.group(1)}" + (f"_{m.group(2)}" if m.group(2) else "")
        if hasattr(RIFEModel, attr):
            return getattr(RIFEModel, attr)
    return RIFEModel.v4_6


def installed_model(name):
    """Map a model name to one whose ONNX file is installed (TensorRT has fewer models than ncnn)."""
    import vsmlrt
    wanted = model_enum(name)
    rife_dir = os.path.join(vsmlrt.models_path, "rife")

    def onnx_for(model):
        n = model.name                     # e.g. v4_22_lite
        ver = "v" + n[1:].replace("_", ".", 1).replace("_lite", "_lite").replace("_heavy", "_heavy")
        return os.path.join(rife_dir, f"rife_{ver}.onnx")

    if os.path.isfile(onnx_for(wanted)):
        return wanted
    from vsmlrt import RIFEModel
    return RIFEModel.v4_6                  # always installed by setup


def backend(gpu=0):
    from vsmlrt import Backend
    os.makedirs(ENGINE_DIR, exist_ok=True)
    return Backend.TRT(
        fp16=True,               # half precision: ~2x faster on RTX, visually identical for RIFE
        device_id=int(gpu),
        num_streams=2,           # overlap upload / inference / download
        use_cuda_graph=True,
        engine_folder=ENGINE_DIR,
    )


def _log(msg, t0):
    line = f"NovaMotion TRT: {msg} (+{time.perf_counter() - t0:.1f}s)"
    path = os.environ.get("NOVAMOTION_DEBUG_LOG")
    try:
        if path:      # inside mpv stderr goes nowhere
            with open(path, "a", encoding="utf-8") as f:
                f.write(time.strftime("%H:%M:%S ") + line + "\n")
        elif sys.stderr:
            print(line, file=sys.stderr, flush=True)
    except OSError:
        pass


# BlankClip lengths must stay inside a signed 32-bit int
MAX_FRAMES = 2**31 - 2


# Mean absolute difference (0..1) between a frame and the one before it that counts as a cut.
# Measured on a small grey copy, so it costs next to nothing beside RIFE.
SCENE_THRESHOLD = 0.12


def scene_changes(clip):
    """Clip whose frame k carries _SceneChangePrev=1 when k starts a new shot."""
    small = core.resize.Bilinear(clip, width=160, height=96, format=vs.GRAY8, matrix_s="709")
    prev = small[0] + small[:-1] if small.num_frames > 1 else small
    stats = core.std.PlaneStats(small, prev)

    def mark(n, f):
        out = f.copy()
        out.props["_SceneChangePrev"] = 1 if n > 0 and f.props["PlaneStatsDiff"] > SCENE_THRESHOLD else 0
        return out

    return core.std.ModifyFrame(stats, stats, mark)


def record_engine(width, height, model):
    """Notes in ENGINE_DIR that an engine for this padded size exists (the app shows which are ready)."""
    import json
    path = os.path.join(ENGINE_DIR, "novamotion-engines.json")
    try:
        data = json.load(open(path, encoding="utf-8")) if os.path.isfile(path) else {}
        key = f"{width}x{height}"
        if key in data and data[key].get("model") == model:
            return
        data[key] = {"model": model, "built": time.strftime("%Y-%m-%dT%H:%M:%S")}
        tmp = path + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(data, f, indent=2)
        os.replace(tmp, path)
    except (OSError, ValueError):
        pass


def interpolate(rgbh, src, dst, model_name, gpu=0, scene_detect=False):
    """rgbh: RGBH clip at src fps. Returns RGBH at dst fps (same size).

    Built on vsmlrt.RIFEMerge rather than vsmlrt.RIFE: RIFE() precomputes a table entry for
    every output frame in Python, which never finishes inside mpv (the player's clip has an
    effectively unbounded length). Here each frame's source pair and time point are
    derived on demand, so setup is instant at any length.
    """
    from vsmlrt import RIFEMerge
    t0 = time.perf_counter()
    w, h = rgbh.width, rgbh.height
    pw, ph = (-w) % PAD, (-h) % PAD
    clip = core.std.AddBorders(rgbh, right=pw, bottom=ph) if (pw or ph) else rgbh

    # Output frame n sits at source position n*q/p: between frames floor(n*q/p) and the next,
    # at time point (n*q mod p)/p. The pattern repeats every p output frames.
    ratio = Fraction(dst) / Fraction(src)
    p, q = ratio.numerator, ratio.denominator
    model = installed_model(model_name)
    last = clip.num_frames - 1
    length = min(clip.num_frames * p // q, MAX_FRAMES)
    _log(f"model {model.name}, {clip.width}x{clip.height}, x{ratio} ({p} out per {q} in), {length} frames", t0)

    base = core.std.BlankClip(clip, length=length, fpsnum=dst.numerator, fpsden=dst.denominator, keep=True)
    left = core.std.FrameEval(base, lambda n: clip[min(n * q // p, last)])
    right = core.std.FrameEval(base, lambda n: clip[min(n * q // p + 1, last)])

    gray = vs.GRAYH if clip.format.bits_per_sample == 16 else vs.GRAYS
    gray_base = core.std.BlankClip(base, format=gray, keep=True)
    phases = [core.std.BlankClip(gray_base, color=(k / p), keep=True) for k in range(p)]
    mask = core.std.FrameEval(gray_base, lambda n: phases[(n * q) % p])

    _log("loading TensorRT engine (vsmlrt.RIFEMerge)", t0)
    merged = RIFEMerge(left, right, mask, model=model, backend=backend(gpu))
    record_engine(clip.width, clip.height, model.name)   # RIFEMerge has built or loaded it by now
    # vs-mlrt returns 32-bit float frames even from a 16-bit input, so copied source frames
    # must be converted to the same format before the two can be mixed in one clip
    src_same = core.resize.Point(clip, format=merged.format.id) if clip.format.id != merged.format.id else clip
    left = core.std.FrameEval(merged, lambda n: src_same[min(n * q // p, last)])

    if scene_detect:
        # Across a cut there is no motion to follow: blending two shots gives a ghosted frame.
        # Show the nearer real frame instead (cuts stay cuts).
        right_same = core.std.FrameEval(merged, lambda n: src_same[min(n * q // p + 1, last)])
        cuts = scene_changes(clip)
        cut_base = core.std.BlankClip(cuts, length=length, fpsnum=dst.numerator, fpsden=dst.denominator, keep=True)
        cut_at = core.std.FrameEval(cut_base, lambda n: cuts[min(n * q // p + 1, last)])

        def pick(n, f):
            phase = (n * q) % p
            if phase == 0:
                return left
            if f.props.get("_SceneChangePrev", 0):
                return left if phase * 2 < p else right_same
            return merged

        out = core.std.FrameEval(merged, pick, prop_src=cut_at)
    else:
        # Frames that land exactly on a source frame (time point 0) are copied, not inferred
        out = core.std.FrameEval(merged, lambda n: left if (n * q) % p == 0 else merged)
    _log("engine ready", t0)
    out = core.std.AssumeFPS(out, fpsnum=dst.numerator, fpsden=dst.denominator)
    # Copied source frames keep the source's duration; give every frame the output's
    out = core.std.SetFrameProps(out, _DurationNum=dst.denominator, _DurationDen=dst.numerator)
    return core.std.Crop(out, right=pw, bottom=ph) if (pw or ph) else out
