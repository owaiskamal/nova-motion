# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Novabit
"""Prepares Enhance's AI models for this graphics card (TensorRT engines), run by setup-enhance.cmd
so the first export doesn't wait for it. Every model works on one fixed tile size (enhance.TILE), so
one engine per model serves every video.

    python enhance_build.py [model file names without .onnx, default: the two everyday ones]
"""
import os
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
for p in (HERE, os.path.join(HERE, "mpv")):
    if p not in sys.path:
        sys.path.insert(0, p)

# Film light and Anime light: the presets most exports use
EVERYDAY = ["enhance/realesr-general-mid-x4v3", "cugan/up2x-latest-conservative"]


def main(argv):
    import vapoursynth as vs
    import vsmlrt
    import enhance
    core = vs.core
    if not hasattr(core, "trt"):
        print("No TensorRT on this PC: the models run on Vulkan, which needs no preparing.")
        return 0
    names = argv[1:] or EVERYDAY
    failed = 0
    for name in names:
        path = os.path.join(vsmlrt.models_path, *name.split("/")) + ".onnx"
        if not os.path.isfile(path):
            print(f"    skip   {name} (not installed)")
            continue
        t0 = time.time()
        print(f"    preparing {os.path.basename(path)} ...", flush=True)
        try:
            frame = core.std.BlankClip(width=enhance.TILE[0], height=enhance.TILE[1], format=vs.RGBH, length=1)
            out = vsmlrt.inference(frame, path, overlap=(0, 0), tilesize=enhance.TILE, backend=enhance.backend({}, *enhance.TILE))
            out.get_frame(0)
            print(f"    ready  {os.path.basename(path)} ({time.time() - t0:.0f} s)", flush=True)
        except Exception as e:          # noqa: BLE001 - the export prepares it later instead
            failed += 1
            print(f"    failed {os.path.basename(path)}: {type(e).__name__}: {e}", flush=True)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
