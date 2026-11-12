"""Test of generative video upscaling (SeedVR2) on a few seconds of a video.

Uses what the app's Generative part already installed (runtime\\generate: its Python, the runner and
the model), so nothing is downloaded. For each size asked for it writes a video next to the source,
plus the same seconds of the source, and a results.txt with the time each one took.

    python generative_video_test.py VIDEO [--start 10] [--seconds 3] [--sizes 1080,2160]
"""
import argparse
import glob
import json
import os
import shutil
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))


def find_runtime(start):
    for d in (os.path.join(start, "runtime"), os.path.join(start, "..", "runtime"), start):
        if os.path.isfile(os.path.join(d, "generate", "ready.txt")) or os.path.isdir(os.path.join(d, "generate", "runner")):
            return os.path.abspath(d)
    sys.exit("Couldn't find NOVA Studio's runtime folder. Put this file in the NOVA Studio folder, "
             "and add Generative on the AI Models page first.")


def card():
    import torch
    if not torch.cuda.is_available():
        sys.exit("This needs an NVIDIA graphics card with CUDA.")
    p = torch.cuda.get_device_properties(0)
    return p.name, p.total_memory / 1024 ** 3


def video_info(path):
    import cv2
    cap = cv2.VideoCapture(path)
    if not cap.isOpened():
        sys.exit(f"Couldn't open {path}")
    info = (cap.get(cv2.CAP_PROP_FPS) or 24.0, int(cap.get(cv2.CAP_PROP_FRAME_COUNT)),
            int(cap.get(cv2.CAP_PROP_FRAME_WIDTH)), int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT)))
    cap.release()
    return info


def dit_model(models):
    for name in ("seedvr2_ema_3b_fp8_e4m3fn.safetensors", "seedvr2_ema_3b-Q4_K_M.gguf"):
        if glob.glob(os.path.join(models, "**", name), recursive=True):
            return name
    sys.exit("The Generative model isn't installed yet: add Generative on the AI Models page first.")


def batches(vram):
    """Frames the model sees at once (4n+1). On a 12 GB RTX 4070, 21 frames of 1080p overflowed into
    system memory: no error, just 5 minutes to encode one batch, so smaller cards start at 9."""
    return [21, 9, 5] if vram >= 16 else [9, 5]


def memory_args(out_w, out_h, vram):
    args = ["--vae_encode_tiled", "--vae_decode_tiled"]
    if vram < 16:
        # 512-pixel tiles both ways: the runner's 1024 for encoding overflowed 12 GB at 1080p
        args += ["--vae_encode_tile_size", "512", "--vae_encode_tile_overlap", "64",
                 "--vae_decode_tile_size", "512", "--vae_decode_tile_overlap", "64"]
    if vram < 16 and out_w * out_h > 4e6:
        args += ["--dit_offload_device", "cpu", "--blocks_to_swap", "16"]
    return args


def run(cmd, log):
    env = dict(os.environ, PYTHONUNBUFFERED="1", PYTHONIOENCODING="utf-8")
    p = subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
                         encoding="utf-8", errors="replace", env=env, cwd=os.path.dirname(cmd[1]))
    oom = False
    for line in p.stdout:
        log.write(line)
        log.flush()
        if "Phase" in line or "Batch" in line or "rror" in line:
            print("   " + line.strip()[:110])
        if "out of memory" in line.lower() or "OutOfMemoryError" in line:
            oom = True
    return p.wait(), oom


def encode(mpv, frames, fps, out, log):
    """PNG frames to an H.264 video at high quality, with the app's own mpv."""
    files = sorted(glob.glob(os.path.join(frames, "**", "*.png"), recursive=True))
    if not files:
        return False
    lst = os.path.join(frames, "list.txt")
    with open(lst, "w", encoding="utf-8") as f:
        f.write("\n".join(files))
    for codec, opts in (("libx264", "crf=12,preset=slow"), ("h264_nvenc", "rc=constqp,qp=14,preset=p7"),
                        ("hevc_nvenc", "rc=constqp,qp=16,preset=p7")):
        try:
            r = subprocess.run([mpv, "--no-config", f"mf://@{lst}", f"--mf-fps={fps}", f"--o={out}",
                                f"--ovc={codec}", f"--ovcopts={opts}"], stdout=log, stderr=subprocess.STDOUT)
        except OSError:
            return False
        if r.returncode == 0 and os.path.isfile(out) and os.path.getsize(out) > 1000:
            return True
    return False


