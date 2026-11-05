# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Novabit
"""Tests for engine/jobs.py: progress files for AI preparation, and learned build times.

    python tests/engine/test_jobs.py
"""
import json
import os
import sys
import tempfile

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "engine"))
import jobs  # noqa: E402

fails = 0


def check(ok, what):
    global fails
    print(("PASS " if ok else "FAIL ") + what)
    if not ok:
        fails += 1


d = tempfile.mkdtemp()
jobs.JOBS_DIR = os.path.join(d, "jobs")
jobs.TIMES = os.path.join(d, "trt-engines", "build-times.json")

check(jobs.expected_seconds("enhance:a.onnx", "entry") == 240 and jobs.expected_seconds("x") == 60,
      "first guess: by graphics class, conservative on GTX 16 cards")
seen = {}
with jobs.Job("Preparing AI models", "realesr-general-x4v3", step=3, steps=9, key="enhance:a.onnx", gpu_class="entry") as j:
    files = os.listdir(jobs.JOBS_DIR)
    seen = json.load(open(os.path.join(jobs.JOBS_DIR, files[0])))
check(seen.get("step") == 3 and seen.get("steps") == 9 and seen.get("expected_s") == 240 and seen.get("pid") == os.getpid()
      and seen.get("detail") == "realesr-general-x4v3", f"a job says what, which step, and how long it should take ({seen})")
check(os.listdir(jobs.JOBS_DIR) == [], "the job file goes when it's done")
check(not os.path.exists(jobs.TIMES), "a quick one (an engine that was already there) isn't recorded as a build time")
jobs.record_seconds("enhance:a.onnx", 150)
jobs.record_seconds("enhance:b.onnx", 210)
check(jobs.expected_seconds("enhance:a.onnx") == 150, "learned: the same model's last build")
check(jobs.expected_seconds("enhance:new.onnx") in (150, 210), "learned: a new model expects the typical build on this PC")
try:
    with jobs.Job("x", "y", key="enhance:c.onnx"):
        raise RuntimeError("build failed")
except RuntimeError:
    pass
check(os.listdir(jobs.JOBS_DIR) == [] and jobs.expected_seconds("enhance:c.onnx") != 0.0, "a failed build removes its job and records nothing")

check(jobs.weight("enhance:up2x-latest-denoise3x.onnx") == 3 and jobs.expected_seconds("enhance:up2x-latest-no-denoise.onnx") > jobs.expected_seconds("enhance:new.onnx"),
      "Real-CUGAN expects a longer build than the compact models")

# --- Builds: progress only for real builds, one at a time, and no second build of the same engine
import threading, time, types  # noqa: E401,E402
jobs.LOCK = os.path.join(d, "trt-engines", "build.lock")
fake = types.ModuleType("fakevsmlrt")
fake.subprocess = __import__("subprocess")
calls = []
engine = os.path.join(d, "m.engine")


def fake_run(args, *a, **k):
    calls.append(os.listdir(jobs.JOBS_DIR))
    with open(engine, "wb") as f:
        f.write(b"x" * 2048)
    return fake.subprocess.CompletedProcess(args, 0)


jobs.install(fake)
fake.subprocess._real = types.SimpleNamespace(run=fake_run, CompletedProcess=__import__("subprocess").CompletedProcess)
check(jobs.install(fake).subprocess._real.run is fake_run, "install is idempotent")
with jobs.preparing("Preparing AI models", "up2x-latest-no-denoise", key="enhance:up2x.onnx", module=fake):
    pass
check(os.listdir(jobs.JOBS_DIR) == [], "a ready model (no build) shows no progress")

seen_wait = []
with jobs._lock_file():                       # another process is building
    def other():
        with jobs.preparing("Preparing AI models", "up2x-latest-no-denoise", key="enhance:up2x.onnx", module=fake):
            fake.subprocess.run(["trtexec", "--onnx=x/up2x.onnx", f"--saveEngine={engine}"])
    t = threading.Thread(target=other)
    t.start()
    time.sleep(1.5)
    files = os.listdir(jobs.JOBS_DIR)
    seen_wait = [json.load(open(os.path.join(jobs.JOBS_DIR, f)))["detail"] for f in files]
    check(calls == [], "a second build waits while another is in progress")
t.join(10)
check(any("waiting for another" in x for x in seen_wait), f"and says it's waiting ({seen_wait})")
check(len(calls) == 1 and len(calls[0]) == 1, "then builds, with progress showing")
with jobs.preparing("Preparing AI models", "up2x-latest-no-denoise", module=fake):
    fake.subprocess.run(["trtexec", "--onnx=x/up2x.onnx", f"--saveEngine={engine}"])
check(len(calls) == 1, "an engine another process made meanwhile isn't built again")

print()
print("ALL PASSED" if not fails else f"{fails} FAILED")
sys.exit(1 if fails else 0)
