# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Novabit
"""Builds one TensorRT engine in the background, started by novamotion_trt when a video needs an
engine that isn't built yet. The player smooths with MVTools meanwhile instead of freezing; when
this finishes, the engine is recorded in trt-engines/novamotion-engines.json and the app switches.

    python novamotion_build.py <model, e.g. v4_6> <padded width> <padded height> <gpu> <streams> <lock file>

The engine is made exactly as playback asks for it: same model, size, format and backend options.
"""
import os
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
MPV = os.path.join(HERE, "mpv")
for p in (HERE, MPV):
    if p not in sys.path:
        sys.path.insert(0, p)


def main(argv):
    model_name, width, height, gpu, streams, lock = argv[1], int(argv[2]), int(argv[3]), int(argv[4]), int(argv[5]), argv[6]
    os.environ["NOVAMOTION_BUILD_ENGINES"] = "1"
    log = os.path.join(HERE, "trt-engines", "background-build.log")
    t0 = time.time()
    try:
        import vapoursynth as vs
        import novamotion_trt as t
        from vsmlrt import RIFEMerge, RIFEModel
        core = vs.core
        model = getattr(RIFEModel, model_name)
        frame = core.std.BlankClip(width=width, height=height, format=vs.RGBH, length=4, fpsnum=60)
        mask = core.std.BlankClip(frame, format=vs.GRAYH, color=0.5)
        import jobs
        with jobs.preparing("Preparing smoothing for this video size", f"RIFE {model.name} at {width}x{height}, in the background",
                      key=f"rife:{model.name}:{width}x{height}"):
            merged = RIFEMerge(frame, frame, mask, model=model, backend=t.backend(gpu, streams or None))
            merged.get_frame(0)
        t.record_engine(width, height, model.name)
        result = f"built {model.name} at {width}x{height} in {time.time() - t0:.0f} s"
        code = 0
    except Exception as e:        # noqa: BLE001 - written to the log; the player keeps MVTools
        result = f"failed {model_name} at {width}x{height}: {type(e).__name__}: {e}"
        code = 1
    finally:
        try:
            os.remove(lock)
        except OSError:
            pass
    try:
        with open(log, "a", encoding="utf-8") as f:
            f.write(time.strftime("%Y-%m-%d %H:%M:%S ") + result + "\n")
    except OSError:
        pass
    return code


if __name__ == "__main__":
    sys.exit(main(sys.argv))
