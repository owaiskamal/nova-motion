# Third-party notices

NOVA Motion's own code is licensed under GPL-3.0-or-later (see [LICENSE](LICENSE)). At setup time it downloads and runs the following components, each under its own licence. Apart from the two fonts and the Anime4K shaders, none of them is stored in this repository.

| Component | Used for | Licence | Source |
| --- | --- | --- | --- |
| mpv | Video player, run as a separate process | GPL-2.0-or-later | https://github.com/mpv-player/mpv (Windows builds: https://github.com/shinchiro/mpv-winbuild-cmake) |
| VapourSynth (portable) | Frame-processing host for the engine | LGPL-2.1 | https://github.com/vapoursynth/vapoursynth |
| Python (embeddable) | Runs VapourSynth scripts | PSF License | https://www.python.org |
| vs-mlrt (vsmlrt.py, vstrt) | RIFE on TensorRT | GPL-3.0 | https://github.com/AmusementClub/vs-mlrt |
| NVIDIA TensorRT and CUDA runtime | GPU inference (optional pack) | NVIDIA SDK licence agreement | https://docs.nvidia.com/deeplearning/tensorrt/latest/reference/sla.html |
| VapourSynth-RIFE-ncnn-Vulkan and RIFE models | RIFE on Vulkan | MIT | https://github.com/styler00dollar/VapourSynth-RIFE-ncnn-Vulkan |
| RIFE (Practical-RIFE) | Interpolation model | MIT | https://github.com/hzwer/Practical-RIFE |
| MVTools for VapourSynth | Motion-vector interpolation | GPL-2.0 | https://github.com/dubhater/vapoursynth-mvtools |
| akarin plugin | Fractional frame-rate steps for vs-mlrt | See project | https://github.com/AkarinVS/vapoursynth-plugin |
| Anime4K shaders (bundled in `engine/mpv/shaders`) | Live enhance: Anime | MIT | https://github.com/bloc97/Anime4K |
| Real-ESRGAN general, AnimeVideo v3, x4plus and x4plus anime 6B models | Enhance: upscaling; x4plus: Max detail for photos (converted to ONNX by `tools/realesrgan_to_onnx.py`) | BSD-3-Clause | https://github.com/xinntao/Real-ESRGAN |
| Real-CUGAN models | Enhance: anime and art upscaling | MIT | https://github.com/bilibili/ailab |
| DPIR deblocking model | Enhance: fixing compression | MIT | https://github.com/cszn/DPIR |
| SeedVR2 3B model (fp8) and VAE | Enhance a photo: Generative (downloaded by `setup-generate.cmd`) | Apache-2.0 | https://github.com/ByteDance-Seed/SeedVR · https://huggingface.co/numz/SeedVR2_comfyUI |
| ComfyUI-SeedVR2_VideoUpscaler (its standalone runner) | Runs SeedVR2 for Generative (downloaded at a pinned commit by `setup-generate.cmd`) | Apache-2.0 | https://github.com/numz/ComfyUI-SeedVR2_VideoUpscaler |
| PyTorch (CUDA 12.8) and uv | Generative's own Python environment (downloaded by `setup-generate.cmd`) | BSD-3-Clause; MIT / Apache-2.0 | https://pytorch.org · https://github.com/astral-sh/uv |
| NAFNet (REDS, width 64) model | Enhance a photo: Remove noise and blur (converted to ONNX by `tools/restore_to_onnx.py` with NAFNet's own network code) | MIT | https://github.com/megvii-research/NAFNet |
| Depth Anything V2 Small (ONNX, onnx-community) | Depth for Animate a picture (optional download) | Apache-2.0 | https://huggingface.co/depth-anything/Depth-Anything-V2-Small · https://huggingface.co/onnx-community/depth-anything-v2-small |
| ONNX Runtime with DirectML | Runs the depth model on the graphics card | MIT | https://github.com/microsoft/onnxruntime |
| NumPy | Draws the camera move | BSD-3-Clause | https://numpy.org |
| Pillow | Reads pictures | MIT-CMU | https://python-pillow.org |
| yt-dlp | Online video | Unlicense | https://github.com/yt-dlp/yt-dlp |
| 7-Zip (7zr.exe) | Unpacking downloads during setup | LGPL-2.1 | https://www.7-zip.org |
| Onest and Unbounded fonts (bundled in the app, `app/NovaMotion.App/Fonts`) | Interface type | SIL Open Font License 1.1 | https://github.com/google/fonts |

Online playback through yt-dlp is subject to each website's own terms of service.

## Images on the website and in the README

The before/after comparison photos (`site/img/compare/`, `docs/img/compare-*.jpg`) are made from the Kodak Lossless True Color Image Suite, released by Eastman Kodak for unrestricted use. The "before" pictures were reduced, or blurred and compressed, for the tests; the "after" pictures are the output of the models named with them.
