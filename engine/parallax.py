# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Novabit
"""NOVA Studio: animate a picture with a 3D camera move (depth parallax).

The picture's depth comes from Depth Anything V2 Small (Apache-2.0) on onnxruntime with DirectML, so
it runs on any DirectX 12 graphics card, a GTX 1650 or Intel Xe included, or on the processor. The
camera move is then rendered in numpy: every output pixel looks up where it came from in the picture,
nearer parts moving more than farther ones, and the frames go to mpv to be encoded as H.264.

    python parallax.py --image in.png --out out.mp4 --move push --seconds 6 --strength 1 --fps 30
                       --model depth.onnx --mpv mpv.exe [--packages dir]

Prints "stage <name>", "progress <0..1>" and finally "done <path>" on stdout, one per line.
An --out ending in .rgb writes the raw RGB frames instead of a video (used by the tests).
"""
import argparse
import math
import os
import subprocess
import sys
import time

# One-way moves ease in and out over the clip; looping ones end where they began
MOVES = ("push", "pull", "drift", "rise", "sway", "orbit")
LOOPING = ("sway", "orbit")

# At strength 1 the nearest part of the picture shifts this share of the width against the farthest
SHIFT = 0.035
# ... and a push-in makes the nearest part this much larger
DOLLY = 0.07
# The depth that stays still: a little behind the middle, so the background moves gently the other way
FOCUS = 0.35


def say(*parts):
    print(*parts, flush=True)


def parse_args(argv=None):
    p = argparse.ArgumentParser(description="Animate a picture with a 3D camera move.")
    p.add_argument("--image", required=True)
    p.add_argument("--out", required=True)
    p.add_argument("--move", choices=MOVES, default="push")
    p.add_argument("--seconds", type=float, default=6.0)
    p.add_argument("--strength", type=float, default=1.0)
    p.add_argument("--fps", type=float, default=30.0)
    p.add_argument("--max-width", type=int, default=1920)
    p.add_argument("--max-height", type=int, default=1080)
    p.add_argument("--model", required=True, help="Depth Anything V2 Small ONNX file")
    p.add_argument("--mpv", default="mpv", help="mpv executable that encodes the video")
    p.add_argument("--packages", default="", help="folder holding numpy, onnxruntime and Pillow")
    p.add_argument("--cpu", action="store_true", help="work out depth on the processor")
    a = p.parse_args(argv)
    a.seconds = min(30.0, max(1.0, a.seconds))
    a.strength = min(3.0, max(0.1, a.strength))
    a.fps = min(120.0, max(1.0, a.fps))
    return a


# ---------------------------------------------------------------- picture and depth

