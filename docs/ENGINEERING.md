# How NOVA Motion works

The engineering behind NOVA Motion: the pipeline, the decisions, and the problems it took to make AI frame interpolation real-time on ordinary PCs. For what the app does and how to install it, see the [README](../README.md).

## The pipeline

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
- **AI size, like DLSS rendering low and upscaling:** on cards that can't keep up at full size, RIFE works on a 75 % or half-size copy and only the in-between frames are scaled back up. Real frames always stay full size. GTX-class cards start at half size until tuned. Vulkan RIFE (Intel, AMD and NVIDIA) does the same: built-in graphics such as Intel Iris Xe and GTX-class cards run it at half size.
- **Even frame pacing:** a 60 fps target on a 144 Hz screen holds frames for 2, then 3 refreshes, which looks like judder. When a whole multiple of the source divides the refresh rate evenly, the engine uses it: 24 fps film on 144 Hz plays at 72 (2 refreshes each), for the same AI work as 60.
- **Rates the AI can afford:** a rate that isn't a whole multiple of the source makes the AI work on almost every frame (25 → 72 needs 71 new frames a second). During playback the engine picks a whole multiple near the target when there is one (25 fps on 144 Hz → 75, which needs 50), and when tuning says even the fastest option can't keep up, it plays a lower multiple (25 → 50) instead of dropping frames.
- **Steady under load, on any screen:** if frames keep dropping during playback, the player first steps to a lighter model or AI size while one exists, then lowers the frame rate one whole multiple of the video at a time (75 → 50 for 25 fps), down to twice the video's rate. Each new video starts at the best setting again. Tuning and the benchmark measure on moving test frames, as real video is: a still test clip is all repeats, which the engine skips, and read several times faster than real playback.
- **Never stuck:** a TensorRT engine build freezes the player for minutes, and steps down used to start one mid-video (a new AI size needs its own engine), then count the frozen time as dropped frames and step again. Step-downs now only go to engines already built, drops in the first seconds after the engine starts don't count, and when the player stops answering a **Play without smoothing** card offers the way out (Ctrl+S too): it restarts at the same moment without the engine. With no build under way it does that by itself after 25 seconds.

## Hard problems solved

- **The endless clip.** A live player doesn't know a stream's length, so mpv hands the engine a clip of ~2 billion frames. vs-mlrt's `RIFE()` builds a Python table entry per output frame up front, which never finishes: playback froze silently. NOVA Motion computes each frame's source pair and time point on demand instead; setup takes 0.02 s at any length.
- **Format hand-offs.** mpv's VapourSynth bridge only accepts YUV back, and vs-mlrt returns 32-bit frames from 16-bit input. Each mismatch disabled the engine with no visible error, and the player fell back to 25 fps. Formats are now matched at every hand-off, and the tests check them.
- **Wasted frames.** "3× source" on a 60 Hz TV made 75 fps and threw 15 frames a second away. Output is now capped at the refresh rate.
- **Laptops dropping frames that were on time.** On a GTX 1650 laptop the engine kept up (0 late frames), yet mpv dropped ~35 frames a second as "mistimed". Locking frames to the refresh rate fails when the screen hangs off the Intel graphics and video is drawn on the NVIDIA card. Laptops with two graphics chips now time frames by the video's own timestamps, the player switches by itself when most drops are mistimed, and those drops no longer push the model down.
- **Tuning that was never read.** Windows PowerShell wrote `tuning.json` with a byte-order mark that the engine's JSON reader rejected, so every PC ran the untuned default. Files are now read with or without it and written without.
- **Models that were never installed.** vs-mlrt's main release only carries RIFE 4.6; newer and lite models are published one archive each in its external-models release, which setup now uses.
- **Exports cut short or starting black.** mpv's VapourSynth bridge stops at the first request past the end of an open-ended clip and drops the frames in flight, and caches a black placeholder for frames asked for during setup. Exports now count the source frames, pad the engine's end, stop the encoder at the exact count, stamp even timestamps, and turn caching off while MVTools is created: a 2 s clip at 25 fps exports as exactly 120 frames at 60 fps, checked in CI.
- **Invisible failures.** Every bug above showed only as "not smooth". Per-5 s stats in `mpv.log`, an `engine-debug.log` that dumps every thread's stack if the engine takes over 15 s to start, and a benchmark found them all.

## Project layout

```
app/                    .NET 8 solution: NovaMotion.Core (logic + view models), NovaMotion.App (WPF), NovaMotion.SmokeTest
docs/                   NOVA-STUDIO.md: the plan for Motion, Enhance and Generate
engine/                 novamotion.vpy, novamotion_trt.py, mpv config + Lua player script
extension/              Chrome/Edge extension (Manifest V3)
setup/                  Get-Runtime.ps1, Get-TensorRT.ps1, Tune.ps1, TuneVulkan.ps1, Benchmark.ps1, Build-Engines.ps1
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
