# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Novabit
"""Progress of AI preparation the app can show: TensorRT building an engine takes minutes on GTX 16
cards and reports no progress of its own, so each build says what it is, which step of how many,
when it started and how long it should take, in runtime/jobs/<id>.json. The app reads the folder
and shows a progress bar on every page.

How long a build takes is learned on this PC (trt-engines/build-times.json): the same model's last
build, else the typical build here, else a conservative first guess by graphics class.
"""
import contextlib
import json
import os
import subprocess
import threading
import time
import uuid

HERE = os.path.dirname(os.path.abspath(__file__))
JOBS_DIR = os.path.join(HERE, "jobs")
TIMES = os.path.join(HERE, "trt-engines", "build-times.json")

FIRST_GUESS = {"entry": 240.0, "integrated": 300.0}     # seconds a build; others 60
# One TensorRT build at a time on this PC, whichever process asks (a preview, an export, setup, the
# player's background build). Two at once on a 4 GB GTX 1650 reset the graphics driver mid-build
# ("device removed"); every build in progress failed and started over the next time.
LOCK = os.path.join(HERE, "trt-engines", "build.lock")
DEFAULT_TITLE = "Preparing an AI model for this graphics card (once)"


def _load_times():
    try:
        with open(TIMES, encoding="utf-8-sig") as f:
            data = json.load(f)
        return data if isinstance(data, dict) else {}
    except (OSError, ValueError):
        return {}


def weight(key):
    """How long a network takes to prepare next to a compact one (Real-ESRGAN v3, RIFE): Real-CUGAN is
    a full U-Net and took over 10 minutes on a GTX 1650 where the compact models take 3-6."""
    k = key.lower()
    # Real-ESRGAN x4plus (Max detail) is a full RRDB network: TensorRT tries its options layer by layer,
    # and x4plus has about ten times the layers of the compact models (the anime 6B version about three)
    if "x4plus_anime_6b" in k:
        return 3.0
    if "x4plus" in k:
        return 10.0
    if "cugan" in k or "up2x" in k or "pro-" in k:
        return 3.0
    if "drunet" in k or "dpir" in k:
        return 2.0
    # NAFNet (Remove noise and blur): 36 blocks of simple layers, about Real-CUGAN's build
    if "nafnet" in k:
        return 3.0
    return 1.0


