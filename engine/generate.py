# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Novabit
"""NOVA Studio: generative detail for a photo, with SeedVR2 (ByteDance, Apache-2.0).

Enhance's other models sharpen what is in the picture; a generative model makes new detail that fits
it: pores, hair strands, fabric weave, leaves. SeedVR2 is a diffusion transformer that restores a
picture in one step and looks at the whole picture while it does (no text prompt: the picture itself is
its context). It runs through its open-source runner (numz/ComfyUI-SeedVR2_VideoUpscaler, Apache-2.0)
installed by setup-generate.cmd into runtime/generate, in its own Python with PyTorch for CUDA.

    python generate.py --image in.jpg --out out.png --settings '{"scale": 2}' --runner dir --models dir
                       [--gpu 0] [--vram 12] [--dit-model file] [--part x0,y0,x1,y1] [--compare c.png --spot 0.5,0.5]

Prints the same lines as photo.py: "stage <name>", "progress i/n", "note <text>", "done <path>",
"compare <path>", or "error <text>".
"""
import argparse
import json
import os
import re
import subprocess
import sys
import tempfile
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import photo  # noqa: E402  (Pillow only: the before/after, sizes and the output lines)

say = photo.say

DIT_MODEL = "seedvr2_ema_3b_fp8_e4m3fn.safetensors"
VAE_MODEL = "ema_vae_fp16.safetensors"
# The runner's four phases, and how far through the work each one starts (by its own timings: the
# decode of a big picture takes about as long as the generating)
PHASES = {"Phase 1": 5, "Phase 2": 20, "Phase 3": 55, "Phase 4": 95}
# Below this much graphics memory the model's blocks are swapped to the PC's memory as they're used
# (slower, but it fits); with less than 8 GB the app doesn't offer Generative at all
SWAP_BELOW_GB = 16
SWAP_ABOVE_MP = 4


def parse_args(argv=None):
    p = argparse.ArgumentParser(description="Generative detail for a photo (SeedVR2).")
    p.add_argument("--image", required=True)
    p.add_argument("--out", required=True)
    p.add_argument("--settings", default="{}", help="JSON: scale 1 | 2 | 4, amount 0..1 (how much of the generated picture is kept)")
    p.add_argument("--runner", required=True, help="the SeedVR2 runner's folder (inference_cli.py)")
    p.add_argument("--models", required=True, help="the folder holding the SeedVR2 models")
    p.add_argument("--python", default=sys.executable, help="the Python with PyTorch that runs it")
    p.add_argument("--gpu", type=int, default=0)
    p.add_argument("--vram", type=float, default=0, help="the card's memory in GB (0: unknown)")
    p.add_argument("--part", default="", help="x0,y0,x1,y1 as fractions: only this part is generated")
    p.add_argument("--dit-model", default=DIT_MODEL, help="the SeedVR2 model file (a GGUF one for small cards)")
    p.add_argument("--compare", default="")
    p.add_argument("--spot", default="0.5,0.5")
    return p.parse_args(argv)


def memory_args(out_w, out_h, vram, cautious=False):
    """How the runner fits this picture in this card: the picture's encoding and decoding in tiles always
    (they cost little); the model's blocks swapped to the PC's memory on cards under 16 GB with a big
    picture, or after it ran out of memory once (cautious)."""
    args = ["--vae_encode_tiled", "--vae_decode_tiled"]
    if vram and vram < 6:
        # A 4 GB card (GTX 1650): the model itself fits once swapped, but the picture's decoding in the
        # runner's 1024-pixel tiles ran out of memory; small tiles fit
        args += ["--vae_encode_tile_size", "512", "--vae_encode_tile_overlap", "64",
                 "--vae_decode_tile_size", "256", "--vae_decode_tile_overlap", "32"]
    elif vram and vram < 12:
        args += ["--vae_decode_tile_size", "512", "--vae_decode_tile_overlap", "64"]
    big = out_w * out_h / 1e6 > SWAP_ABOVE_MP
    cautious = cautious or bool(vram and vram < 6)        # 4 GB cards: everything that can wait in the PC's memory
    if cautious or (vram and vram < SWAP_BELOW_GB and big):
        args += ["--dit_offload_device", "cpu", "--blocks_to_swap", "32" if cautious else "16"]
        if cautious:
            args += ["--swap_io_components", "--vae_offload_device", "cpu"]
    return args


