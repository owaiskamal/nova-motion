# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Novabit
"""Tests for engine/generate.py (generative detail, SeedVR2), no GPU needed: a stand-in runner
(fake_seedvr2) enlarges plainly. With SEEDVR2_RUNNER set to a checkout of the real runner, the command
lines are also checked against its own argument parser.

    pip install pillow
    python tests/engine/test_generate.py
"""
import ast
import contextlib
import io
import os
import re
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "..", "engine"))
import generate  # noqa: E402
from PIL import Image  # noqa: E402

fails = 0


def check(ok, what):
    global fails
    print(("PASS " if ok else "FAIL ") + what)
    if not ok:
        fails += 1


d = tempfile.mkdtemp(prefix="nm-generate-")
FAKE = os.path.join(HERE, "fake_seedvr2")
os.environ["FAKE_SEEDVR2_ARGS"] = os.path.join(d, "args.txt")


def run(image, out, settings, *extra):
    lines = io.StringIO()
    with contextlib.redirect_stdout(lines):
        code = generate.main(["--image", image, "--out", out, "--settings", settings, "--runner", FAKE,
                              "--models", d, *extra])
    return code, lines.getvalue()


def picture(name, w, h, mode="RGB"):
    img = Image.new(mode, (w, h), (90, 140, 200, 255) if mode == "RGBA" else (90, 140, 200))
    if mode == "RGBA":
        img.putpixel((0, 0), (0, 0, 0, 0))
        for x in range(w // 2):
            for y in range(h):
                img.putpixel((x, y), (90, 140, 200, 0))
    path = os.path.join(d, name)
    img.save(path)
    return path


# ---------------------------------------------------------------- a photo, 2x
src = picture("a.png", 301, 200)
out = os.path.join(d, "a out.png")
code, text = run(src, out, '{"scale": 2}', "--vram", "12", "--compare", os.path.join(d, "cmp.png"))
res = Image.open(out) if os.path.exists(out) else None
check(code == 0 and res is not None and res.size == (602, 400), f"2x: the exact size, though the model works in 16s ({res and res.size})")
progress = [int(l.split()[1].split("/")[0]) for l in text.splitlines() if l.startswith("progress ")]
check(progress == sorted(progress) and progress[-1] == 100 and len(progress) >= 5,
      f"the runner's phases become a progress bar ({progress})")
check("stage generate" in text and "done " + out in text and "compare " in text and os.path.exists(os.path.join(d, "cmp.png")),
      "says its stages, where it saved, and makes the before/after")
args = open(os.path.join(d, "args.txt")).read().splitlines()[-1]
check("--resolution 400" in args and "--max_resolution 602" in args and "--batch_size 1" in args
      and "--vae_decode_tiled" in args and "--blocks_to_swap" not in args,
      f"a small picture on a 12 GB card: tiled encoding only ({args})")

# ---------------------------------------------------------------- a chosen part, and how much is kept
import numpy as np  # noqa: E402
noisy = os.path.join(d, "noisy.png")
rng = np.random.default_rng(1)
Image.fromarray(rng.integers(0, 255, (400, 600, 3), dtype=np.uint8)).save(noisy)
code, text = run(noisy, os.path.join(d, "part.png"), '{"scale": 1}', "--part", "0.5,0.25,0.75,0.75")
args = open(os.path.join(d, "args.txt")).read().splitlines()[-1]
res = np.asarray(Image.open(os.path.join(d, "part.png"))).astype(int)
orig = np.asarray(Image.open(noisy)).astype(int)
check(code == 0 and res.shape == orig.shape and "--resolution 246" in args and "--max_resolution 296" in args,
      f"a part: only it and its margin go to the model (150 x 200 + 48 around: {args.split('--resolution ')[1].split()[0]} short side)")
check(np.abs(res[:, :200] - orig[:, :200]).max() == 0 and np.abs(res[150:250, 330:420] - orig[150:250, 330:420]).mean() > 1,
      "a part: the rest of the picture untouched, the part changed")
code, _ = run(src, os.path.join(d, "half.png"), '{"scale": 1, "amount": 0.5}')
check(code == 0 and Image.open(os.path.join(d, "half.png")).size == (301, 200), "Strength below 1: mixed with the original, the same size")
check(generate.parse_part("0.9,0.9,0.905,0.95", 100, 100) is None and generate.parse_part("x", 10, 10) is None,
      "a part too small or unreadable: the whole picture")

# ---------------------------------------------------------------- memory
check("--blocks_to_swap" in " ".join(generate.memory_args(3000, 4000, 12)), "12 MP on a 12 GB card: the model's blocks swapped")
check("--blocks_to_swap" not in " ".join(generate.memory_args(3000, 4000, 24)), "12 MP on a 24 GB card: all on the card")
small = generate.memory_args(1024, 1024, 4)
check("--blocks_to_swap 32" in " ".join(small) and "--vae_decode_tile_size 256" in " ".join(small),
      "a 4 GB card: the model swapped and the picture decoded in small tiles (it ran out of memory in 1024s on a GTX 1650)")
os.environ["FAKE_SEEDVR2_OOM"] = "1"
code, text = run(src, os.path.join(d, "oom.png"), '{"scale": 2}', "--vram", "12")
os.environ.pop("FAKE_SEEDVR2_OOM")
args = open(os.path.join(d, "args.txt")).read().splitlines()[-1]
check(code == 0 and "ran out of memory" in text and "--blocks_to_swap 32" in args and "--swap_io_components" in args,
      "out of memory: tried again with the model in the PC's memory, and said so")

# ---------------------------------------------------------------- transparency, JPEG, a failing runner
logo = picture("logo.png", 120, 80, "RGBA")
code, _ = run(logo, os.path.join(d, "logo out.png"), '{"scale": 4}')
res = Image.open(os.path.join(d, "logo out.png"))
check(code == 0 and res.mode == "RGBA" and res.size == (480, 320) and res.getpixel((10, 10))[3] < 10 and res.getpixel((470, 10))[3] > 240,
      "transparency kept, enlarged with the picture")
code, _ = run(src, os.path.join(d, "same.jpg"), '{"scale": 1}')
res = Image.open(os.path.join(d, "same.jpg"))
check(code == 0 and res.format == "JPEG" and res.size == (301, 200), f"1x as JPEG, exactly the same size ({res.size})")
bad = os.path.join(d, "bad-runner")
os.makedirs(bad, exist_ok=True)
open(os.path.join(bad, "inference_cli.py"), "w").write("import sys\nprint('boom')\nsys.exit(3)\n")
try:
    with contextlib.redirect_stdout(io.StringIO()):
        generate.main(["--image", src, "--out", os.path.join(d, "x.png"), "--runner", bad, "--models", d])
    check(False, "a failing runner is an error")
except RuntimeError as e:
    check("generate-runner.log" in str(e), f"a failing runner: a readable error pointing at its log ({e})")

# ---------------------------------------------------------------- the real runner's own argument parser
runner = os.environ.get("SEEDVR2_RUNNER")
if runner:
    source = open(os.path.join(runner, "inference_cli.py"), encoding="utf-8").read()
    fn = next(n for n in ast.parse(source).body if isinstance(n, ast.FunctionDef) and n.name == "parse_arguments")
    registry = open(os.path.join(runner, "src", "utils", "model_registry.py"), encoding="utf-8").read()
    names = re.findall(r'"([^"]+)":\s*ModelInfo\(', registry)
    import argparse
    import platform
    g = {"argparse": argparse, "sys": sys, "os": os, "platform": platform, "DEFAULT_DIT": names[0], "SEEDVR2_FOLDER_NAME": "SEEDVR2",
         "get_available_dit_models": lambda: [n for n in names if "vae" not in n]}
    exec(compile(ast.Module(body=[fn], type_ignores=[]), "inference_cli.py", "exec"), g)

    class A:
        python, runner, models, gpu, vram = sys.executable, runner, d, 0, 12
    for cautious in (False, True):
        cmd = generate.runner_args(A, "in.png", "out.png", 4000, 3000, cautious)
        saved = sys.argv
        sys.argv = cmd[1:]
        try:
            ns = g["parse_arguments"]()
            check(ns.dit_model == generate.DIT_MODEL and ns.resolution == 3000 and ns.max_resolution == 4000
                  and (ns.blocks_to_swap == 32) == cautious, f"the real runner accepts the command line (cautious={cautious})")
        except SystemExit:
            check(False, f"the real runner rejects the command line: {' '.join(cmd[2:])}")
        finally:
            sys.argv = saved
    check(generate.VAE_MODEL in names and generate.DIT_MODEL in names, "the models are in the runner's registry")
else:
    print("(SEEDVR2_RUNNER not set: the real runner's parser isn't checked)")

print()
print("ALL PASSED" if not fails else f"{fails} FAILED")
sys.exit(1 if fails else 0)