def expected_seconds(key, gpu_class=None):
    times = _load_times()
    if isinstance(times.get(key), (int, float)):
        return float(times[key])
    # The typical build on this PC, scaled to this network
    known = sorted(v / weight(k) for k, v in times.items() if isinstance(v, (int, float)))
    if known:
        return known[len(known) // 2] * weight(key)
    return FIRST_GUESS.get(gpu_class or "", 60.0) * weight(key)


def record_seconds(key, seconds):
    times = _load_times()
    times[key] = round(float(seconds), 1)
    try:
        os.makedirs(os.path.dirname(TIMES), exist_ok=True)
        tmp = TIMES + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(times, f, indent=1)
        os.replace(tmp, TIMES)
    except OSError:
        pass


class Job:
    """with Job("Preparing AI models", "realesr-general-x4v3", step=3, steps=9, key=...): <the build>

    Writes the job file on entry, removes it on exit, and on success records how long it took.
    With say=print, it also prints a line every 30 s, so a console never looks stuck."""

    def __init__(self, title, detail, step=1, steps=1, key=None, gpu_class=None, say=None):
        self.title, self.detail, self.step, self.steps = title, detail, step, steps
        self.key = key or detail
        self.expected = expected_seconds(self.key, gpu_class)
        self.path = os.path.join(JOBS_DIR, f"{uuid.uuid4().hex[:10]}.json")
        self.say = say
        self._stop = threading.Event()

    def _write(self):
        try:
            os.makedirs(JOBS_DIR, exist_ok=True)
            tmp = self.path + ".tmp"
            with open(tmp, "w", encoding="utf-8") as f:
                json.dump({"title": self.title, "detail": self.detail, "step": self.step, "steps": self.steps,
                           "started": self.started, "expected_s": self.expected, "pid": os.getpid()}, f)
            os.replace(tmp, self.path)
        except OSError:
            pass

    def update(self, detail=None, restart=False):
        """A new detail line, and with restart the clock starts again (after waiting its turn)."""
        if detail is not None:
            self.detail = detail
        if restart:
            self.started = time.time()
        self._write()

    def _heartbeat(self):
        while not self._stop.wait(30):
            spent = time.time() - self.started
            left = max(0, self.expected - spent)
            if spent > self.expected * 1.2:
                tail = ", longer than expected on this card; still working (it can't show finer progress)"
            elif left > 30:
                tail = f", about {left / 60:.0f} min left"
            else:
                tail = ", should finish soon"
            self.say(f"      still working: {spent / 60:.0f} min so far{tail} ...")

    def __enter__(self):
        self.started = time.time()
        self._write()
        if self.say:
            threading.Thread(target=self._heartbeat, daemon=True).start()
        return self

    def __exit__(self, kind, value, tb):
        self._stop.set()
        if kind is None:
            took = time.time() - self.started
            if took > 5:                    # a real build, not an engine that was already there
                record_seconds(self.key, took)
        try:
            os.remove(self.path)
        except OSError:
            pass
        return False


# --- Builds: only real ones show progress, and one at a time ------------------------------------
#
# vs-mlrt decides itself whether an engine is already there; when it isn't, it runs trtexec through
# subprocess.run (the only thing it runs that way). The progress job and the build lock wrap exactly
# that call, so loading a ready model shows nothing and never waits behind another build.

_current = threading.local()


@contextlib.contextmanager
def _lock_file(on_wait=None):
    os.makedirs(os.path.dirname(LOCK), exist_ok=True)
    f = open(LOCK, "a+")
    waited = False
    try:
        while True:
            try:
                f.seek(0)
                if os.name == "nt":
                    import msvcrt
                    msvcrt.locking(f.fileno(), msvcrt.LK_NBLCK, 1)
                else:
                    import fcntl
                    fcntl.flock(f.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
                break
            except OSError:
                if not waited and on_wait:
                    on_wait()
                waited = True
                time.sleep(1)
        yield waited
    finally:
        try:
            f.seek(0)
            if os.name == "nt":
                import msvcrt
                msvcrt.locking(f.fileno(), msvcrt.LK_UNLCK, 1)
            else:
                import fcntl
                fcntl.flock(f.fileno(), fcntl.LOCK_UN)
        except OSError:
            pass
        f.close()


def _arg(args, prefix):
    for a in args if isinstance(args, (list, tuple)) else []:
        if isinstance(a, str) and a.startswith(prefix):
            return a[len(prefix):]
    return None


def guarded_run(run, args, *a, **k):
    """subprocess.run for a TensorRT build: waits its turn, skips the build if another process made
    the same engine meanwhile, and shows progress while it builds."""
    info = dict(getattr(_current, "info", None) or {})
    if not info:
        onnx = _arg(args, "--onnx=") or ""
        name = os.path.basename(onnx)
        info = {"title": DEFAULT_TITLE, "detail": name[:-5] if name.endswith(".onnx") else name or "AI model",
                "key": f"enhance:{name}" if name else None}
    detail = info.get("detail", "")
    job = Job(info.get("title", DEFAULT_TITLE), detail, step=info.get("step", 1), steps=info.get("steps", 1),
              key=info.get("key"), gpu_class=info.get("gpu_class"), say=info.get("say"))
    with job:
        def waiting():
            job.update(f"{detail} · waiting for another AI preparation to finish")
            if job.say:
                job.say("      waiting for another AI preparation on this PC to finish first ...")
        with _lock_file(waiting):
            engine = _arg(args, "--saveEngine=")
            if engine and os.path.isfile(engine) and os.path.getsize(engine) >= 1024:
                job.started = time.time()          # made by the other process: nothing to record
                return subprocess.CompletedProcess(args, 0)
            job.update(detail, restart=True)
            return run(args, *a, **k)


class _GuardedSubprocess:
    def __init__(self, real):
        self._real = real

    def __getattr__(self, name):
        return getattr(self._real, name)

    def run(self, args, *a, **k):
        return guarded_run(self._real.run, args, *a, **k)


def install(module=None):
    """Routes vs-mlrt's builds through guarded_run (idempotent)."""
    if module is None:
        import vsmlrt as module
    sp = getattr(module, "subprocess", None)
    if not isinstance(sp, _GuardedSubprocess):
        module.subprocess = _GuardedSubprocess(sp if sp is not None else subprocess)
    return module


@contextlib.contextmanager
def preparing(title, detail, step=1, steps=1, key=None, gpu_class=None, say=None, module=None):
    """with preparing("Preparing AI models", "realesr-general-x4v3", key=...): vsmlrt.inference(...)

    Any TensorRT build inside shows as this job; a model that's ready loads without a job."""
    install(module)
    prev = getattr(_current, "info", None)
    _current.info = {"title": title, "detail": detail, "step": step, "steps": steps, "key": key,
                     "gpu_class": gpu_class, "say": say}
    try:
        yield
    finally:
        _current.info = prev