def cut_source(mpv, video, start, seconds, out, log):
    """The same seconds of the source, for comparing (no sound)."""
    try:
        subprocess.run([mpv, "--no-config", video, f"--start={start}", f"--length={seconds}", "--no-audio",
                        f"--o={out}", "--ovc=libx264", "--ovcopts=crf=12,preset=slow"],
                       stdout=log, stderr=subprocess.STDOUT)
    except OSError:
        pass


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("video")
    p.add_argument("--start", type=float, default=0, help="where to start, in seconds")
    p.add_argument("--seconds", type=float, default=3, help="how many seconds to do (default 3)")
    p.add_argument("--sizes", default="1080,2160", help="output heights (short side), e.g. 1080,2160")
    p.add_argument("--runtime", default="")
    a = p.parse_args()

    rt = os.path.abspath(a.runtime) if a.runtime else find_runtime(HERE)
    gen = os.path.join(rt, "generate")
    python = sys.executable
    runner = os.path.join(gen, "runner", "inference_cli.py")
    models = os.path.join(gen, "models")
    mpv = os.path.join(rt, "mpv", "mpv.exe") if os.name == "nt" else (shutil.which("mpv") or "mpv")
    video = os.path.abspath(a.video)
    name, vram = card()
    fps, total, w, h = video_info(video)
    first = int(round(a.start * fps))
    count = max(1, min(int(round(a.seconds * fps)), total - first if total > 0 else 10 ** 9))
    model = dit_model(models)
    out_dir = os.path.join(os.path.dirname(video), os.path.splitext(os.path.basename(video))[0] + " - generative test")
    os.makedirs(out_dir, exist_ok=True)
    log = open(os.path.join(out_dir, "log.txt"), "a", encoding="utf-8")
    print(f"{name}, {vram:.0f} GB. Source {w}x{h} at {fps:.3f} fps; doing {count} frames from {a.start:g} s.")
    print(f"Model {model}. Results go to:\n   {out_dir}\n")
    print("If a phase takes many minutes with the graphics card's shared memory filling up in Task Manager,\n"
          "the card ran out of its own memory and Windows is using system memory instead (very slow).\n"
          "NVIDIA Control Panel > Manage 3D settings > CUDA - Sysmem Fallback Policy > Prefer No Sysmem\n"
          "Fallback turns that into an error, and this test then retries with fewer frames.\n")

    results = {"gpu": name, "vram_gb": round(vram, 1), "source": f"{w}x{h}", "fps": round(fps, 3),
               "frames": count, "start_s": a.start, "model": model, "runs": []}
    cut_source(mpv, video, a.start, count / fps, os.path.join(out_dir, "0 source.mp4"), log)

    for size in [int(s) for s in a.sizes.split(",") if s.strip()]:
        if h <= w:
            out_h, out_w = size, round(size * w / h / 2) * 2
        else:
            out_w, out_h = size, round(size * h / w / 2) * 2
        frames = os.path.join(out_dir, f"frames {size}")
        print(f"{out_w}x{out_h}:")
        run_info = {"size": f"{out_w}x{out_h}"}
        tries = batches(vram)
        for batch in tries:
            shutil.rmtree(frames, ignore_errors=True)
            cmd = [python, runner, video, "--output", frames, "--output_format", "png", "--model_dir", models,
                   "--dit_model", model, "--resolution", str(min(out_w, out_h)), "--max_resolution",
                   str(max(out_w, out_h)), "--skip_first_frames", str(first), "--load_cap", str(count),
                   "--batch_size", str(batch), "--uniform_batch_size", "--temporal_overlap", "2",
                   "--chunk_size", str(max(batch * 2 + 2, 24)), "--seed", "42", "--color_correction", "lab",
                   "--cuda_device", "0"] + memory_args(out_w, out_h, vram)
            log.write("\n> " + " ".join(cmd) + "\n")
            t = time.time()
            code, oom = run(cmd, log)
            took = time.time() - t
            if code == 0 and glob.glob(os.path.join(frames, "**", "*.png"), recursive=True):
                run_info.update(batch=batch, seconds=round(took), seconds_per_frame=round(took / count, 2))
                break
            print(f"   {'Ran out of graphics memory' if oom else 'Failed'} at {batch} frames at once"
                  + (", trying fewer." if batch != tries[-1] else "."))
            run_info.setdefault("failed", []).append({"batch": batch, "oom": oom, "seconds": round(took)})
        else:
            results["runs"].append(run_info)
            continue
        mp4 = os.path.join(out_dir, f"{size}p generative.mp4")
        if encode(mpv, frames, fps, mp4, log):
            shutil.rmtree(frames, ignore_errors=True)
            run_info["video"] = os.path.basename(mp4)
        else:
            run_info["video"] = f"(couldn't encode: the frames are in '{os.path.basename(frames)}')"
        mins, secs = divmod(run_info["seconds"], 60)
        print(f"   Done in {mins} min {secs} s ({run_info['seconds_per_frame']} s a frame, {batch} frames at once).\n")
        results["runs"].append(run_info)

    with open(os.path.join(out_dir, "results.txt"), "w", encoding="utf-8") as f:
        json.dump(results, f, indent=2)
    print("Finished. Send me results.txt and the videos in:\n   " + out_dir)
    if os.name == "nt":
        subprocess.Popen(["explorer", out_dir])


if __name__ == "__main__":
    main()
