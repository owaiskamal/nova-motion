# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Novabit
"""NOVA Studio Enhance: AI clean-up, upscaling and finishing for exports.

The chain around RIFE in novamotion.vpy:
    fix compression (DPIR, at the video's size)  ->  RIFE  ->  AI upscale (every output frame)
    ->  fit to the chosen size  ->  detail and grain
Upscaling after RIFE puts real and generated frames through the same model, so they come out equally
sharp: no sharpness flicker between them, and RIFE keeps working at the size it is fast and good at.

Models (all allow commercial use), in vs-plugins/models:
    enhance/realesr-general-{x4v3, mid-x4v3, wdn-x4v3}.onnx  Real-ESRGAN general, BSD-3: film and live action
        x4v3 cleans noise hardest, wdn keeps grain and fine texture, mid is half of each
    cugan/up2x-latest-{no-denoise, conservative, denoise3x}.onnx and pro-*  Real-CUGAN, MIT: anime, art
    RealESRGANv2/realesr-animevideov3.onnx  Real-ESRGAN AnimeVideo v3, BSD-3: anime, big enlargements
    dpir/drunet_deblocking_color.onnx  DPIR, MIT: compression blocks and ringing

The "enhance" setting:
    {"preset": "film" | "anime" | "off", "height": 2160 (0: the video's own), "clean": "off" | "light" | "strong",
     "strength": "natural" | "balanced" | "full", "fix_compression": false, "detail": 0..1, "grain": 0..1}
"""
import os

import vapoursynth as vs

core = vs.core

PRESETS = ("film", "anime")
CLEAN = ("off", "light", "strong")
STRENGTHS = ("natural", "balanced", "full")
# How much of the AI's picture is kept, the rest a plain enlargement of the original: on real
# footage the model's full result turns foliage into dark-outlined blobs and water into plastic.
# Half and half kept the texture and still looked clearly sharper (tuned on a phone clip). Anime's
# flat colour and lines suit the model, so it keeps more.
MIX = {"film": {"natural": 0.35, "balanced": 0.5, "full": 1.0},
       "anime": {"natural": 0.6, "balanced": 0.8, "full": 1.0}}

FILM_MODELS = {"off": "realesr-general-wdn-x4v3", "light": "realesr-general-mid-x4v3", "strong": "realesr-general-x4v3"}
ANIME_MODELS = {"off": ("no-denoise", "pro-no-denoise3x"), "light": ("conservative", "pro-conservative"),
                "strong": ("denoise3x", "pro-denoise3x")}


def settings(cfg):
    """The enhance settings with defaults, or None when Enhance is off."""
    e = cfg.get("enhance") or {}
    if not isinstance(e, dict) or e.get("preset", "off") not in PRESETS:
        return None
    out = {"preset": e["preset"], "height": int(e.get("height") or 0), "clean": e.get("clean", "light"),
           "fix_compression": bool(e.get("fix_compression")), "detail": float(e.get("detail") or 0),
           "grain": float(e.get("grain") or 0), "strength": e.get("strength", "balanced")}
    if out["clean"] not in CLEAN:
        out["clean"] = "light"
    if out["strength"] not in STRENGTHS:
        out["strength"] = "balanced"
    out["detail"] = min(1.0, max(0.0, out["detail"]))
    out["grain"] = min(1.0, max(0.0, out["grain"]))
    return out


def output_size(width, height, target_height):
    """The finished size: the video's shape at the chosen height (never smaller), even numbers."""
    if target_height <= 0 or target_height <= height:
        return width, height
    k = target_height / height
    return int(round(width * k / 2)) * 2, int(target_height) // 2 * 2


def ai_input_size(width, height, out_w, out_h):
    """The size the model works at: the video's own, but never more than half the finished size.
    A 4K phone clip exported at 4K ran the 4x model on whole 4K frames (about 40 tiles each, into
    a 16K picture shrunk back): 40+ minutes for 4 seconds. At half size the model still has room
    to add detail, and the mix with the original keeps the real 4K detail."""
    if height * 2 <= out_h * 1.01:
        return width, height
    k = (out_h / 2) / height
    return max(2, int(round(width * k / 2)) * 2), max(2, int(round(height * k / 2)) * 2)


