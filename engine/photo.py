# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Novabit
"""NOVA Studio: enhance a photo or a piece of art with the same AI models as Enhance's videos.

A single picture can take the heavy models a whole video can't: the "max" tier (Real-ESRGAN x4plus and
x4plus anime, BSD-3) adds far more texture than the compact video models, and there's no flicker to
avoid between frames. The picture goes through enhance.py exactly as a video frame does: optional
Fix compression (DPIR, good for JPEG blocks), Remove noise and blur (NAFNet, at the photo's own size),
the AI enlargement mixed with a plain one by AI strength, then detail and grain. At the same size with
Remove noise and blur there's no enlargement: a 4x model's work shrunk back 4x averaged away nearly
everything it added. Transparency is kept (enlarged plainly). The result is saved as PNG or JPEG.

    python photo.py --image in.jpg --out out.png --settings '{"preset": "film", "scale": 2, ...}'
                    [--packages dir] [--gpu 0] [--gpu-class entry]

settings: preset film | anime, scale 1 | 2 | 4 (1: same size, cleaned), clean, strength, detail, grain,
fix_compression, model standard | max, restore 0..1 (Remove noise and blur). Prints "stage <name>", then "done <path>", or "error <text>".
"""
import argparse
import json
import os
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
# Longest side of a finished picture: 8K. Beyond that the picture and the model's output don't fit
# comfortably in memory on 16 GB PCs.
MAX_SIDE = 8192
SCALES = (1, 2, 4)


def say(*parts):
    print(*parts, flush=True)


def utf8_output():
    """The app reads these lines as UTF-8. Left to Windows' code page, a picture named in Urdu, Chinese
    or with an emoji made the final "done <path>" line fail, and with it a picture that was already saved
    (python -I ignores PYTHONIOENCODING, so it's set here)."""
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")
        except (AttributeError, ValueError):
            pass


def parse_args(argv=None):
    p = argparse.ArgumentParser(description="Enhance a photo with AI.")
    p.add_argument("--image", required=True)
    p.add_argument("--out", required=True)
    p.add_argument("--settings", default="{}", help="JSON: preset, scale, clean, strength, detail, grain, fix_compression, model, restore")
    p.add_argument("--packages", default="", help="folder holding numpy and Pillow")
    p.add_argument("--gpu", type=int, default=0)
    p.add_argument("--gpu-class", default="")
    p.add_argument("--compare", default="", help="also write a before/after at 100%% here")
    p.add_argument("--spot", default="0.5,0.5", help="centre of the before/after, as fractions of width,height")
    p.add_argument("--compare-only", action="store_true", help="--out is an existing result: only (re)make --compare")
    return p.parse_args(argv)


# Each side of the before/after: a part of the result at 100%, so its pixels are seen as they are
COMPARE_SIDE = (880, 880)


