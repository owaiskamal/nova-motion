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
    RealESRGANv2/realesr-animevideov3.onnx  Real-ESRGAN AnimeVideo v3, BSD-3: installed, no longer chosen
    dpir/drunet_deblocking_color.onnx  DPIR, MIT: compression blocks and ringing
    enhance/RealESRGAN_x4plus{,_anime_6B}.onnx  Real-ESRGAN x4plus, BSD-3: "Max detail" for photos, a full
        RRDB network that adds far more texture than the compact models, and is 10-15x heavier
    restore/nafnet_reds64.onnx  NAFNet (REDS), MIT: "Remove noise and blur" for photos, at their own size

The "enhance" setting:
    {"preset": "film" | "anime" | "off", "height": 2160 (0: the video's own), "clean": "off" | "light" | "strong",
     "strength": "natural" | "balanced" | "full", "fix_compression": false, "detail": 0..1, "grain": 0..1,
     "model": "standard" | "max", "restore": 0..1}
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
# "Max detail": one heavy model per preset, used as they are (the clean-up setting doesn't apply)
MAX_MODELS = {"film": "RealESRGAN_x4plus", "anime": "RealESRGAN_x4plus_anime_6B"}
ANIME_MODELS = {"off": ("no-denoise", "pro-no-denoise3x"), "light": ("conservative", "pro-conservative"),
                "strong": ("denoise3x", "pro-denoise3x")}


def settings(cfg):
    """The enhance settings with defaults, or None when Enhance is off."""
    e = cfg.get("enhance") or {}
    if not isinstance(e, dict) or e.get("preset", "off") not in PRESETS:
        return None
    out = {"preset": e["preset"], "height": int(e.get("height") or 0), "clean": e.get("clean", "light"),
           "fix_compression": bool(e.get("fix_compression")), "detail": float(e.get("detail") or 0),
           "grain": float(e.get("grain") or 0), "strength": e.get("strength", "balanced"),
           "model": "max" if e.get("model") == "max" else "standard", "restore": float(e.get("restore") or 0)}
    if out["clean"] not in CLEAN:
        out["clean"] = "light"
    if out["strength"] not in STRENGTHS:
        out["strength"] = "balanced"
    out["detail"] = min(1.0, max(0.0, out["detail"]))
    out["grain"] = min(1.0, max(0.0, out["grain"]))
    out["restore"] = min(1.0, max(0.0, out["restore"]))
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
    if e.get("model") == "max":
        path = os.path.join(models, "enhance", MAX_MODELS[e["preset"]] + ".onnx")
        if os.path.exists(path):
            return path, 4
    if e["preset"] == "film":
        return os.path.join(models, "enhance", FILM_MODELS[e["clean"]] + ".onnx"), 4
    # Anime: Real-CUGAN at 2x, then enlarged the rest of the way, at every size. AnimeVideo v3 at 4x
    # (used beyond 2.5x until 0.3.1) flattened shading and hatching into a waxy look on 480p sources
    # taken to 4K; Real-CUGAN keeps the line art and the cel texture
    legacy, pro = ANIME_MODELS[e["clean"]]
    pro_path = os.path.join(models, "cugan", pro + "-up2x.onnx")
    if os.path.exists(pro_path):
        return pro_path, 2
    return os.path.join(models, "cugan", f"up2x-latest-{legacy}.onnx"), 2


def has_max_model(e, models=None):
    return os.path.exists(os.path.join(models or models_dir(), "enhance", MAX_MODELS[e["preset"]] + ".onnx"))


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


# DPIR is a big network (125 MB): its TensorRT build on a 640 x 384 tile ran 7 minutes on a 4 GB GTX
# 1650 and failed. Smaller tiles keep the build within such cards.
DPIR_TILE = (384, 256)
# Fix compression is for low-quality downloads; above this height (4K phone clips) there are no
# blocks worth the cost, and DPIR on 4K frames is far too slow.
FIX_COMPRESSION_MAX_HEIGHT = 1440


def tiles(width, height, multiple=8, tile=TILE, overlap=OVERLAP):
    """(tile size, overlap): always the fixed tile, overlapping where the picture is split. Pictures
    smaller than a tile in either direction are padded to it first (pad_to_tile): a 480 x 640
    portrait clip otherwise asked for a 480 x 384 engine, a build of minutes in the middle of a preview."""
    return tile, (overlap if width > tile[0] else 0, overlap if height > tile[1] else 0)


def pad_to_tile(clip, tile):
    """(clip at least the tile's size, padding right, padding bottom); the padding is cropped off after."""
    pw, ph = max(0, tile[0] - clip.width), max(0, tile[1] - clip.height)
    return (core.std.AddBorders(clip, right=pw, bottom=ph) if pw or ph else clip), pw, ph


def mirror_pad(clip, right, bottom):
    """clip with its right and bottom edges mirrored outwards: for a model that looks at the whole
    tile, a black border would change the picture next to it."""
    def extend(c, n, horizontal):
        parts, flipped = [c], (core.std.FlipHorizontal(c) if horizontal else core.std.FlipVertical(c))
        while n > 0:
            take = min(n, c.width if horizontal else c.height)
            parts.append(core.std.Crop(flipped, right=c.width - take) if horizontal
                         else core.std.Crop(flipped, bottom=c.height - take))
            n -= take
        if len(parts) == 1:
            return c
        return core.std.StackHorizontal(parts) if horizontal else core.std.StackVertical(parts)
    return extend(extend(clip, right, True), bottom, False)


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
    rgb, tw, th = pad_to_tile(rgb, DPIR_TILE)
    pad_w, pad_h = pad_w + tw, pad_h + th
    size, overlap = tiles(rgb.width, rgb.height, tile=DPIR_TILE)
    import jobs
    with jobs.preparing(jobs.DEFAULT_TITLE, "Fix compression (DPIR)",
                  key="enhance:drunet_deblocking_color.onnx", gpu_class=cfg.get("gpu_class")):
        out = vsmlrt.DPIR(rgb, strength=strength, model=vsmlrt.DPIRModel.drunet_deblocking_color,
                          tilesize=size, overlap=overlap, backend=be)
    if pad_w or pad_h:
        out = core.std.Crop(out, right=pad_w, bottom=pad_h)
    return core.resize.Bicubic(out, format=clip.format.id, matrix_s=matrix, dither_type="error_diffusion")


# "Remove noise and blur" for photos: NAFNet trained on REDS (blurred, compressed video frames) takes out
# camera noise, softness and JPEG damage together, at the picture's own size, before any enlarging. On
# a phone photo it cleaned the skin and sharpened eyes and lashes where Real-ESRGAN at the same size
# changed almost nothing. Mixed with the original by the "restore" amount so skin keeps some texture.
RESTORE_MODEL = "nafnet_reds64"
# 384 x 384 tiles: a 68-million-weight network, kept within a 4 GB card's TensorRT build (DPIR, half
# the size, failed at 640 x 384 on a GTX 1650); NAFNet's channel attention looks at a whole tile, so
# tiles overlap twice as much as the upscalers'
RESTORE_TILE = (384, 384)
RESTORE_OVERLAP = 32


def restore_path(models=None):
    return os.path.join(models or models_dir(), "restore", RESTORE_MODEL + ".onnx")


def has_restore_model(models=None):
    return os.path.exists(restore_path(models))


def restore(clip, cfg, e, matrix):
    """Noise, blur and compression damage removed at the clip's own size (NAFNet), mixed with the
    original by e["restore"]."""
    amount = e.get("restore") or 0
    if amount <= 0:
        return clip
    import vsmlrt
    path = restore_path()
    if not os.path.exists(path):
        raise vs.Error(f"Enhance model missing: {os.path.basename(path)} (run setup-enhance.cmd)")
    be = backend(cfg, clip.width, clip.height)
    bits = 16 if isinstance(be, vsmlrt.Backend.TRT) else 32
    rgb = _rgb(clip, bits, matrix)
    pw, ph = max(0, RESTORE_TILE[0] - rgb.width), max(0, RESTORE_TILE[1] - rgb.height)
    # NAFNet works on multiples of 16 at heart; padding to them here keeps every tile the same
    pw, ph = pw + (-(rgb.width + pw)) % 16, ph + (-(rgb.height + ph)) % 16
    padded = mirror_pad(rgb, pw, ph)
    size, overlap = tiles(padded.width, padded.height, tile=RESTORE_TILE, overlap=RESTORE_OVERLAP)
    import jobs
    with jobs.preparing(jobs.DEFAULT_TITLE, "Remove noise and blur (NAFNet)",
                        key=f"enhance:{RESTORE_MODEL}.onnx", gpu_class=cfg.get("gpu_class")):
        out = vsmlrt.inference(padded, path, overlap=overlap, tilesize=size, backend=be)
    if pw or ph:
        out = core.std.Crop(out, right=pw, bottom=ph)
    if amount < 1.0:
        out = core.std.Merge(core.resize.Point(rgb, format=vs.RGBS), core.resize.Point(out, format=vs.RGBS), weight=amount)
    return core.resize.Bicubic(out, format=clip.format.id, matrix_s=matrix, dither_type="error_diffusion")


def upscale(clip, cfg, e, matrix, full=None):
    """The AI model, then a fit to the chosen size. At the video's own size the model's result is
    scaled back down: a cleaner, crisper picture at the same resolution.

    full=(width, height): clip is a part of a frame that size (a preview's crop); it's enlarged
    exactly as it would be within the whole frame, with the same model."""
    import vsmlrt
    fw, fh = full or (clip.width, clip.height)
    fo_w, fo_h = output_size(fw, fh, e["height"])
    # Photos: the model sees the whole picture, and its output is scaled to the size (as Real-ESRGAN's
    # own tool does), while that output stays within memory; videos cap it at half the finished size
    fi_w, fi_h = (fw, fh) if e.get("keep_input") and max(fw, fh) * 4 <= PHOTO_MODEL_OUTPUT_MAX \
        else ai_input_size(fw, fh, fo_w, fo_h)
    path, scale = model_plan(e, fi_w, fi_h)

    def part(n, whole, of):
        return max(2, int(round(n * whole / of / 2)) * 2)
    out_w, out_h = (part(clip.width, fo_w, fw), part(clip.height, fo_h, fh)) if full else (fo_w, fo_h)
    in_w, in_h = (part(clip.width, fi_w, fw), part(clip.height, fi_h, fh)) if full else (fi_w, fi_h)
    if not os.path.exists(path):
        raise vs.Error(f"Enhance model missing: {os.path.basename(path)} (run setup-enhance.cmd)")
    be = backend(cfg, clip.width, clip.height)
    bits = 16 if isinstance(be, vsmlrt.Backend.TRT) else 32
    rgb = _rgb(clip, bits, matrix)
    small = core.resize.Spline36(rgb, in_w, in_h) if (in_w, in_h) != (clip.width, clip.height) else rgb
    small, pw, ph = pad_to_tile(small, TILE)
    size, overlap = tiles(small.width, small.height)
    import jobs
    with jobs.preparing(jobs.DEFAULT_TITLE, os.path.basename(path)[:-5],
                  key=f"enhance:{os.path.basename(path)}", gpu_class=cfg.get("gpu_class")):
        big = vsmlrt.inference(small, path, overlap=overlap, tilesize=size, backend=be)
    if pw or ph:
        k = big.width // small.width
        big = core.std.Crop(big, right=pw * k, bottom=ph * k)
    if (big.width, big.height) != (out_w, out_h):
        # Downscaling the model's output: Spline36 keeps the detail it made without ringing
        big = core.resize.Spline36(big, out_w, out_h)
    mix = MIX[e["preset"]][e["strength"]]
    if mix < 1.0:
        # In 32-bit float: older VapourSynth (the one shipped) can't Merge the half floats TensorRT gives
        plain = core.resize.Spline36(rgb, out_w, out_h, format=vs.RGBS)
        big = core.std.Merge(plain, core.resize.Point(big, format=vs.RGBS) if big.format.id != vs.RGBS else big, weight=mix)
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


def demo_view(w, h):
    """The part of a finished w x h frame a "100% detail" before/after shows: the middle, a quarter
    of the width and half the height, as (x, y, width, height)."""
    cw, ch = (w // 4) & ~1, (h // 2) & ~1
    return ((w - cw) // 2) & ~1, ((h - ch) // 2) & ~1, cw, ch


def split_view(w, h):
    """Split: the right half of the finished frame is enhanced (the left half is the original)."""
    half = (w // 2) & ~3
    return half, 0, w - half, h


def whole_view(w, h):
    """Side by side: the whole finished frame."""
    return 0, 0, w & ~1, h & ~1


# How a before/after shows the picture (the app's Compare choice). "side" was the 100% detail view
# before the choice existed; old settings keep meaning that.
COMPARE_VIEWS = {"split": split_view, "whole": whole_view, "zoom": demo_view, "side": demo_view}
# A side-by-side of two whole frames is twice as wide: kept within 3840 px (two 1080p frames). The GPU's
# H.264 encoder stops at 4096 px wide ("Width 7678 exceeds 4096" on an RTX 4070, which failed the export),
# and 3840 is also the most that sharing sites and screens show
COMPARE_MAX_WIDTH = 3840


def compare_mode(cfg):
    m = (cfg or {}).get("demo_mode") or "split"
    return m if m in COMPARE_VIEWS else "split"


# Longest side of a model's output for a photo seen whole: 16384 px (a 12 MP phone photo at 4x) is
# about 1.5 GB in half floats, only for the moment it's scaled to the finished size. Larger photos are
# halved first: the model never has to see more than 4096 px to make an 8K picture.
PHOTO_MODEL_OUTPUT_MAX = 16384


# Pixels of picture kept around a preview's part, at the video's size: the AI model and the resizers
# look a few pixels beyond what they make, so the edges of the part come out as in the whole frame.
PREVIEW_MARGIN = 32
# Fix compression runs before smoothing, which may move picture in from further away
PREVIEW_FIX_MARGIN = 96


def preview_plan(width, height, e, view=None):
    """Previews show only part of the finished frame (view: demo_view, split_view or whole_view), so
    only that part (and a margin) is cleaned and enlarged: an eighth of the work for 100% detail,
    half for a split.

    {"box": the part of the video frame to enhance (x0, y0, x1, y1), "view": (x, y, w, h) to cut
    from the enlarged part, "fix_box": the part Fix compression cleans}."""
    out_w, out_h = output_size(width, height, e["height"])
    kx, ky = out_w / width, out_h / height
    x, y, cw, ch = (view or demo_view)(out_w, out_h)

    def box(margin):
        x0 = max(0, int(x / kx) - margin) & ~3
        y0 = max(0, int(y / ky) - margin) & ~3
        x1 = min(width, -int(-(x + cw) // kx) + margin)
        y1 = min(height, -int(-(y + ch) // ky) + margin)
        x1 = x1 if x1 == width else x1 + (-(x1 - x0)) % 4
        y1 = y1 if y1 == height else y1 + (-(y1 - y0)) % 4
        return x0, y0, min(width, x1), min(height, y1)
    b = box(PREVIEW_MARGIN)
    left = int(round(x - b[0] * kx)) & ~1
    top = int(round(y - b[1] * ky)) & ~1
    return {"box": b, "view": (left, top, cw, ch), "fix_box": box(PREVIEW_FIX_MARGIN)}


def crop_box(clip, b):
    x0, y0, x1, y1 = b
    if (x0, y0, x1, y1) == (0, 0, clip.width, clip.height):
        return clip
    return core.std.Crop(clip, left=x0, right=clip.width - x1, top=y0, bottom=clip.height - y1)


def paste_box(whole, part, b):
    """whole with part put back at box b (same format)."""
    x0, y0, x1, y1 = b
    w, h = whole.width, whole.height
    row = ([crop_box(whole, (0, y0, x0, y1))] if x0 > 0 else []) + [part] + \
          ([crop_box(whole, (x1, y0, w, y1))] if x1 < w else [])
    middle = core.std.StackHorizontal(row) if len(row) > 1 else part
    rows = ([crop_box(whole, (0, 0, w, y0))] if y0 > 0 else []) + [middle] + \
           ([crop_box(whole, (0, y1, w, h))] if y1 < h else [])
    return core.std.StackVertical(rows) if len(rows) > 1 else middle


def demo(before, after, labeller=None, words=("Original", "NOVA Enhance"), bar=None, view=None):
    """Before/after for Enhance at full size: the same middle part of the picture, the original
    enlarged plainly next to the enhanced one, each a quarter of the width and half the height, so the
    clip shows detail as it really is instead of shrinking it. view: the part to show, when after is
    already only a part of the frame (preview_plan)."""
    if (before.width, before.height) != (after.width, after.height):
        before = core.resize.Bicubic(before, after.width, after.height)
    if before.format.id != after.format.id:
        before = core.resize.Bicubic(before, format=after.format.id)
    w, h = after.width, after.height
    x, y, cw, ch = view or demo_view(w, h)
    x, y = max(0, min(x, w - cw)) & ~1, max(0, min(y, h - ch)) & ~1

    def crop(c):
        return core.std.Crop(c, left=x, right=w - x - cw, top=y, bottom=h - y - ch)

    left, right = crop(before), crop(after)
    if labeller:
        left, right = labeller(left, words[0]), labeller(right, words[1])
    parts = [left, bar(left)] if bar else [left]
    return core.std.StackHorizontal(parts + [right])


def compare(before, after, mode, out_size, view, labeller=None, words=("Original", "NOVA Enhance"), bar=None):
    """A before/after of the whole picture at the finished size.

    before: the original, whole, at the video's own size. after: the enhanced part (preview_plan's
    box, enlarged); view = (x, y, w, h) cuts the shown part out of it. mode "split": the left half
    is the original enlarged plainly, the right half NOVA, with a bar between (the classic wipe);
    "whole": both whole frames side by side."""
    out_w, out_h = out_size
    x, y, cw, ch = view
    after = core.std.Crop(after, left=x, right=after.width - x - cw, top=y, bottom=after.height - y - ch)
    before = core.resize.Bicubic(before, out_w & ~1, out_h & ~1, format=after.format.id)
    if mode == "whole":
        bw = bar(before).width if bar else 0
        k = min(1.0, (COMPARE_MAX_WIDTH - bw) / (2 * before.width))
        if k < 1:
            w2, h2 = int(before.width * k) & ~1, int(before.height * k) & ~1
            before, after = core.resize.Bicubic(before, w2, h2), core.resize.Bicubic(after, w2, h2)
        left, right = before, after
        if labeller:
            left, right = labeller(left, words[0]), labeller(right, words[1])
        return core.std.StackHorizontal([left, bar(left), right] if bar else [left, right])
    # split: the bar takes its width from the right side, so the picture keeps its size and place
    half = before.width - after.width
    left = core.std.Crop(before, right=before.width - half)
    b = bar(left) if bar else None
    if b is not None:
        after = core.std.Crop(after, left=b.width)
    if labeller:
        left, after = labeller(left, words[0]), labeller(after, words[1])
    return core.std.StackHorizontal([left, b, after] if b is not None else [left, after])