def output_size(w, h, max_w, max_h):
    """The video size: the picture fitted within the box (turned for portrait pictures), even numbers."""
    if h > w:
        max_w, max_h = min(max_w, max_h), max(max_w, max_h)
    k = min(1.0, max_w / w, max_h / h)
    return max(2, int(w * k) // 2 * 2), max(2, int(h * k) // 2 * 2)


def model_size(w, h, long_side=518):
    """The size the depth model sees: about 518 on the long side, both multiples of 14."""
    k = long_side / max(w, h)
    return max(14, round(w * k / 14) * 14), max(14, round(h * k / 14) * 14)


def load_picture(path, max_w, max_h):
    from PIL import Image, ImageOps
    import numpy as np
    img = ImageOps.exif_transpose(Image.open(path)).convert("RGB")
    w, h = output_size(img.width, img.height, max_w, max_h)
    if (w, h) != img.size:
        img = img.resize((w, h), Image.LANCZOS)
    return img, np.asarray(img, dtype=np.uint8)


def depth_session(model, cpu=False):
    import onnxruntime as ort
    available = ort.get_available_providers()
    order = [] if cpu else ["DmlExecutionProvider"]
    providers = [p for p in order if p in available] + ["CPUExecutionProvider"]
    opts = ort.SessionOptions()
    opts.log_severity_level = 3
    try:
        return ort.InferenceSession(model, opts, providers=providers)
    except Exception:
        if providers == ["CPUExecutionProvider"]:
            raise
        return ort.InferenceSession(model, opts, providers=["CPUExecutionProvider"])


def estimate_depth(session, img):
    """Relative depth at the picture's size, 0 far to 1 near."""
    from PIL import Image
    import numpy as np
    mw, mh = model_size(img.width, img.height)
    x = np.asarray(img.resize((mw, mh), Image.BICUBIC), dtype=np.float32) / 255.0
    x = (x - np.array([0.485, 0.456, 0.406], np.float32)) / np.array([0.229, 0.224, 0.225], np.float32)
    x = x.transpose(2, 0, 1)[None]
    name = session.get_inputs()[0].name
    raw = np.asarray(session.run(None, {name: x})[0], dtype=np.float32).squeeze()
    lo, hi = np.percentile(raw, 2), np.percentile(raw, 98)
    d = np.clip((raw - lo) / max(hi - lo, 1e-6), 0.0, 1.0).astype(np.float32)
    d = np.asarray(Image.fromarray(d, mode="F").resize(img.size, Image.BILINEAR), dtype=np.float32)
    return prepare_depth(d)


def box_blur(a, r):
    """Mean over a (2r+1) square, edges repeated."""
    import numpy as np
    if r < 1:
        return a
    for axis in (0, 1):
        p = np.pad(a, [(r + 1, r) if i == axis else (0, 0) for i in range(2)], mode="edge")
        c = np.cumsum(p, axis=axis, dtype=np.float64)
        hi = np.take(c, np.arange(2 * r + 1, c.shape[axis]), axis=axis)
        lo = np.take(c, np.arange(0, c.shape[axis] - 2 * r - 1), axis=axis)
        a = ((hi - lo) / (2 * r + 1)).astype(np.float32)
    return a


def grow_near(a, r):
    """Each pixel takes the nearest depth around it, so foreground edges hold together and the
    stretching at a depth jump happens in the background, where it shows least."""
    import numpy as np
    if r < 1:
        return a
    for axis in (0, 1):
        p = np.pad(a, [(r, r) if i == axis else (0, 0) for i in range(2)], mode="edge")
        out = a.copy()
        n = a.shape[axis]
        for s in range(2 * r + 1):
            np.maximum(out, np.take(p, np.arange(s, s + n), axis=axis), out=out)
        a = out
    return a


def prepare_depth(d):
    r = max(1, round(max(d.shape) * 0.004))
    return box_blur(grow_near(d, r), r)


# ---------------------------------------------------------------- the camera move

def ease(t):
    return 0.5 - 0.5 * math.cos(math.pi * t)


def camera(move, t, strength):
    """(shift x, shift y, dolly) at time t in [0, 1): shifts as shares of the width."""
    a, z = SHIFT * strength, DOLLY * strength
    e = ease(t)
    if move == "push":
        return 0.0, 0.0, z * e
    if move == "pull":
        return 0.0, 0.0, z * (1.0 - e)
    if move == "drift":
        return a * (e - 0.5), 0.0, z * 0.15
    if move == "rise":
        return 0.0, -a * 0.8 * (e - 0.5), z * 0.15
    w = 2.0 * math.pi * t
    if move == "sway":
        return 0.5 * a * math.sin(w), 0.0, z * 0.1
    return 0.5 * a * math.sin(w), 0.25 * a * math.cos(w), z * 0.1    # orbit


def margin_zoom(move, strength, aspect):
    """How much to enlarge every frame so the moved picture always covers it: no empty edges."""
    worst = 0.0
    for i in range(65):
        sx, sy, z = camera(move, i / 64.0, strength)
        worst = max(worst, abs(sx) * max(FOCUS, 1 - FOCUS), abs(sy) * aspect * max(FOCUS, 1 - FOCUS))
    return 1.0 + 2.0 * worst + 0.01


class Renderer:
    """Draws frames of the move. Each pixel is packed into one 32-bit value so a lookup costs one
    gather, and each frame is split into strips drawn on all processor threads (numpy lets go of
    Python's lock while it works)."""

    def __init__(self, rgb, depth, move, strength, threads=None):
        import numpy as np
        self.np = np
        self.h, self.w = depth.shape
        r, g, b = (rgb[..., i].astype(np.uint32) for i in range(3))
        self.packed = (r | (g << 8) | (b << 16)).reshape(-1)
        self.depth = np.ascontiguousarray(depth, dtype=np.float32).reshape(-1)
        self.move, self.strength = move, strength
        self.zoom = margin_zoom(move, strength, self.w / self.h)
        self.cx, self.cy = (self.w - 1) / 2.0, (self.h - 1) / 2.0
        # Output pixel, before the move, as a position in the picture
        self.gx = (self.cx + (np.arange(self.w, dtype=np.float32) - self.cx) / self.zoom).astype(np.float32)
        self.gy = (self.cy + (np.arange(self.h, dtype=np.float32) - self.cy) / self.zoom).astype(np.float32)
        self.threads = max(1, min(16, threads or os.cpu_count() or 1))
        rows = max(8, -(-self.h // (self.threads * 2)))
        self.strips = [(y, min(self.h, y + rows)) for y in range(0, self.h, rows)]
        self.pool = None
        if self.threads > 1 and len(self.strips) > 1:
            from concurrent.futures import ThreadPoolExecutor
            self.pool = ThreadPoolExecutor(self.threads)

    def close(self):
        if self.pool is not None:
            self.pool.shutdown()

    def _corners(self, x, y):
        np = self.np
        np.clip(x, 0.0, self.w - 1.001, out=x)
        np.clip(y, 0.0, self.h - 1.001, out=y)
        x0 = x.astype(np.int32)
        y0 = y.astype(np.int32)
        fx = x - x0
        fy = y - y0
        i = y0 * self.w + x0
        return i, fx, fy

    def _strip(self, cam, y0, y1, out):
        np = self.np
        sx, sy, z = cam
        n = y1 - y0
        gx = np.broadcast_to(self.gx, (n, self.w)).reshape(-1)
        gy = np.repeat(self.gy[y0:y1], self.w)
        ax, ay = gx - self.cx, gy - self.cy
        px, py = gx.copy(), gy.copy()
        # p = c + (q - c - shift * (d - focus)) / (1 + dolly * (0.25 + d)), with d the depth at p itself:
        # two quick nearest-pixel passes, then one exact one
        for exact in (False, False, True):
            np.clip(px, 0.0, self.w - 1.001, out=px)
            np.clip(py, 0.0, self.h - 1.001, out=py)
            if exact:
                i, fx, fy = self._corners(px, py)
                dd = self.depth
                top = dd[i] + (dd[i + 1] - dd[i]) * fx
                d = top + (dd[i + self.w] + (dd[i + self.w + 1] - dd[i + self.w]) * fx - top) * fy
            else:
                d = self.depth[(py + 0.5).astype(np.int32) * self.w + (px + 0.5).astype(np.int32)]
            s = 1.0 / (1.0 + z * (0.25 + d))
            off = d - FOCUS
            px = self.cx + (ax - sx * off) * s
            py = self.cy + (ay - sy * off) * s
        i, fx, fy = self._corners(px, py)
        fx = fx[:, None]
        fy = fy[:, None]
        pk = self.packed

        def get(j):
            return pk[j].view(np.uint8).reshape(-1, 4)[:, :3].astype(np.float32)

        a, b = get(i), get(i + 1)
        top = a + (b - a) * fx
        a, b = get(i + self.w), get(i + self.w + 1)
        rgb = top + (a + (b - a) * fx - top) * fy
        rgb += 0.5
        out[y0:y1] = rgb.reshape(n, self.w, 3).astype(np.uint8)

    def frame(self, t):
        np = self.np
        sx, sy, z = camera(self.move, t, self.strength)
        cam = (sx * self.w, sy * self.w, z)
        out = np.empty((self.h, self.w, 3), np.uint8)
        if self.pool is None:
            for y0, y1 in self.strips:
                self._strip(cam, y0, y1, out)
        else:
            list(self.pool.map(lambda s: self._strip(cam, s[0], s[1], out), self.strips))
        return out


# ---------------------------------------------------------------- the video

def encoder(args, w, h):
    """A pipe that takes raw RGB frames: mpv encoding them as H.264, or a raw file for the tests."""
    if args.out.lower().endswith(".rgb"):
        return None, open(args.out, "wb")
    cmd = [args.mpv, "-", "--no-config", "--msg-level=all=error",
           "--demuxer=rawvideo", f"--demuxer-rawvideo-w={w}", f"--demuxer-rawvideo-h={h}",
           "--demuxer-rawvideo-mp-format=rgb24", f"--demuxer-rawvideo-fps={args.fps:g}",
           "--vf=format=fmt=yuv420p:colormatrix=bt.709:colorlevels=limited",
           f"--o={args.out}", "--of=mp4", "--ovc=libx264", "--ovcopts=crf=17,preset=slow,profile=high"]
    flags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
    proc = subprocess.Popen(cmd, stdin=subprocess.PIPE, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE, creationflags=flags)
    return proc, proc.stdin


def main(argv=None):
    args = parse_args(argv)
    if args.packages:
        sys.path.insert(0, args.packages)
    started = time.time()
    say("stage picture")
    img, rgb = load_picture(args.image, args.max_width, args.max_height)
    say("stage depth")
    depth = estimate_depth(depth_session(args.model, args.cpu), img)
    say("stage frames")
    r = Renderer(rgb, depth, args.move, args.strength)
    n = max(2, round(args.seconds * args.fps))
    proc, sink = encoder(args, r.w, r.h)
    try:
        for i in range(n):
            # Loops stop one frame short of the start, so they play round seamlessly
            t = i / n if args.move in LOOPING else i / (n - 1)
            sink.write(r.frame(t).tobytes())
            if i % 5 == 0 or i == n - 1:
                say(f"progress {(i + 1) / n:.3f}")
    except BrokenPipeError:
        pass
    finally:
        sink.close()
        r.close()
    if proc is not None:
        err = proc.stderr.read().decode("utf-8", "replace").strip()
        if proc.wait() != 0 or not os.path.exists(args.out):
            raise RuntimeError("mpv could not write the video" + (": " + err if err else ""))
    say(f"took {time.time() - started:.1f} s, {n} frames at {r.w}x{r.h}")
    say(f"done {args.out}")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as ex:      # one readable line for the app; the details for the log
        import traceback
        traceback.print_exc()
        print(f"error {ex}", flush=True)
        sys.exit(1)