def models_dir():
    import vsmlrt
    return vsmlrt.models_path


def model_plan(e, width, height, models=None):
    """(model file, its scale) for these settings: which network, and how many times it enlarges."""
    models = models or models_dir()
    out_w, out_h = output_size(width, height, e["height"])
    need = out_h / height
    if e["preset"] == "film":
        return os.path.join(models, "enhance", FILM_MODELS[e["clean"]] + ".onnx"), 4
    # Anime: Real-CUGAN at 2x for up to 2.5x (the best line art); AnimeVideo v3 at 4x beyond that
    if need > 2.5:
        return os.path.join(models, "RealESRGANv2", "realesr-animevideov3.onnx"), 4
    legacy, pro = ANIME_MODELS[e["clean"]]
    pro_path = os.path.join(models, "cugan", pro + "-up2x.onnx")
    if os.path.exists(pro_path):
        return pro_path, 2
    return os.path.join(models, "cugan", f"up2x-latest-{legacy}.onnx"), 2


def backend(cfg, width, height):
    """The fastest way this PC runs the models: TensorRT on NVIDIA, else Vulkan (ncnn), else OpenVINO."""
    import vsmlrt
    gpu = int(cfg.get("rife_gpu") or 0)
    if hasattr(core, "trt"):
        return vsmlrt.Backend.TRT(fp16=True, device_id=gpu, num_streams=2, output_format=1)
    if hasattr(core, "ncnn"):
        return vsmlrt.Backend.NCNN_VK(fp16=True, device_id=gpu, num_streams=2)
    if hasattr(core, "ov"):
        return vsmlrt.Backend.OV_GPU(fp16=True, device_id=gpu)
    raise vs.Error("Enhance needs the AI models' runtime: run setup-enhance.cmd")


# Every model works on tiles of one fixed size, overlapping by 16 pixels, whatever the video's size.
# TensorRT builds an engine per input size, and a 1080p frame took 17 minutes to build on a GTX 1650;
# a fixed tile means one engine per model for every video, quick to build and made during setup.
TILE = (640, 384)
OVERLAP = 16


def tiles(width, height, multiple=8):
    """(tile size, overlap) for a picture: the fixed tile, or the whole picture when it's smaller."""
    if width <= TILE[0] and height <= TILE[1]:
        return (width, height), (0, 0)
    tw = min(TILE[0], width - width % multiple)
    th = min(TILE[1], height - height % multiple)
    return (tw, th), (OVERLAP if tw < width else 0, OVERLAP if th < height else 0)


def _rgb(clip, sample_bits, matrix):
    fmt = vs.RGBH if sample_bits == 16 else vs.RGBS
    return core.resize.Bicubic(clip, format=fmt, matrix_in_s=matrix)


def fix_compression(clip, cfg, matrix, strength=20.0):
    """DPIR deblocking: takes out the blocks and ringing of heavily compressed video, at its own size."""
    import vsmlrt
    be = backend(cfg, clip.width, clip.height)
    bits = 16 if isinstance(be, vsmlrt.Backend.TRT) else 32
    rgb = _rgb(clip, bits, matrix)
    pad_w, pad_h = (-clip.width) % 8, (-clip.height) % 8          # DPIR works on multiples of 8
    if pad_w or pad_h:
        rgb = core.std.AddBorders(rgb, right=pad_w, bottom=pad_h)
    size, overlap = tiles(rgb.width, rgb.height)
    out = vsmlrt.DPIR(rgb, strength=strength, model=vsmlrt.DPIRModel.drunet_deblocking_color,
                      tilesize=size, overlap=overlap, backend=be)
    if pad_w or pad_h:
        out = core.std.Crop(out, right=pad_w, bottom=pad_h)
    return core.resize.Bicubic(out, format=clip.format.id, matrix_s=matrix, dither_type="error_diffusion")


