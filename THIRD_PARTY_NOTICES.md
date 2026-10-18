# Third-party notices

NOVA Motion's own code is licensed under GPL-3.0-or-later (see [LICENSE](LICENSE)). At setup time it downloads and runs the following components, each under its own licence. Apart from the two fonts, none of them is stored in this repository.

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
| yt-dlp | Online video | Unlicense | https://github.com/yt-dlp/yt-dlp |
| 7-Zip (7zr.exe) | Unpacking downloads during setup | LGPL-2.1 | https://www.7-zip.org |
| Onest and Unbounded fonts (bundled in the app, `app/NovaMotion.App/Fonts`) | Interface type | SIL Open Font License 1.1 | https://github.com/google/fonts |

Online playback through yt-dlp is subject to each website's own terms of service.
