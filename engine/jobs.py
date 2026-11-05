# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Novabit
"""Progress of AI preparation the app can show: TensorRT building an engine takes minutes on GTX 16
cards and reports no progress of its own, so each build says what it is, which step of how many,
when it started and how long it should take, in runtime/jobs/<id>.json. The app reads the folder
and shows a progress bar on every page.

How long a build takes is learned on this PC (trt-engines/build-times.json): the same model's last
build, else the typical build here, else a conservative first guess by graphics class.
"""
import json
import os
import threading
import time
import uuid

HERE = os.path.dirname(os.path.abspath(__file__))
JOBS_DIR = os.path.join(HERE, "jobs")
TIMES = os.path.join(HERE, "trt-engines", "build-times.json")

FIRST_GUESS = {"entry": 240.0, "integrated": 300.0}     # seconds a build; others 60


def _load_times():
    try:
        with open(TIMES, encoding="utf-8-sig") as f:
            data = json.load(f)
        return data if isinstance(data, dict) else {}
    except (OSError, ValueError):
        return {}


def expected_seconds(key, gpu_class=None):
    times = _load_times()
    if isinstance(times.get(key), (int, float)):
        return float(times[key])
    known = sorted(v for v in times.values() if isinstance(v, (int, float)))
    if known:
        return known[len(known) // 2]
    return FIRST_GUESS.get(gpu_class or "", 60.0)


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

    def _heartbeat(self):
        while not self._stop.wait(30):
            spent = time.time() - self.started
            left = max(0, self.expected - spent)
            self.say(f"      still working: {spent / 60:.0f} min so far"
                     + (f", about {left / 60:.0f} min left" if left > 30 else ", nearly done") + " ...")

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
