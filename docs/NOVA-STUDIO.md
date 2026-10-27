# NOVA Studio plan

NOVA Studio is one Windows app by Novabit for watching, improving and making video on your own graphics card. NOVA Motion, the real-time smoothing player, is its first module. Everything runs locally: no account, no per-video fee, no upload.

## Modules

| Module | What it does | State |
| --- | --- | --- |
| **Motion** | Plays files, YouTube and 1000+ sites at the screen's refresh rate with AI in-between frames (RIFE on TensorRT or Vulkan, MVTools fallback). | v0.2, shipping |
| **Enhance** | Makes better copies of files: smooth export today; heavier offline interpolation and upscaling next. | Export shipping, upscaling planned |
| **Generate** | Makes images and short clips from a prompt or an image with open models, then hands them to Enhance to smooth to 60 fps and upscale. | Planned; hardware check shipping now |

The pitch that ties them together: **generate → smooth → upscale → export, in one app, on your own PC.** Open video models output short, choppy clips (Wan 2.2 makes 16–24 fps); the interpolation NOVA Motion already does is the missing step, and today people do it by hand across several tools.

## Rules that stay fixed

- One app, one installer. Code, settings folder (`%APPDATA%\NovaMotion`), the `novamotion://` link scheme, the installer's AppId and the repo keep their NovaMotion names, so existing installs update in place and nothing breaks.
- Each module works on the weakest PC it can, and says plainly what a PC can't do instead of failing.
- Only models whose licence allows commercial use ship as one-click downloads. Models are downloaded by the user from the publisher's official page, never bundled in the installer.
- Clean room: no SVP code or assets; NVIDIA TensorRT files are never committed.

## Phases

### Phase 1: Studio shell (now)

- Rebrand the window, title bar and installer to NOVA Studio.
- Module tabs in the title bar: Motion, Enhance, Generate. Settings stays shared.
- Generate page with a hardware check: graphics card, its memory and system memory, and which open models this PC can run (well, slowly with quantised weights, or not at all). This tells users what to expect and tells us what hardware our users have.

### Phase 2: Launch Motion (M4)

- Extension in the Chrome and Edge stores, project site, LinkedIn launch.
- Launch angle that leads into Studio: "smooth your AI-generated clips to 60 fps".

### Phase 3: Enhance

- Upscaling for exports (Real-ESRGAN class models on TensorRT/Vulkan, the same runtime approach as RIFE).
- Heavier offline interpolation (e.g. GIMM-VFI) for the best-quality export.
- Batch presets: "AI clip" (16 fps → 60, 2× upscale), "Phone video", "Anime".

### Phase 4: Generate

- Engine: a ComfyUI install that NOVA Studio manages and drives headless over its API (like mpv for Motion), so we reuse the community's model support instead of writing our own inference.
- Model manager: download, verify and delete models from the official repositories, with sizes and VRAM shown up front.
- First models: Flux.1 Schnell and SDXL for images; Wan 2.2 TI2V-5B for video, then the 14B image-to-video model in quantised form for 12 GB cards.
- Simple UI: prompt, optional start image, length, size, seed. Results go to a gallery with "Smooth & upscale" one click away.
- Speed: step-distillation LoRAs (4–8 steps instead of 20–30) where their licences allow.

### Later

- A light timeline: trim, join clips, music and titles. Not an After Effects or DaVinci replacement.
- Premium tier on rented cloud GPUs for people without a strong card; mobile apps.

## Hardware the Generate check uses

NVIDIA cards only for now (the models depend on CUDA). Rough needs, before tuning:

| Model | Licence | Runs well | Runs (quantised, slower) |
| --- | --- | --- | --- |
| SDXL (images) | CreativeML Open RAIL++-M | 8 GB | 6 GB |
| Flux.1 Schnell (images) | Apache 2.0 | 16 GB | 8 GB |
| Wan 2.2 TI2V-5B (video, 720p) | Apache 2.0 | 24 GB | 12 GB |
| Wan 2.2 I2V/T2V-14B (video, 480p) | Apache 2.0 | 32 GB | 12 GB |

32 GB of system memory is recommended for video, because quantised runs move part of the model into it. Not offered as one-click: Flux.1 Dev (non-commercial licence) and newer Wan versions (2.6, 2.7, 3.0), which are API-only with no downloadable weights.