def runner_args(a, src, dst, out_w, out_h, cautious=False):
    """The runner's command line for one picture at out_w x out_h."""
    return [a.python, os.path.join(a.runner, "inference_cli.py"), src, "--output", dst,
            "--output_format", "png", "--model_dir", a.models, "--dit_model", getattr(a, "dit_model", DIT_MODEL),
            "--resolution", str(min(out_w, out_h)), "--max_resolution", str(max(out_w, out_h)),
            "--batch_size", "1", "--seed", "42", "--color_correction", "lab",
            "--cuda_device", str(a.gpu)] + memory_args(out_w, out_h, a.vram, cautious)


def run_runner(cmd, log):
    """Runs the runner, turning its phases into progress lines; (exit code, ran out of memory)."""
    env = dict(os.environ, PYTHONUNBUFFERED="1", PYTHONIOENCODING="utf-8")
    p = subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, encoding="utf-8",
                         errors="replace", env=env, cwd=os.path.dirname(cmd[1]))
    oom = False
    done = 0
    for line in p.stdout:
        log.write(line)
        for phase, at in PHASES.items():
            if phase in line and at > done:
                done = at
                say(f"progress {at}/100")
        if re.search(r"out of memory|OutOfMemoryError", line, re.I):
            oom = True
    return p.wait(), oom


# Picture kept around a chosen part, so the model sees what surrounds it; the seam is blended over this
PART_MARGIN = 48
PART_FEATHER = 24


def parse_part(text, w, h):
    """"x0,y0,x1,y1" (fractions of the picture) as a pixel box with PART_MARGIN around it, or None."""
    if not text:
        return None
    try:
        x0, y0, x1, y1 = (min(max(float(v), 0.0), 1.0) for v in text.split(","))
    except ValueError:
        return None
    if x1 - x0 < 0.01 or y1 - y0 < 0.01:
        return None
    box = (int(x0 * w), int(y0 * h), int(round(x1 * w)), int(round(y1 * h)))
    outer = (max(0, box[0] - PART_MARGIN), max(0, box[1] - PART_MARGIN),
             min(w, box[2] + PART_MARGIN), min(h, box[3] + PART_MARGIN))
    return box, outer


def feather_mask(size, inner, feather):
    """A mask the size of the generated part: opaque over the chosen box, fading out over feather pixels
    into the margin, so the generated part blends into the plainly enlarged picture around it."""
    from PIL import Image, ImageDraw, ImageFilter
    m = Image.new("L", size, 0)
    ImageDraw.Draw(m).rectangle(inner, fill=255)
    return m.filter(ImageFilter.GaussianBlur(feather / 2)) if feather > 0 else m


