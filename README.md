# NOVA Motion

[![CI](https://github.com/owaiskamal/nova-motion/actions/workflows/ci.yml/badge.svg)](https://github.com/owaiskamal/nova-motion/actions/workflows/ci.yml)

**Real-time AI motion smoothing for video and YouTube on Windows, by Novabit.**

NOVA Motion plays 24/25/30 fps video at your screen's refresh rate by generating the missing frames with AI, live, while you watch. Local files, YouTube and 1000+ other sites, one click from the browser.

> Status: v0.2, the full player. The video plays inside NOVA Motion with its own controls, resume, queue, subtitles and a live Motion panel. 1080p 25 → 60 fps in real time with RIFE on TensorRT, Enhance & Export for smooth copies of files, and tuning that fits the engine to your graphics card, from RTX desktops down to a GTX 1650 laptop. See the [roadmap](#roadmap).

## Benchmarks

Measured with `benchmark.cmd` and `tune.cmd` (the whole engine, not just the model). Real-time playback of 24 fps film to 60 needs 48 AI frames a second; 25 → 60 needs 55.

**NVIDIA RTX 4070 desktop**, 1080p, 25 → 60 fps:

| Engine | Frames per second | Quality | Runs on |
| --- | ---: | --- | --- |
| **RIFE on TensorRT** (fp16) | **81** | AI, best | NVIDIA RTX, GTX 16 |
| MVTools | 94 | Motion vectors, good | Any CPU |
| RIFE on Vulkan (ncnn) | 49 | AI, best | Any modern GPU |

TensorRT is what makes AI interpolation real-time: same model, 1.65× the Vulkan speed.

**NVIDIA GTX 1650 laptop** (4 GB, no tensor cores, Intel i5-12450H, 144 Hz screen), 25 → 60 fps:

| RIFE model on TensorRT | Full size (1080p) | AI at half size (960×540) |
| --- | ---: | ---: |
| **4.6** | 19.7 | **73.2** |
| 4.22 Lite | 13.5 | 50.1 |
| 4.26 | 11.9 | 47.7 |
| 4.25 Lite | 7.1 | 35.3 |
| MVTools (CPU) | 77.4 | |

- At full size no model keeps up on this card. With the AI on a half-size copy (only the in-between frames come from it; real frames stay full size), 4.6 has room to spare for 24 and 25 fps video.
- "Lite" models are *slower* than 4.6 here. They have fewer weights, not less work, and without tensor cores that doesn't pay off. Tuning measures instead of assuming, so the engine picks 4.6.
- One TensorRT stream gave 8 fps, three gave 20: the card waits more than it works at 1080p, so tuning also picks the stream count.

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
- **Repeated frames:** sports and phone video often repeat a frame. Blending across a repeat makes motion pause; the engine spots repeats block by block (so a small moving ball isn't mistaken for a still frame) and blends across the pair around them instead.
- **Tuning** (`tune.cmd`, offered on Home after TensorRT setup) builds and times every installed model on your PC. The engine then picks, per video, the best model and AI size that keep up: full size before a better model at a smaller size. If frames still drop, the player steps to a lighter option on its own and says so.
- **AI size, like DLSS rendering low and upscaling:** on cards that can't keep up at full size, RIFE works on a 75 % or half-size copy and only the in-between frames are scaled back up. Real frames always stay full size. GTX-class cards start at half size until tuned.
- **Even frame pacing:** a 60 fps target on a 144 Hz screen holds frames for 2, then 3 refreshes, which looks like judder. When a whole multiple of the source divides the refresh rate evenly, the engine uses it: 24 fps film on 144 Hz plays at 72 (2 refreshes each), for the same AI work as 60.

### Hard problems solved

- **The endless clip.** A live player doesn't know a stream's length, so mpv hands the engine a clip of ~2 billion frames. vs-mlrt's `RIFE()` builds a Python table entry per output frame up front, which never finishes: playback froze silently. NOVA Motion computes each frame's source pair and time point on demand instead; setup takes 0.02 s at any length.
- **Format hand-offs.** mpv's VapourSynth bridge only accepts YUV back, and vs-mlrt returns 32-bit frames from 16-bit input. Each mismatch disabled the engine with no visible error, and the player fell back to 25 fps. Formats are now matched at every hand-off, and the tests check them.
- **Wasted frames.** "3× source" on a 60 Hz TV made 75 fps and threw 15 frames a second away. Output is now capped at the refresh rate.
- **Laptops dropping frames that were on time.** On a GTX 1650 laptop the engine kept up (0 late frames), yet mpv dropped ~35 frames a second as "mistimed". Locking frames to the refresh rate fails when the screen hangs off the Intel graphics and video is drawn on the NVIDIA card. Laptops with two graphics chips now time frames by the video's own timestamps, the player switches by itself when most drops are mistimed, and those drops no longer push the model down.
- **Tuning that was never read.** Windows PowerShell wrote `tuning.json` with a byte-order mark that the engine's JSON reader rejected, so every PC ran the untuned default. Files are now read with or without it and written without.
- **Models that were never installed.** vs-mlrt's main release only carries RIFE 4.6; newer and lite models are published one archive each in its external-models release, which setup now uses.
- **Exports cut short or starting black.** mpv's VapourSynth bridge stops at the first request past the end of an open-ended clip and drops the frames in flight, and caches a black placeholder for frames asked for during setup. Exports now count the source frames, pad the engine's end, stop the encoder at the exact count, stamp even timestamps, and turn caching off while MVTools is created: a 2 s clip at 25 fps exports as exactly 120 frames at 60 fps, checked in CI.
- **Invisible failures.** Every bug above showed only as "not smooth". Per-5 s stats in `mpv.log`, an `engine-debug.log` that dumps every thread's stack if the engine takes over 15 s to start, and a benchmark found them all.

## Quick start (Windows 10/11)

**Installer:** download `NovaMotion-Setup` from the latest [release](https://github.com/owaiskamal/nova-motion/releases) (or from the newest CI run's artifacts), run it, and start NOVA Motion. No admin rights needed. NVIDIA RTX and GTX 16-series users then run *Add NVIDIA TensorRT engine* from the Start menu once.

**From source:**

1. Install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (`winget install Microsoft.DotNet.SDK.8`).
2. Run **`build.cmd`**. The first run downloads the runtime (about 150 MB: mpv, VapourSynth, RIFE, MVTools, yt-dlp), runs the tests and builds `dist\NovaMotion.exe`.
3. **NVIDIA RTX and GTX 16-series users:** run **`setup-tensorrt.cmd`** once. It downloads NVIDIA TensorRT through [vs-mlrt](https://github.com/AmusementClub/vs-mlrt) (about 3.3 GB) with RIFE 4.6, 4.22 Lite, 4.25 Lite and 4.26, then tunes for your PC (about 10 minutes on an RTX card, longer on a GTX), measuring each model so playback uses the best one your card keeps up with. Set up TensorRT before October 2026? Run **`get-models.cmd`** once to add the newer models (about 60 MB, no TensorRT download). Run **`tune.cmd`** again any time, e.g. after a driver update. GTX-class cards are tuned at the half size they play at.
4. Start `dist\NovaMotion.exe`, paste a link or drop a file, and press **Play**.

Optional: load `extension\` in Chrome or Edge (`chrome://extensions` → Developer mode → Load unpacked). Alt+Shift+F on any video page sends it to NOVA Motion at the same timestamp.

Measure your own machine with **`benchmark.cmd`** (results in `runtime\benchmark.log`): every engine, TensorRT at full and half size, and Vulkan RIFE on each graphics chip (laptops have two; the app then uses the faster one).

Something not smooth? Run **`collect-logs.cmd`**. It puts the player and engine logs, settings, tuning results and graphics info into `nova-motion-logs.zip` on your Desktop, to attach to an issue.

### The player

- **Home:** paste a link or drop files; a link you've copied is offered with one click. Recently played videos resume a few seconds before where you stopped, and opening an episode queues the rest of its folder.
- **On the video:** controls that hide while you watch, a seek bar with buffered range, chapters and hover time, double-click for full screen, scroll for volume.
- **Motion panel** (Tab, or the badge next to the controls): the frame rate on screen, dropped frames, the model in use and the size the AI runs at, the frame-cadence strip (white bars are frames from the video, gold ones are generated), the engine's status in plain words, and every smoothing choice: engine, frame rate, AI model (Auto or a fixed one) and AI size (Auto, Full, 75 %, Half).
- **Before/after:** side by side or a split screen with a divider, labelled, to see what the engine adds.
- **Enhance & Export:** make a smooth copy of any file at 60 fps, 120 fps or 2× with the best model installed (or the faster live one). Files queue up, show time left, and are saved next to the original with NOVA Motion in the name. NVIDIA cards encode on the GPU (HEVC); otherwise x264 on the CPU.
- **Fits the PC it runs on:** RTX cards get TensorRT at the display's refresh rate; GTX and other entry cards get 60 fps (or an even rate such as 72 on 144 Hz screens) with the AI at half size; PCs without a graphics card get MVTools. Home offers tuning once TensorRT is installed and explains what the card can do.
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
setup/                  Get-Runtime.ps1, Get-TensorRT.ps1, Tune.ps1, Benchmark.ps1, Build-Engines.ps1
installer/              Inno Setup script (per-user install)
tests/                  engine tests (VapourSynth, no GPU), Lua tests (mock mpv), XAML checks
build.cmd · setup-tensorrt.cmd · get-models.cmd · tune.cmd · prepare-engines.cmd · benchmark.cmd · collect-logs.cmd
```

## Development

```bash
dotnet run --project app/NovaMotion.SmokeTest                       # core library: settings, IPC, links
pip install vapoursynth vapoursynth-mvtools
python tests/engine/test_engine.py engine/novamotion.vpy             # engine, GPU parts stood in
cd tests/lua && TESTCFG=/tmp/cfg.json lua5.4 run.lua ../../engine/mpv/scripts/novamotion.lua
python tests/ui/lint_xaml.py app/NovaMotion.App app/NovaMotion.Core  # resource keys, style targets, bindings
```

CI runs all of these, then builds the app on Windows, plays a test video through the real player, exports a test clip and checks it frame by frame (count, timestamps, no black start), screenshots every screen (published to the `ci-screenshots` branch) and builds the installer.

## Roadmap

| Milestone | Scope |
| --- | --- |
| M1 Foundations | ✅ CI with Windows UI screenshots · ✅ installer · ✅ pinned dependency versions |
| M2 Own player window | ✅ mpv inside the app, on-video controls, subtitles, queue, resume |
| M3 Engine manager | ✅ scene-cut and repeated-frame handling · ✅ per-size engine status and preparation · ✅ tuning, offered on first run · ✅ automatic model and AI-size choice with step-down · ✅ defaults for GTX and laptop graphics |
| M4 Online and launch | ✅ quality picker · ✅ daily yt-dlp updates · extension in the stores, project site |
| M5 Enhance & Export | ✅ offline smooth copies with the best model, queue, GPU encoding · heavier offline models (e.g. GIMM-VFI), upscaling |
| Later | NOVA Studio (enhancement suite), smoothing over any window, faster AMD/Intel paths, mobile |

## License

NOVA Motion is free software under the [GNU GPL v3.0](LICENSE) (or later). It builds on mpv, VapourSynth, vs-mlrt, RIFE, MVTools and yt-dlp; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). NVIDIA TensorRT is downloaded by the setup script on your machine and is not part of this repository.

NOVA Motion is an independent project. It is not affiliated with NVIDIA, SVP (SmoothVideo Project) or the projects above, and contains no code from SVP.

© 2026 Novabit
