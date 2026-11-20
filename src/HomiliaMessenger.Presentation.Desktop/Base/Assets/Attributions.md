# Attributions

## Fonts

- **Akashi** - Ten by Twenty @ [FontSpace](https://www.fontspace.com/akashi-font-f14894)

  Used for page headings.

## Icons

- **Speech Bubble** - Magnific @ [Flaticon](https://www.flaticon.com/free-icon/speech-bubble_3845696)

  Used repo logo and application icon.

- **User** - Becris @ [Magnific](https://www.magnific.com/icon/user_848006)

  Used as the placeholder contact image.

## Image generation

- **DreamShaper 8 LCM** — by Lykon ([Lykon/dreamshaper-8-lcm](https://huggingface.co/Lykon/dreamshaper-8-lcm)), a fine-tune of Stable Diffusion 1.5, converted to GGUF by [NishQ1/vora-create-sd15](https://huggingface.co/NishQ1/vora-create-sd15). CreativeML Open RAIL-M, with use-based restrictions (Attachment A) that apply to everyone who uses it; downloaded only when requested, with its license.
- **FLUX.2 [klein] 4B** — by Black Forest Labs ([black-forest-labs/FLUX.2-klein-4B](https://huggingface.co/black-forest-labs/FLUX.2-klein-4B)), Apache 2.0, converted to GGUF by [leejet/FLUX.2-klein-4B-GGUF](https://huggingface.co/leejet/FLUX.2-klein-4B-GGUF); with its FLUX.2 VAE (Apache 2.0, from [Comfy-Org/Flux-2-Klein-4B](https://huggingface.co/Comfy-Org/Flux-2-Klein-4B)) and the **Qwen3-4B** text encoder (Qwen, Apache 2.0, converted by [unsloth/Qwen3-4B-GGUF](https://huggingface.co/unsloth/Qwen3-4B-GGUF)). Downloaded only when requested.
- **DreamShaper XL Lightning** — by Lykon ([Lykon/dreamshaper-xl-lightning](https://huggingface.co/Lykon/dreamshaper-xl-lightning)), a fine-tune of SDXL 1.0. CreativeML Open RAIL++-M, with use-based restrictions; downloaded only when requested, with IP-Adapter Plus-Face for SDXL (Apache 2.0) for its selfies.
- **stable-diffusion.cpp** — [leejet/stable-diffusion.cpp](https://github.com/leejet/stable-diffusion.cpp) at commit `5dcfa79`, MIT. Local image generation. Its license notice, and ggml's, are included under `StableDiffusion/<platform>/` in build and publish output. Its CUDA build, downloaded only when requested for NVIDIA graphics cards, includes NVIDIA's CUDA runtime and cuBLAS libraries under NVIDIA's [CUDA license](https://docs.nvidia.com/cuda/eula/index.html).
- **ggml** — [ggml-org/ggml](https://github.com/ggml-org/ggml), MIT, as built into stable-diffusion.cpp's release (its private copy is renamed `sdml` so it loads beside LLamaSharp's).
- **IP-Adapter Plus-Face (SD 1.5) and its ViT-H/14 image encoder** — Tencent AI Lab's [IP-Adapter](https://github.com/tencent-ailab/IP-Adapter), weights from [h94/IP-Adapter](https://huggingface.co/h94/IP-Adapter), Apache 2.0. Face matching for selfies; downloaded only when requested, and the encoder converted to Q8_0 on this computer.
- **SkiaSharp** — [mono/SkiaSharp](https://github.com/mono/SkiaSharp), MIT, the copy Avalonia ships. Reads contact photos for face matching.

## Speech

- **Kokoro v1.0 and English voice embeddings** — [hexgrad/Kokoro-82M](https://huggingface.co/hexgrad/Kokoro-82M), Apache 2.0. ONNX model distributed by [KokoroSharpBinaries](https://github.com/Lyrcaxis/KokoroSharpBinaries/releases/tag/v2.0.0); downloaded only when requested. The bundled voice license is included under `SpeechVoices/LICENSE` in build and publish output.
- **KokoroSharp** — [Lyrcaxis/KokoroSharp](https://github.com/Lyrcaxis/KokoroSharp), MIT. Local inference, voice loading, and playback adapters.
- **MisakiSharp** — [Lyrcaxis/MisakiSharp](https://github.com/Lyrcaxis/MisakiSharp), Apache 2.0. Local text phonemization.