def main(argv=None):
    a = parse_args(argv)
    started = time.time()
    say("stage picture")
    from PIL import Image, ImageOps
    img = ImageOps.exif_transpose(Image.open(a.image))
    img.load()
    alpha = None
    if img.mode in ("RGBA", "LA", "PA") or (img.mode == "P" and "transparency" in img.info):
        alpha = img.convert("RGBA").getchannel("A")
    rgb = img.convert("RGB")
    settings = json.loads(a.settings or "{}")
    # How much of the generated picture is kept, the rest the original (plainly enlarged): below 1 it
    # tames the brush-stroke look the model can give large areas of fur or grass
    amount = min(1.0, max(0.0, float(settings.get("amount", 1.0))))
    scale, out_h = photo.target(img.width, img.height, settings.get("scale", 2))
    if out_h == img.height:
        out_w, out_h = img.width, img.height                 # the same size: exactly
    else:
        out_w = max(2, int(round(img.width * out_h / img.height / 2)) * 2)
        out_h = out_h // 2 * 2
    kx, ky = out_w / img.width, out_h / img.height
    part = parse_part(a.part, img.width, img.height)
    say(f"model {a.dit_model}")

    work = tempfile.mkdtemp(prefix="nova-generate-")
    src = os.path.join(work, "in.png")
    dst = os.path.join(work, "out.png")
    # Upright (EXIF), without transparency: the runner reads it with OpenCV. A chosen part: only it
    # (with some picture around it) goes to the model
    piece = rgb.crop(part[1]) if part else rgb
    piece.save(src, "PNG")
    gen_w = max(16, int(round(piece.width * kx)))
    gen_h = max(16, int(round(piece.height * ky)))
    log_path = os.path.join(HERE, "generate-runner.log")     # runtime\, next to photo.log
    say("stage generate")
    say("progress 1/100")
    with open(log_path, "w", encoding="utf-8") as log:
        code, oom = run_runner(runner_args(a, src, dst, gen_w, gen_h), log)
        if code != 0 and oom:
            say("note The graphics card ran out of memory; trying again with the model partly in the PC's memory (slower)")
            code, oom = run_runner(runner_args(a, src, dst, gen_w, gen_h, cautious=True), log)
    if code != 0 or not os.path.exists(dst):
        raise RuntimeError("the graphics card ran out of memory, even with the model partly in the PC's memory"
                           if oom else "the generative model stopped (details in runtime\\generate-runner.log)")

    say("stage save")
    generated = Image.open(dst).convert("RGB")
    if generated.size != (gen_w, gen_h):
        # The runner works in multiples of 16 pixels; back to the exact size
        generated = generated.resize((gen_w, gen_h), Image.LANCZOS)
    plain = rgb.resize((out_w, out_h), Image.LANCZOS) if (out_w, out_h) != rgb.size else rgb.copy()
    if part:
        ox, oy = int(round(part[1][0] * kx)), int(round(part[1][1] * ky))
        under = plain.crop((ox, oy, ox + gen_w, oy + gen_h))
        if amount < 1:
            generated = Image.blend(under, generated, amount)
        inner = (int((part[0][0] - part[1][0]) * kx), int((part[0][1] - part[1][1]) * ky),
                 int((part[0][2] - part[1][0]) * kx), int((part[0][3] - part[1][1]) * ky))
        feather = min(int(PART_FEATHER * kx), max(0, min(inner[0], inner[1], gen_w - inner[2], gen_h - inner[3])))
        plain.paste(generated, (ox, oy), feather_mask(generated.size, inner, feather))
        result = plain
    else:
        result = Image.blend(plain, generated, amount) if amount < 1 else generated
    if alpha is not None:
        result.putalpha(alpha.resize(result.size, Image.LANCZOS))
    os.makedirs(os.path.dirname(os.path.abspath(a.out)), exist_ok=True)
    tmp = a.out + ".part"
    if a.out.lower().endswith((".jpg", ".jpeg")):
        result.convert("RGB").save(tmp, "JPEG", quality=95, subsampling=0)
    else:
        result.save(tmp, "PNG")
    os.replace(tmp, a.out)
    say("progress 100/100")
    if a.compare:
        spot = photo.parse_spot(a.spot)
        if part:                                  # the before/after shows the part that was generated
            spot = ((part[0][0] + part[0][2]) / 2 / img.width, (part[0][1] + part[0][3]) / 2 / img.height)
        photo.make_compare(img, result, spot, a.compare)
        say(f"compare {a.compare}")
    for f in (src, dst):
        try:
            os.remove(f)
        except OSError:
            pass
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
