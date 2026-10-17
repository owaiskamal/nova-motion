# NOVA Motion

[![CI](https://github.com/owaiskamal/nova-motion/actions/workflows/ci.yml/badge.svg)](https://github.com/owaiskamal/nova-motion/actions/workflows/ci.yml)

**Real-time AI motion smoothing for video and YouTube on Windows, by Novabit.**

NOVA Motion plays 24/25/30 fps video at your screen's refresh rate by generating the missing frames with AI, live, while you watch. Local files, YouTube and 1000+ other sites, one click from the browser.

> Status: working prototype (v0.1). Plays 1080p 25 → 60 fps in real time with RIFE on TensorRT. The full player is being built in the open; see the [roadmap](#roadmap).

## Benchmarks

1080p, 25 → 60 fps, measured with `benchmark.cmd` on an NVIDIA RTX 4070. Real-time playback needs 60+.

| Engine | Frames per second | Quality | Runs on |
| --- | ---: | --- | --- |
| **RIFE on TensorRT** (fp16) | **81** | AI, best | NVIDIA RTX |
| MVTools | 94 | Motion vectors, good | Any CPU |
| RIFE on Vulkan (ncnn) | 49 | AI, best | Any modern GPU |

TensorRT is what makes AI interpolation real-time: same model, 1.65× the Vulkan speed.

## How it works

```
 NOVA Motion app (WPF, .NET 8) ──writes──►  %APPDATA%\NovaMotion\engine.json
        │  JSON IPC (named pipe)                       ▲ read on every (re)load
        ▼                                              │
   mpv player ── vf=vapoursynth ──►  novamotion.vpy ──┬─► RIFE · TensorRT (vs-mlrt, fp16)   NVIDIA RTX
        │  ytdl_hook                                   ├─► RIFE · ncnn Vulkan               any GPU
        ▼                                              └─► MVTools FlowFPS                  CPU fallback
     yt-dlp (YouTube & co)
```

- **The engine** (`engine/novamotion.vpy`) receives decoded frames from mpv, works out the target rate (your display's refresh rate by default; 23.976 → 60000/1001 on a 59.94 Hz screen), and generates the in-between frames. It never makes more frames than the screen can show, and falls back TensorRT → Vulkan → MVTools so playback always works.
- **The TensorRT path** (`engine/novamotion_trt.py`) schedules frames itself: output frame *n* sits between source frames ⌊n·q/p⌋ and the next, at time point (n·q mod p)/p. Frames that land exactly on a source frame are copied, not inferred.
- **The player script** (`engine/mpv/scripts/novamotion.lua`) applies the engine to every file, handles hotkeys and live settings changes, and writes a performance line every 5 s (output fps, dropped frames, cache) to `mpv.log`.
- **The app** (`app/`) is the control window: profiles, GPU detection, live fps readout, tray icon, and `novamotion://` links for the browser extension.

### Hard problems solved

- **The endless clip.** A live player doesn't know a stream's length, so mpv hands the engine a clip of ~2 billion frames. vs-mlrt's `RIFE()` builds a Python table entry per output frame up front, which never finishes: playback froze silently. NOVA Motion computes each frame's source pair and time point on demand instead; setup takes 0.02 s at any length.
- **Format hand-offs.** mpv's VapourSynth bridge only accepts YUV back, and vs-mlrt returns 32-bit frames from 16-bit input. Each mismatch disabled the engine with no visible error, and the player fell back to 25 fps. Formats are now matched at every hand-off, and the tests check them.
- **Wasted frames.** "3× source" on a 60 Hz TV made 75 fps and threw 15 frames a second away. Output is now capped at the refresh rate.
- **Invisible failures.** Every bug above showed only as "not smooth". Per-5 s stats in `mpv.log`, an `engine-debug.log` that dumps every thread's stack if the engine takes over 15 s to start, and a benchmark found them all.

## Quick start (Windows 10/11)

1. Install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (`winget install Microsoft.DotNet.SDK.8`).
2. Run **`build.cmd`**. The first run downloads the runtime (about 150 MB: mpv, VapourSynth, RIFE, MVTools, yt-dlp), runs the tests and builds `dist\NovaMotion.exe`.
3. **NVIDIA RTX users:** run **`setup-tensorrt.cmd`** once. It downloads NVIDIA TensorRT through [vs-mlrt](https://github.com/AmusementClub/vs-mlrt) (about 3.3 GB) and prepares the 1080p engine (about 90 s).
4. Start `dist\NovaMotion.exe`, paste a link or drop a file, and press **Play**.

Optional: load `extension\` in Chrome or Edge (`chrome://extensions` → Developer mode → Load unpacked). Alt+Shift+F on any video page sends it to NOVA Motion at the same timestamp.

Measure your own machine with **`benchmark.cmd`** (results in `runtime\benchmark.log`).

### Player hotkeys

| Key | Action |
| --- | --- |
| Ctrl+S | Smoothing on/off |
| Ctrl+E | Engine: RIFE TensorRT → RIFE Vulkan → MVTools |
| Ctrl+T | Target: display → ½ display → 60 → 120 → 2× |
| Ctrl+D | Split-screen before/after |
| Ctrl+R | RTX Video Super Resolution |
| Ctrl+I | Current settings |

## Project layout

```
app/                    .NET 8 solution: NovaMotion.Core, NovaMotion.App (WPF), NovaMotion.SmokeTest
engine/                 novamotion.vpy, novamotion_trt.py, mpv config + Lua player script
extension/              Chrome/Edge extension (Manifest V3)
setup/                  Get-Runtime.ps1, Get-TensorRT.ps1, Benchmark.ps1
tests/                  engine tests (VapourSynth, no GPU) and Lua tests (mock mpv)
build.cmd · setup-tensorrt.cmd · benchmark.cmd
```

## Development

```bash
dotnet run --project app/NovaMotion.SmokeTest                       # core library: settings, IPC, links
pip install vapoursynth vapoursynth-mvtools
python tests/engine/test_engine.py engine/novamotion.vpy             # engine, GPU parts stood in
cd tests/lua && TESTCFG=/tmp/cfg.json lua5.4 run.lua ../../engine/mpv/scripts/novamotion.lua
```

CI runs all of these plus a Windows build of the WPF app on every push.

## Roadmap

| Milestone | Scope |
| --- | --- |
| M1 Foundations | Pinned dependency versions, one installer, CI |
| M2 Own player window | mpv drawn inside the app, on-video controls, subtitles, play queue |
| M3 Engine manager | Background TensorRT builds per resolution, scene-cut detection, first-run auto-tuning |
| M4 Online and launch | Quality picker, yt-dlp auto-update, extension in the stores, project site |
| Later | Enhance & Export (offline render), smoothing over any window, faster AMD/Intel paths |

## License

NOVA Motion is free software under the [GNU GPL v3.0](LICENSE) (or later). It builds on mpv, VapourSynth, vs-mlrt, RIFE, MVTools and yt-dlp; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). NVIDIA TensorRT is downloaded by the setup script on your machine and is not part of this repository.

NOVA Motion is an independent project. It is not affiliated with NVIDIA, SVP (SmoothVideo Project) or the projects above, and contains no code from SVP.

© 2026 Novabit
