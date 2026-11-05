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
       "cugan/pro-conservative-up2x", "cugan/pro-no-denoise3x-up2x", "cugan/pro-denoise3x-up2x", "dpir"]


def main(argv):
    import vapoursynth as vs
    import vsmlrt
    import enhance
    core = vs.core
    if not hasattr(core, "trt"):
        print("No TensorRT on this PC: the models run on Vulkan, which needs no preparing.")
        return 0
    names = [n for n in argv[1:] if n != "all"] or ALL
    failed = 0
    for i, name in enumerate(names, 1):
        path = os.path.join(vsmlrt.models_path, "dpir", "drunet_deblocking_color.onnx") if name == "dpir" \
            else os.path.join(vsmlrt.models_path, *name.split("/")) + ".onnx"
        if not os.path.isfile(path):
            continue                       # not installed (e.g. Real-CUGAN Pro)
        t0 = time.time()
        print(f"    [{i}/{len(names)}] preparing {os.path.basename(path)} ...", flush=True)
        try:
            backend = enhance.backend({}, *enhance.TILE)
            if name == "dpir":
                # Exactly as Fix compression runs it: its own tile size, through vs-mlrt's DPIR
                frame = core.std.BlankClip(width=enhance.DPIR_TILE[0], height=enhance.DPIR_TILE[1], format=vs.RGBH, length=1)
                out = vsmlrt.DPIR(frame, strength=20.0, model=vsmlrt.DPIRModel.drunet_deblocking_color,
                                  tilesize=enhance.DPIR_TILE, overlap=(0, 0), backend=backend)
            else:
                frame = core.std.BlankClip(width=enhance.TILE[0], height=enhance.TILE[1], format=vs.RGBH, length=1)
                out = vsmlrt.inference(frame, path, overlap=(0, 0), tilesize=enhance.TILE, backend=backend)
            out.get_frame(0)
            print(f"    ready  {os.path.basename(path)} ({time.time() - t0:.0f} s)", flush=True)
        except Exception as e:          # noqa: BLE001 - the export prepares it later instead
            failed += 1
            print(f"    failed {os.path.basename(path)}: {type(e).__name__}: {e}", flush=True)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
