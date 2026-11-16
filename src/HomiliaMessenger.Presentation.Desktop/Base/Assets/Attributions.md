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
- **stable-diffusion.cpp** — [leejet/stable-diffusion.cpp](https://github.com/leejet/stable-diffusion.cpp) at commit `5dcfa79`, MIT. Local image generation. Its license notice, and ggml's, are included under `StableDiffusion/<platform>/` in build and publish output.
- **ggml** — [ggml-org/ggml](https://github.com/ggml-org/ggml), MIT, as built into stable-diffusion.cpp's release (its private copy is renamed `sdml` so it loads beside LLamaSharp's).

## Speech

- **Kokoro v1.0 and English voice embeddings** — [hexgrad/Kokoro-82M](https://huggingface.co/hexgrad/Kokoro-82M), Apache 2.0. ONNX model distributed by [KokoroSharpBinaries](https://github.com/Lyrcaxis/KokoroSharpBinaries/releases/tag/v2.0.0); downloaded only when requested. The bundled voice license is included under `SpeechVoices/LICENSE` in build and publish output.
- **KokoroSharp** — [Lyrcaxis/KokoroSharp](https://github.com/Lyrcaxis/KokoroSharp), MIT. Local inference, voice loading, and playback adapters.
- **MisakiSharp** — [Lyrcaxis/MisakiSharp](https://github.com/Lyrcaxis/MisakiSharp), Apache 2.0. Local text phonemization.
