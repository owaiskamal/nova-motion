# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Novabit
"""NOVA Studio: generative detail for a photo, with SeedVR2 (ByteDance, Apache-2.0).

Enhance's other models sharpen what is in the picture; a generative model makes new detail that fits
it: pores, hair strands, fabric weave, leaves. SeedVR2 is a diffusion transformer that restores a
picture in one step and looks at the whole picture while it does (no text prompt: the picture itself is
its context). It runs through its open-source runner (numz/ComfyUI-SeedVR2_VideoUpscaler, Apache-2.0)
installed by setup-generate.cmd into runtime/generate, in its own Python with PyTorch for CUDA.

    python generate.py --image in.jpg --out out.png --settings '{"scale": 2}' --runner dir --models dir
                       [--gpu 0] [--vram 12] [--compare c.png --spot 0.5,0.5]

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
    p.add_argument("--settings", default="{}", help="JSON: scale 1 | 2 | 4")
    p.add_argument("--runner", required=True, help="the SeedVR2 runner's folder (inference_cli.py)")
    p.add_argument("--models", required=True, help="the folder holding the SeedVR2 models")
    p.add_argument("--python", default=sys.executable, help="the Python with PyTorch that runs it")
    p.add_argument("--gpu", type=int, default=0)
    p.add_argument("--vram", type=float, default=0, help="the card's memory in GB (0: unknown)")
    p.add_argument("--dit-model", default=DIT_MODEL, help="the SeedVR2 model file (a GGUF one for small cards)")
    p.add_argument("--compare", default="")
    p.add_argument("--spot", default="0.5,0.5")
    return p.parse_args(argv)


def memory_args(out_w, out_h, vram, cautious=False):
    """How the runner fits this picture in this card: the picture's encoding and decoding in tiles always
    (they cost little); the model's blocks swapped to the PC's memory on cards under 16 GB with a big
    picture, or after it ran out of memory once (cautious)."""
    args = ["--vae_encode_tiled", "--vae_decode_tiled"]
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
    settings = json.loads(a.settings or "{}")
    scale, out_h = photo.target(img.width, img.height, settings.get("scale", 2))
    if out_h == img.height:
        out_w, out_h = img.width, img.height                 # the same size: exactly
    else:
        out_w = max(2, int(round(img.width * out_h / img.height / 2)) * 2)
        out_h = out_h // 2 * 2
    say(f"model {a.dit_model}")

    work = tempfile.mkdtemp(prefix="nova-generate-")
    src = os.path.join(work, "in.png")
    dst = os.path.join(work, "out.png")
    img.convert("RGB").save(src, "PNG")      # upright (EXIF), without transparency: the runner reads it with OpenCV
    log_path = os.path.join(HERE, "generate-runner.log")     # runtime\, next to photo.log
    say("stage generate")
    say("progress 1/100")
    with open(log_path, "w", encoding="utf-8") as log:
        code, oom = run_runner(runner_args(a, src, dst, out_w, out_h), log)
        if code != 0 and oom:
            say("note The graphics card ran out of memory; trying again with the model partly in the PC's memory (slower)")
            code, oom = run_runner(runner_args(a, src, dst, out_w, out_h, cautious=True), log)
    if code != 0 or not os.path.exists(dst):
        raise RuntimeError("the graphics card ran out of memory, even with the model partly in the PC's memory"
                           if oom else "the generative model stopped (details in runtime\\generate-runner.log)")

    say("stage save")
    result = Image.open(dst).convert("RGB")
    if result.size != (out_w, out_h):
        # The runner works in multiples of 16 pixels; back to the exact size
        result = result.resize((out_w, out_h), Image.LANCZOS)
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
        photo.make_compare(img, result, photo.parse_spot(a.spot), a.compare)
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
