# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Novabit
"""Prepares Enhance's AI models for this graphics card (TensorRT engines), run by setup-enhance.cmd
so the first export doesn't wait for it. Every model works on one fixed tile size (enhance.TILE), so
one engine per model serves every video.

    python enhance_build.py [all | model file names without .onnx; default: all]

All of them: every choice on the Enhance page, so no setting ever has to wait for a build. About
3 minutes a model on a GTX 1650, once; a few seconds each on an RTX card.
"""
import os
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
for p in (HERE, os.path.join(HERE, "mpv")):
    if p not in sys.path:
        sys.path.insert(0, p)

# The everyday ones first (Film light, Anime light), then the rest of the page's choices
ALL = ["enhance/realesr-general-mid-x4v3", "cugan/up2x-latest-conservative",
       "enhance/realesr-general-wdn-x4v3", "enhance/realesr-general-x4v3",
       "cugan/up2x-latest-no-denoise", "cugan/up2x-latest-denoise3x", "RealESRGANv2/realesr-animevideov3",
       "cugan/pro-conservative-up2x", "cugan/pro-no-denoise3x-up2x", "cugan/pro-denoise3x-up2x", "dpir",
       # Remove noise and blur for photos (on by default for them)
       "restore/nafnet_reds64",
       # Max detail for photos: the heaviest, last
       "enhance/RealESRGAN_x4plus_anime_6B", "enhance/RealESRGAN_x4plus"]


LOG = os.path.join(HERE, "prepare-models.log")


def say(line):
    """To the console and to runtime/prepare-models.log (collect-logs.cmd sends it): PowerShell's
    transcript of setup-enhance doesn't record what this script prints."""
    print(line, flush=True)
    try:
        with open(LOG, "a", encoding="utf-8") as f:
            f.write(time.strftime("%Y-%m-%d %H:%M:%S ") + line.strip() + "\n")
    except OSError:
        pass


def main(argv):
    import vapoursynth as vs
    import vsmlrt
    import enhance
    core = vs.core
    if not hasattr(core, "trt"):
        say("No TensorRT on this PC: the models run on Vulkan, which needs no preparing.")
        return 0
    import jobs
    names = [n for n in argv[1:] if n != "all"] or ALL

    def path_of(name):
        return os.path.join(vsmlrt.models_path, "dpir", "drunet_deblocking_color.onnx") if name == "dpir" \
            else os.path.join(vsmlrt.models_path, *name.split("/")) + ".onnx"

    names = [n for n in names if os.path.isfile(path_of(n))]     # installed ones (Real-CUGAN Pro may not be)
    total = sum(jobs.expected_seconds(f"enhance:{os.path.basename(path_of(n))}", "entry") for n in names)
    say(f"    {len(names)} models; about {total / 60:.0f} minutes on a GTX 16 card the first time, much less after"
        " (a model that's ready takes seconds). A line appears every 30 s while one is being prepared.")
    failed = 0
    for i, name in enumerate(names, 1):
        path = path_of(name)
        t0 = time.time()
        say(f"    [{i}/{len(names)}] preparing {os.path.basename(path)} ...")
        # Shows progress only while it really builds; waits its turn if a preview or export is building
        job = jobs.preparing("Preparing AI models for this graphics card", os.path.basename(path)[:-5], step=i, steps=len(names),
                             key=f"enhance:{os.path.basename(path)}", gpu_class="entry", say=say)
        try:
            with job:
              backend = enhance.backend({}, *enhance.TILE)
              if name == "dpir":
                  # Exactly as Fix compression runs it: its own tile size, through vs-mlrt's DPIR
                  frame = core.std.BlankClip(width=enhance.DPIR_TILE[0], height=enhance.DPIR_TILE[1], format=vs.RGBH, length=1)
                  out = vsmlrt.DPIR(frame, strength=20.0, model=vsmlrt.DPIRModel.drunet_deblocking_color,
                                    tilesize=enhance.DPIR_TILE, overlap=(0, 0), backend=backend)
              elif name.startswith("restore/"):
                  # Its own tile size, as enhance.restore runs it
                  frame = core.std.BlankClip(width=enhance.RESTORE_TILE[0], height=enhance.RESTORE_TILE[1], format=vs.RGBH, length=1)
                  out = vsmlrt.inference(frame, path, overlap=(0, 0), tilesize=enhance.RESTORE_TILE, backend=backend)
              else:
                  frame = core.std.BlankClip(width=enhance.TILE[0], height=enhance.TILE[1], format=vs.RGBH, length=1)
                  out = vsmlrt.inference(frame, path, overlap=(0, 0), tilesize=enhance.TILE, backend=backend)
              out.get_frame(0)
            say(f"    ready  {os.path.basename(path)} ({time.time() - t0:.0f} s)")
        except Exception as e:          # noqa: BLE001 - the export prepares it later instead
            failed += 1
            say(f"    failed {os.path.basename(path)}: {type(e).__name__}: {e}")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
