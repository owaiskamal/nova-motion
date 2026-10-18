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


# A cut is a spike in the frame-to-frame difference: big on its own (mean absolute difference
# over 0..1) and several times bigger than the differences just before and after it. A camera
# pan or fast play (sports) changes every frame a lot, but evenly, so it is not mistaken for a
# cut; a flash lights up two neighbouring differences, so it isn't either.
SCENE_THRESHOLD = 0.10
SCENE_SPIKE = 2.5


def scene_changes(clip):
    """Clip whose frame k carries _SceneChangePrev=1 when k starts a new shot."""
    small = core.resize.Bilinear(clip, width=160, height=96, format=vs.GRAY8, matrix_s="709")
    if small.num_frames < 3:
        return core.std.SetFrameProps(small, _SceneChangePrev=0)
    diff = core.std.PlaneStats(small, small[0] + small[:-1])     # frame k against k-1
    before = diff[0] + diff[:-1]                                  # difference k-1 against k-2
    after = diff[1:] + diff[-1]                                   # difference k+1 against k

    def mark(n, f):
        d = f[0].props["PlaneStatsDiff"]
        around = max(f[1].props["PlaneStatsDiff"], f[2].props["PlaneStatsDiff"])
        out = f[0].copy()
        out.props["_SceneChangePrev"] = 1 if n > 0 and d > SCENE_THRESHOLD and d > SCENE_SPIKE * around else 0
        return out

    return core.std.ModifyFrame(diff, [diff, before, after], mark)


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


# Two neighbouring frames closer than this (mean absolute difference, 0..1, on a small grey
# copy) are the same picture shown twice. Compression noise alone stays well below it.
DUP_THRESHOLD = 0.004


def frame_info(clip):
    """Per source frame k: Cut1/Cut2 (k+1 / k+2 starts a new shot), DupM1/Dup0/Dup1/Dup2 (k-1 .. k+2 repeat the frame before)."""
    small = core.resize.Bilinear(clip, width=160, height=96, format=vs.GRAY8, matrix_s="709")
    n = small.num_frames
    if n < 4:
        return core.std.SetFrameProps(small, Cut1=0, Cut2=0, DupM1=0, Dup0=0, Dup1=0, Dup2=0)
    cuts = scene_changes(clip)
    diff = core.std.PlaneStats(small, small[0] + small[:-1])     # frame k against k-1
    d1 = diff[1:] + diff[-1]
    d2 = diff[2:] + diff[-1] + diff[-1]
    dm1 = diff[0] + diff[:-1]
    c1 = cuts[1:] + cuts[-1]
    c2 = cuts[2:] + cuts[-1] + cuts[-1]

    def mark(n, f):
        out = f[0].copy()
        dup = lambda fr, idx: 1 if idx > 0 and fr.props["PlaneStatsDiff"] < DUP_THRESHOLD else 0
        out.props["Dup0"] = dup(f[0], n)
        out.props["Dup1"] = dup(f[1], n + 1)
        out.props["Dup2"] = dup(f[2], n + 2)
        out.props["DupM1"] = dup(f[3], n - 1)
        out.props["Cut1"] = f[4].props.get("_SceneChangePrev", 0)
        out.props["Cut2"] = f[5].props.get("_SceneChangePrev", 0)
        return out

    return core.std.ModifyFrame(diff, [diff, d1, d2, dm1, c1, c2], mark)


def plan(n, p, q, info):
    """Which source frames output frame n is made from, and where between them it sits.

    Returns (a, b, t2, copy): blend frames a and b at time t2 / (2p); copy=True means show
    frame a as it is. Normally a = floor(n*q/p) and b = a + 1. A repeated frame carries no
    motion, so blending across it gives a ball that stops and then jumps; instead the pair is
    widened to skip the repeat and the time point halved to match the longer gap.
    """
    i, k = n * q // p, (n * q) % p
    g = lambda name: info.props.get(name, 0)
    # Only a single repeat inside moving video counts: a run of identical frames is a still
    # scene, and a pair is never widened across a cut
    if g("Dup0") and not g("DupM1") and not g("Dup1") and not g("Cut1"):   # i repeats i-1: blend i-1 .. i+1
        return i - 1, i + 1, p + k, False
    if g("Dup1") and not g("Dup0") and not g("Dup2") and not g("Cut2"):    # i+1 repeats i: blend i .. i+2
        return i, i + 2, k, k == 0
    if g("Cut1") and k:                             # a cut between i and i+1: no blend
        return (i, i, 0, True) if k * 2 < p else (i + 1, i + 1, 0, True)
    return i, i + 1, 2 * k, k == 0


def interpolate(rgbh, src, dst, model_name, gpu=0, scene_detect=False, dedup=True):
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
    gray = vs.GRAYH if clip.format.bits_per_sample == 16 else vs.GRAYS
    gray_base = core.std.BlankClip(base, format=gray, keep=True)
    # Time points are k/(2p): halves of the normal steps, for pairs widened over a repeated frame
    phases = [core.std.BlankClip(gray_base, color=(k / (2 * p)), keep=True) for k in range(2 * p + 1)]
    src_at = lambda idx: clip[min(max(idx, 0), last)]

    if scene_detect or dedup:
        info = frame_info(clip)
        if not scene_detect:
            info = core.std.SetFrameProps(info, Cut1=0, Cut2=0)
        if not dedup:
            info = core.std.SetFrameProps(info, DupM1=0, Dup0=0, Dup1=0, Dup2=0)
        info_base = core.std.BlankClip(info, length=length, fpsnum=dst.numerator, fpsden=dst.denominator, keep=True)
        info_out = core.std.FrameEval(info_base, lambda n: info[min(n * q // p, last)])
        decide = lambda n, f: plan(n, p, q, f)
    else:
        info_out = core.std.BlankClip(gray_base, format=vs.GRAY8, keep=True)
        decide = lambda n, f: (n * q // p, n * q // p + 1, 2 * ((n * q) % p), (n * q) % p == 0)

    left = core.std.FrameEval(base, lambda n, f: src_at(decide(n, f)[0]), prop_src=info_out)
    right = core.std.FrameEval(base, lambda n, f: src_at(decide(n, f)[1]), prop_src=info_out)
    mask = core.std.FrameEval(gray_base, lambda n, f: phases[decide(n, f)[2]], prop_src=info_out)

    _log("loading TensorRT engine (vsmlrt.RIFEMerge)", t0)
    merged = RIFEMerge(left, right, mask, model=model, backend=backend(gpu))
    record_engine(clip.width, clip.height, model.name)   # RIFEMerge has built or loaded it by now
    # vs-mlrt returns 32-bit float frames even from a 16-bit input, so copied source frames
    # must be converted to the same format before the two can be mixed in one clip
    src_same = core.resize.Point(clip, format=merged.format.id) if clip.format.id != merged.format.id else clip

    def pick(n, f):
        a, _, _, copy = decide(n, f)
        # Frames that land on a real frame (or a cut's nearer side) are copied, not inferred
        return src_same[min(max(a, 0), last)] if copy else merged

    out = core.std.FrameEval(merged, pick, prop_src=info_out)
    _log("engine ready", t0)
    out = core.std.AssumeFPS(out, fpsnum=dst.numerator, fpsden=dst.denominator)
    # Copied source frames keep the source's duration; give every frame the output's
    out = core.std.SetFrameProps(out, _DurationNum=dst.denominator, _DurationDen=dst.numerator)
    return core.std.Crop(out, right=pw, bottom=ph) if (pw or ph) else out
