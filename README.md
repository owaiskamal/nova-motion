# NOVA Motion

[![CI](https://github.com/owaiskamal/nova-motion/actions/workflows/ci.yml/badge.svg)](https://github.com/owaiskamal/nova-motion/actions/workflows/ci.yml)

**Real-time AI motion smoothing for video and YouTube on Windows, by Novabit.**

NOVA Motion plays 24/25/30 fps video at your screen's refresh rate by generating the missing frames with AI, live, while you watch. Local files, YouTube and 1000+ other sites, one click from the browser.

> Status: v0.2, the full player. The video plays inside NOVA Motion with its own controls, resume, queue, subtitles and a live Motion panel. 1080p 25 → 60 fps in real time with RIFE on TensorRT. See the [roadmap](#roadmap).

## Benchmarks

1080p, 25 → 60 fps, measured with `benchmark.cmd` on an NVIDIA RTX 4070. Real-time playback needs 60+.

| Engine | Frames per second | Quality | Runs on |
| --- | ---: | --- | --- |
| **RIFE on TensorRT** (fp16) | **81** | AI, best | NVIDIA RTX, GTX 16 |
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
- **The app** (`app/`) is the player. mpv draws the video into a window inside the app; the controls live in a transparent window kept exactly over it, because Windows always paints a native child window above WPF content. All behaviour sits in a view model (`NovaMotion.Core/ViewModels`) with no WPF in it, so it is tested on Linux against a fake mpv.
- **Scene cuts:** on the TensorRT path a cheap frame-difference check finds cuts, and frames that would blend two shots show the nearer real frame instead.

### Hard problems solved

- **The endless clip.** A live player doesn't know a stream's length, so mpv hands the engine a clip of ~2 billion frames. vs-mlrt's `RIFE()` builds a Python table entry per output frame up front, which never finishes: playback froze silently. NOVA Motion computes each frame's source pair and time point on demand instead; setup takes 0.02 s at any length.
- **Format hand-offs.** mpv's VapourSynth bridge only accepts YUV back, and vs-mlrt returns 32-bit frames from 16-bit input. Each mismatch disabled the engine with no visible error, and the player fell back to 25 fps. Formats are now matched at every hand-off, and the tests check them.
- **Wasted frames.** "3× source" on a 60 Hz TV made 75 fps and threw 15 frames a second away. Output is now capped at the refresh rate.
- **Invisible failures.** Every bug above showed only as "not smooth". Per-5 s stats in `mpv.log`, an `engine-debug.log` that dumps every thread's stack if the engine takes over 15 s to start, and a benchmark found them all.

## Quick start (Windows 10/11)

**Installer:** download `NovaMotion-Setup` from the latest [release](https://github.com/owaiskamal/nova-motion/releases) (or from the newest CI run's artifacts), run it, and start NOVA Motion. No admin rights needed. NVIDIA RTX and GTX 16-series users then run *Add NVIDIA TensorRT engine* from the Start menu once.

**From source:**

1. Install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (`winget install Microsoft.DotNet.SDK.8`).
2. Run **`build.cmd`**. The first run downloads the runtime (about 150 MB: mpv, VapourSynth, RIFE, MVTools, yt-dlp), runs the tests and builds `dist\NovaMotion.exe`.
3. **NVIDIA RTX and GTX 16-series users:** run **`setup-tensorrt.cmd`** once. It downloads NVIDIA TensorRT through [vs-mlrt](https://github.com/AmusementClub/vs-mlrt) (about 3.3 GB) and prepares the 1080p engine (about 90 s). Then NOVA Motion offers to tune itself for your PC (about 10 minutes), measuring each AI model so playback uses the best one your card keeps up with. Entry cards such as a GTX 1650 start with the light model at 60 fps until then.
4. Start `dist\NovaMotion.exe`, paste a link or drop a file, and press **Play**.

Optional: load `extension\` in Chrome or Edge (`chrome://extensions` → Developer mode → Load unpacked). Alt+Shift+F on any video page sends it to NOVA Motion at the same timestamp.

Measure your own machine with **`benchmark.cmd`** (results in `runtime\benchmark.log`).

### The player

- **Home:** paste a link or drop files; a link you've copied is offered with one click. Recently played videos resume a few seconds before where you stopped, and opening an episode queues the rest of its folder.
- **On the video:** controls that hide while you watch, a seek bar with buffered range, chapters and hover time, double-click for full screen, scroll for volume.
- **Motion panel** (Tab, or the badge next to the controls): the frame rate on screen, the frame-cadence strip (white bars are frames from the video, gold ones are generated), the engine's status in plain words, and every smoothing choice.
- **Subtitles, audio, speed and online quality** in one panel; drop a subtitle file on the video to load it.

### Keyboard

| Key | Action |
| --- | --- |
| Space / K | Play or pause |
| ← / → · Shift for 30 s | Back or forward 5 s |
| ↑ / ↓ · M | Volume · mute |
| F · Esc | Full screen · leave it |
| 0 – 9 | Jump to 0 % – 90 % |
| N / P | Next or previous in the queue |
| Tab | Motion panel |
| Ctrl+S | Smoothing on/off |
| Ctrl+E / Ctrl+T | Next engine / next frame-rate target |
| Ctrl+D | Before/after split screen |
| Ctrl+R | RTX Video Super Resolution |

The full list is in Settings → Keyboard shortcuts.

## Project layout

```
app/                    .NET 8 solution: NovaMotion.Core (logic + view models), NovaMotion.App (WPF), NovaMotion.SmokeTest
engine/                 novamotion.vpy, novamotion_trt.py, mpv config + Lua player script
extension/              Chrome/Edge extension (Manifest V3)
setup/                  Get-Runtime.ps1, Get-TensorRT.ps1, Benchmark.ps1
installer/              Inno Setup script (per-user install)
tests/                  engine tests (VapourSynth, no GPU), Lua tests (mock mpv), XAML checks
build.cmd · setup-tensorrt.cmd · prepare-engines.cmd · benchmark.cmd
```

## Development

```bash
dotnet run --project app/NovaMotion.SmokeTest                       # core library: settings, IPC, links
pip install vapoursynth vapoursynth-mvtools
python tests/engine/test_engine.py engine/novamotion.vpy             # engine, GPU parts stood in
cd tests/lua && TESTCFG=/tmp/cfg.json lua5.4 run.lua ../../engine/mpv/scripts/novamotion.lua
python tests/ui/lint_xaml.py app/NovaMotion.App app/NovaMotion.Core  # resource keys, style targets, bindings
```

CI runs all of these, then builds the app on Windows, plays a test video through the real player, screenshots every screen (published to the `ci-screenshots` branch) and builds the installer.

## Roadmap

| Milestone | Scope |
| --- | --- |
| M1 Foundations | ✅ CI with Windows UI screenshots · ✅ installer · ✅ pinned dependency versions |
| M2 Own player window | ✅ mpv inside the app, on-video controls, subtitles, queue, resume |
| M3 Engine manager | ✅ scene-cut detection on TensorRT · ✅ per-size engine status and preparation · first-run auto-tuning |
| M4 Online and launch | ✅ quality picker · ✅ daily yt-dlp updates · extension in the stores, project site |
| Later | Enhance & Export (offline render), smoothing over any window, faster AMD/Intel paths |

## License

NOVA Motion is free software under the [GNU GPL v3.0](LICENSE) (or later). It builds on mpv, VapourSynth, vs-mlrt, RIFE, MVTools and yt-dlp; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). NVIDIA TensorRT is downloaded by the setup script on your machine and is not part of this repository.

NOVA Motion is an independent project. It is not affiliated with NVIDIA, SVP (SmoothVideo Project) or the projects above, and contains no code from SVP.

© 2026 Novabit