def upscale(clip, cfg, e, matrix):
    """The AI model, then a fit to the chosen size. At the video's own size the model's result is
    scaled back down: a cleaner, crisper picture at the same resolution."""
    import vsmlrt
    out_w, out_h = output_size(clip.width, clip.height, e["height"])
    in_w, in_h = ai_input_size(clip.width, clip.height, out_w, out_h)
    path, scale = model_plan(e, in_w, in_h)
    if not os.path.exists(path):
        raise vs.Error(f"Enhance model missing: {os.path.basename(path)} (run setup-enhance.cmd)")
    be = backend(cfg, clip.width, clip.height)
    bits = 16 if isinstance(be, vsmlrt.Backend.TRT) else 32
    rgb = _rgb(clip, bits, matrix)
    small = core.resize.Spline36(rgb, in_w, in_h) if (in_w, in_h) != (clip.width, clip.height) else rgb
    size, overlap = tiles(in_w, in_h)
    big = vsmlrt.inference(small, path, overlap=overlap, tilesize=size, backend=be)
    if (big.width, big.height) != (out_w, out_h):
        # Downscaling the model's output: Spline36 keeps the detail it made without ringing
        big = core.resize.Spline36(big, out_w, out_h)
    mix = MIX[e["preset"]][e["strength"]]
    if mix < 1.0:
        plain = core.resize.Spline36(rgb, out_w, out_h)
        big = core.std.Merge(plain, big, weight=mix)
    out_matrix = "709" if out_h >= 600 else matrix
    return core.resize.Bicubic(big, format=clip.format.id, matrix_s=out_matrix, dither_type="error_diffusion")


def add_detail(clip, amount):
    """A gentle unsharp mask on brightness: the "crisp" in a finished edit. 0..1."""
    if amount <= 0:
        return clip
    blur = core.std.Convolution(clip, matrix=[1, 2, 1, 2, 4, 2, 1, 2, 1], planes=[0])
    k = 0.8 * amount
    return core.std.Expr([clip, blur], [f"x x y - {k:.3f} * +", "", ""] if clip.format.num_planes > 1 else [f"x x y - {k:.3f} * +"])


def add_grain(clip, amount):
    """Fine film grain on brightness, different every frame, so a cleaned picture doesn't look
    plastic. Needs the akarin plugin (installed with TensorRT); without it, no grain."""
    if amount <= 0 or not hasattr(core, "akarin"):
        return clip
    fmt = clip.format
    peak = 1.0 if fmt.sample_type == vs.FLOAT else (1 << fmt.bits_per_sample) - 1
    strength = 0.02 * amount * peak
    noise = "X 12.9898 * Y 78.233 * + N 37.719 * + sin 43758.5453 * dup floor - 0.5 -"
    expr = f"x {noise} {strength:.4f} * +"
    try:
        return core.akarin.Expr(clip, [expr, "", ""] if fmt.num_planes > 1 else [expr])
    except vs.Error:
        return clip


def finish(clip, e):
    return add_grain(add_detail(clip, e["detail"]), e["grain"])


def label_text(e, out_h):
    size = {4320: "8K", 2160: "4K", 1440: "1440p", 1080: "1080p"}.get(out_h, f"{out_h}p")
    return f"NOVA Enhance  {size}"


def demo(before, after, labeller=None, words=("Original", "NOVA Enhance"), bar=None):
    """Before/after for Enhance at full size: the same middle part of the picture, the original
    enlarged plainly next to the enhanced one, each a quarter of the width and half the height, so the
    clip shows detail as it really is instead of shrinking it."""
    if (before.width, before.height) != (after.width, after.height):
        before = core.resize.Bicubic(before, after.width, after.height)
    if before.format.id != after.format.id:
        before = core.resize.Bicubic(before, format=after.format.id)
    w, h = after.width, after.height
    cw, ch = (w // 4) & ~1, (h // 2) & ~1
    x, y = ((w - cw) // 2) & ~1, ((h - ch) // 2) & ~1

    def crop(c):
        return core.std.Crop(c, left=x, right=w - x - cw, top=y, bottom=h - y - ch)

    left, right = crop(before), crop(after)
    if labeller:
        left, right = labeller(left, words[0]), labeller(right, words[1])
    parts = [left, bar(left)] if bar else [left]
    return core.std.StackHorizontal(parts + [right])
