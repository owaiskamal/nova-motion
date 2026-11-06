# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Novabit
"""A stand-in for SeedVR2's inference_cli.py in tests: the same arguments that matter, its phase lines,
and a plain enlargement instead of the model. FAKE_SEEDVR2_OOM=1: runs out of memory unless the model is
swapped to the PC's memory."""
import argparse
import os
import sys

from PIL import Image

p = argparse.ArgumentParser()
p.add_argument("input")
p.add_argument("--output")
p.add_argument("--output_format")
p.add_argument("--model_dir")
p.add_argument("--dit_model")
p.add_argument("--resolution", type=int)
p.add_argument("--max_resolution", type=int)
p.add_argument("--batch_size", type=int)
p.add_argument("--seed", type=int)
p.add_argument("--color_correction")
p.add_argument("--cuda_device")
p.add_argument("--vae_encode_tiled", action="store_true")
p.add_argument("--vae_decode_tiled", action="store_true")
p.add_argument("--dit_offload_device")
p.add_argument("--vae_offload_device")
p.add_argument("--blocks_to_swap", type=int, default=0)
p.add_argument("--swap_io_components", action="store_true")
a = p.parse_args()
with open(os.environ.get("FAKE_SEEDVR2_ARGS", os.devnull), "a") as f:
    f.write(" ".join(sys.argv[1:]) + "\n")
print("Phase 1: Encoding", flush=True)
if os.environ.get("FAKE_SEEDVR2_OOM") and a.blocks_to_swap < 32:
    print("torch.OutOfMemoryError: CUDA out of memory. Tried to allocate 2.00 GiB", flush=True)
    sys.exit(1)
print("Phase 2: Upscaling", flush=True)
img = Image.open(a.input)
k = a.resolution / min(img.size)
# Like the runner: multiples of 16
size = (int(img.width * k) // 16 * 16, int(img.height * k) // 16 * 16)
print("Phase 3: Decoding", flush=True)
img.resize(size, Image.BICUBIC).save(a.output)
print("Phase 4: Post-processing", flush=True)