def make_compare(original, result, spot, path):
    """Before | after, side by side: the same part of the picture, the result at 100% and the original
    enlarged plainly (what a picture viewer shows) to the same size, with an amber line between and
    labels. A whole picture shrunk to fit the screen hides exactly what enhancing changes."""
    from PIL import Image, ImageDraw, ImageFont
    rw, rh = result.size
    cw, ch = min(rw, COMPARE_SIDE[0]), min(rh, COMPARE_SIDE[1])
    fx, fy = spot
    x = int(min(max(fx * rw - cw / 2, 0), rw - cw))
    y = int(min(max(fy * rh - ch / 2, 0), rh - ch))
    after = result.convert("RGB").crop((x, y, x + cw, y + ch))
    kx, ky = original.width / rw, original.height / rh
    before = original.convert("RGB").resize((cw, ch), Image.BICUBIC,
                                             box=(x * kx, y * ky, (x + cw) * kx, (y + ch) * ky))
    bar = max(6, cw // 110)
    out = Image.new("RGB", (cw * 2 + bar, ch), (255, 176, 32))
    out.paste(before, (0, 0))
    out.paste(after, (cw + bar, 0))
    draw = ImageDraw.Draw(out)
    try:
        font = ImageFont.load_default(size=max(14, cw // 40))
    except TypeError:          # Pillow before 10.1
        font = ImageFont.load_default()
    zoom = rw / original.width
    for left, words in ((0, f"BEFORE: original, enlarged {zoom:.1f}x" if zoom > 1.01 else "BEFORE: original"),
                        (cw + bar, "AFTER: NOVA Enhance, at 100%")):
        tw, th = draw.textbbox((0, 0), words, font=font)[2:]
        draw.rectangle((left + 10, 10, left + 26 + tw, 20 + th + 6), fill=(15, 20, 36))
        draw.text((left + 18, 14), words, font=font, fill=(230, 233, 242))
    tmp = path + ".part"
    out.save(tmp, "PNG")
    os.replace(tmp, path)


def parse_spot(text):
    try:
        fx, fy = (float(v) for v in text.split(","))
        return min(max(fx, 0.0), 1.0), min(max(fy, 0.0), 1.0)
    except ValueError:
        return 0.5, 0.5


def target(width, height, scale):
    """(scale actually used, finished height): the chosen scale, kept within MAX_SIDE."""
    scale = max(1.0, min(float(scale), MAX_SIDE / max(width, height)))
    return scale, int(round(height * scale / 2)) * 2


def photo_settings(raw):
    import enhance
    s = dict(raw)
    s.setdefault("preset", "film")
    s.setdefault("strength", "full")       # one picture, looked at closely: the model's own result
    e = enhance.settings({"enhance": s})
    if e is None:
        raise ValueError(f"Unknown preset {s.get('preset')!r}")
    e["keep_input"] = True
    scale = s.get("scale", 2)
    e["scale"] = scale if scale in SCALES else 2
    return e


def to_clip(rgb):
    """An H x W x 3 float array (0..1) as a one-frame RGBS clip."""
    import numpy as np
    import vapoursynth as vs
    core = vs.core
    h, w = rgb.shape[:2]
    blank = core.std.BlankClip(width=w, height=h, format=vs.RGBS, length=1)

    def fill(n, f):
        out = f.copy()
        for i in range(3):
            np.copyto(np.asarray(out[i]), rgb[:, :, i])
        return out
    return core.std.ModifyFrame(blank, blank, fill)


# Strips of the picture, for progress: each is enhanced with this much picture around it (as the
# previews do), so the strips join exactly as if the picture had been done whole
STRIP_MARGIN = 32
STRIPS = 12


def strips(h, out_h):
    """[(y0, y1)] rows of the picture in strips, or None when they can't join exactly (the finished
    size isn't a whole multiple: then the picture is done whole, without progress steps)."""
    if out_h % h or h < 2 * 256:
        return None
    step = max(256, -(-h // STRIPS))
    step += step % 2
    return [(y, min(h, y + step)) for y in range(0, h, step)]


def looks_broken(rgb, out):
    """The result is nothing like the picture: on average more than an eighth of the brightness range
    apart, compared small (enhancing changes detail, never the picture as a whole)."""
    import numpy as np
    from PIL import Image
    small = (64, max(1, round(64 * rgb.shape[0] / rgb.shape[1])))
    before = np.asarray(Image.fromarray((rgb * 255 + 0.5).astype("uint8")).resize(small, Image.BOX), dtype=np.float32)
    after = np.asarray(Image.fromarray(out).resize(small, Image.BOX), dtype=np.float32)
    return float(np.abs(before - after).mean()) > 32


def enlarge(clip, cfg, e, full):
    """The AI enlargement, except at the same size after Remove noise and blur: the restored picture
    is the result there (an upscaler at 1x only smooths it)."""
    import enhance
    if e.get("restore") and e["height"] <= (full[1] if full else clip.height):
        return clip
    return enhance.upscale(clip, cfg, e, "709", full=full)


def enhance_picture(rgb, e, gpu=0, gpu_class=""):
    """The enhanced picture as an H x W x 3 uint8 array. Done in strips, one frame each of one clip
    (so each model is loaded once), saying "progress i/n" after each."""
    import numpy as np
    import vapoursynth as vs
    import enhance
    core = vs.core
    h, w = rgb.shape[:2]
    scale, e["height"] = target(w, h, e["scale"])
    out_w, out_h = enhance.output_size(w, h, e["height"])
    cfg = {"rife_gpu": gpu, "gpu_class": gpu_class}
    # The same 4:4:4 YUV the video chain works in, at 16 bits so nothing is lost on the way
    clip = core.resize.Bicubic(to_clip(rgb), format=vs.YUV444P16, matrix_s="709")
    parts = strips(h, out_h)
    say("stage clean" if e["fix_compression"] else "stage restore" if e.get("restore") else "stage enhance")
    if parts is None or len(parts) < 2:
        if e["fix_compression"]:
            clip = enhance.fix_compression(clip, cfg, "709")
            say("stage enhance")
        out = enhance.finish(enlarge(enhance.restore(clip, cfg, e, "709"), cfg, e, None), e)
        out = core.resize.Bicubic(out, format=vs.RGB24, matrix_in_s="709", dither_type="error_diffusion")
        say("progress 1/1")
        f = out.get_frame(0)
        return np.dstack([np.asarray(f[i]) for i in range(3)]).copy()

    k = out_h // h
    m = STRIP_MARGIN
    tall = max(y1 - y0 for y0, y1 in parts) + 2 * m
    frames, keep = [], []
    for y0, y1 in parts:
        ya, yb = max(0, y0 - m), min(h, y1 + m)
        piece = core.std.Crop(clip, top=ya, bottom=h - yb)
        if piece.height < tall:
            # The same height for every strip; the extra rows mirror the picture's edge and are cut off
            need = tall - piece.height
            edge = core.std.FlipVertical(core.std.Crop(piece, top=max(0, piece.height - need)))
            while edge.height < need:
                edge = core.std.StackVertical([edge, edge])
            piece = core.std.StackVertical([piece, core.std.Crop(edge, bottom=edge.height - need)])
        frames.append(piece)
        keep.append(((y0 - ya) * k, (y1 - y0) * k))
    strip_clip = core.std.Splice(frames)
    if e["fix_compression"]:
        strip_clip = enhance.fix_compression(strip_clip, cfg, "709")
    out = enhance.finish(enlarge(enhance.restore(strip_clip, cfg, e, "709"), cfg, e, (w, h)), e)
    out = core.resize.Bicubic(out, format=vs.RGB24, matrix_in_s="709", dither_type="error_diffusion")
    rows = []
    for i, (top, n) in enumerate(keep):
        f = out.get_frame(i)
        if i == 0 and e["fix_compression"]:
            say("stage enhance")
        part = np.dstack([np.asarray(f[c]) for c in range(3)])
        rows.append(part[top:top + n].copy())
        say(f"progress {i + 1}/{len(keep)}")
    return np.vstack(rows)


def main(argv=None):
    utf8_output()
    a = parse_args(argv)
    if a.packages and a.packages not in sys.path:
        sys.path.insert(0, a.packages)
    for p in (HERE, os.path.join(HERE, "mpv")):
        if p not in sys.path:
            sys.path.insert(0, p)
    started = time.time()
    say("stage picture")
    from PIL import Image, ImageOps
    import numpy as np
    img = ImageOps.exif_transpose(Image.open(a.image))
    img.load()
    if a.compare_only:
        make_compare(img, Image.open(a.out), parse_spot(a.spot), a.compare)
        say(f"compare {a.compare}")
        return 0
    alpha = None
    if img.mode in ("RGBA", "LA", "PA") or (img.mode == "P" and "transparency" in img.info):
        alpha = img.convert("RGBA").getchannel("A")
    rgb = np.asarray(img.convert("RGB"), dtype=np.float32) / 255.0
    e = photo_settings(json.loads(a.settings or "{}"))

    import enhance
    if e.get("model") == "max" and not enhance.has_max_model(e):
        say("note The Max detail models aren't installed (run setup-enhance.cmd); using the standard ones")
    if e.get("restore") and not enhance.has_restore_model():
        say("note Remove noise and blur needs its model (run setup-enhance.cmd); enhancing without it")
        e["restore"] = 0
    if e.get("restore"):
        say(f"model {enhance.RESTORE_MODEL}.onnx")
    if not e.get("restore") or e["scale"] > 1:
        path, _ = enhance.model_plan(e, img.width, img.height)
        say(f"model {os.path.basename(path)}")

    out = enhance_picture(rgb, e, a.gpu, a.gpu_class)
    if e.get("restore") and looks_broken(rgb, out):
        # A model that overflows on this graphics card (NAFNet in half precision gave a green
        # checkerboard on a GTX 1650): the photo without it rather than a ruined one
        say("note Remove noise and blur gave a broken picture on this graphics card; enhanced without it")
        e["restore"] = 0
        say("progress 0/1")
        out = enhance_picture(rgb, e, a.gpu, a.gpu_class)
    result = Image.fromarray(out, "RGB")
    say("stage save")
    if alpha is not None:
        result.putalpha(alpha.resize(result.size, Image.LANCZOS))
    out_dir = os.path.dirname(os.path.abspath(a.out))
    os.makedirs(out_dir, exist_ok=True)
    tmp = a.out + ".part"
    if a.out.lower().endswith((".jpg", ".jpeg")):
        result.convert("RGB").save(tmp, "JPEG", quality=95, subsampling=0)
    else:
        result.save(tmp, "PNG")
    os.replace(tmp, a.out)
    if a.compare:
        make_compare(img, result, parse_spot(a.spot), a.compare)
        say(f"compare {a.compare}")
    say(f"took {time.time() - started:.1f} s, {img.width}x{img.height} to {result.width}x{result.height}")
    say(f"done {a.out}")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as ex:      # one readable line for the app; the details for the log
        import traceback
        traceback.print_exc()
        print(f"error {ex}", flush=True)
        sys.exit(1)
