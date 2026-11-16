# Contact Photo Generation

Contacts' photos can be generated on this computer from a description of the person, as an alternative to choosing a photo file. The feature is optional: it needs a 1.8 GB image model, downloaded only when you ask for it in Settings. It runs on the CPU without a GPU, account, or online service.

The aim is a polished, slightly idealized photographic portrait, a believable profile picture rather than documentary realism, that keeps the attributes you asked for.

## Installing the model

1. Open **Settings → Image Generation**.
2. Choose **Download** beside **DreamShaper 8 LCM** (1.8 GB, CreativeML Open RAIL-M).
3. Read and accept the license notice (see [Licensing](#licensing)).
4. Watch the progress bar. **Pause** stops the download, and downloading again resumes it. When the files have been checked against their published SHA-256 hashes, the model is marked **In use** and is ready at once. Nothing else has to be configured.

Image models are never downloaded at installation or start-up. If you have more than one image model downloaded, choose **Use** beside the one that should make new photos; with only one, it is used automatically. Removing a model deletes its files; photos already saved to contacts are kept.

Models are stored in the application data directory under `Models/Images/<model>/`, apart from chat models (`Models/`) and embedding models (`Models/Embeddings/`). The publisher's `LICENSE.txt` is downloaded and kept beside the DreamShaper checkpoint.

## Generating a photo

1. On the **Contacts** page, choose the wand button beside the contact's photo (**Generate photo**).
2. Describe the person: age, appearance, and optionally clothing and a setting. For example, *"An older man with a long white beard and glasses, wearing a plaid shirt in his workshop."*
3. Choose **Generate**. The dialog shows its progress: getting ready, drawing, finishing.
4. Look at the result. **Generate another** makes a new likeness from the same description, with a fresh random seed. The photo shown changes only when the new one is ready.
5. Choose **Use photo** to put it on the contact, then **Save** the contact as usual. **Cancel**, or closing the dialog, leaves the contact's photo exactly as it was.

The description is remembered while the contact is open, so it can be adjusted rather than retyped. Uploading a photo file works as before; a generated photo is stored the same way, as a PNG in the contact's record.

If no image model is installed, the dialog says so and offers **Open Image Generation settings**; it never downloads anything itself.

### How a description becomes a prompt

The app rewrites the description into a short prompt, without another model and the same way every time:

1. the person and their attributes, as you wrote them;
2. clothing and accessories;
3. framing: a close head-and-shoulders portrait by default, or waist-up when you ask for clothing seen below the chest (overalls, a dress, an apron…) or something behind the person; your own framing words ("full body", "close-up") are kept instead;
4. the setting;
5. a brief style phrase: soft natural light, polished photographic style.

*"A middle-aged Black farmer in denim overalls with a tractor behind him."* becomes *"Middle-aged Black farmer in denim overalls. Waist-up portrait, facing the camera. A tractor behind him. Soft natural light, polished photographic style."*

Nothing about who the person is (age, gender, ethnicity, build) is ever added, and your words are never cut. Only articles and joining words may be dropped. Bare "glasses" becomes "eyeglasses". The added words are shortened, never yours, to keep the prompt within one 75-token chunk of Stable Diffusion's text encoder; a longer description still works. No negative prompt is used: at the model's guidance setting it has no effect.

This follows the local portrait experiment, in which concise, subject-first prompts rendered the requested person far more faithfully than a long photographic preamble, and a close crop kept awkward hands and unrequested props out of frame.

## What to expect

Measured on a 12th-generation Intel Core i9-12900K (Windows 11, x64, CPU only):

| | DreamShaper 8 LCM, 512×512, 4 steps |
|---|---|
| Time per photo | about 17–19 seconds, of which about 9 drawing and 8 decoding |
| Memory while generating | about 3.7 GB (process peak) |
| Disk | 1.8 GB model + 46 MB runtime included with the app |

Slower and older CPUs will take longer; that has not been measured. To stay within memory on ordinary computers:

- only one photo is generated at a time;
- an idle chat model is released before the image model loads, and reloads on the contact's next reply (a reply in progress finishes first);
- the image model is loaded for each photo and released as soon as it is done, rather than kept in memory. Loading its weights is a few of the seconds a photo takes, a cost "Generate another" pays again.

Generation runs on a background thread, so the app stays responsive.

**Stopping.** **Stop**, or closing the dialog, asks the runtime to stop. stable-diffusion.cpp checks between drawing steps, so a stop during drawing takes effect within a step (measured at 0.1 seconds). The last stage, decoding the image (about 8 seconds), cannot be interrupted; a stop then takes effect when it ends. The app never aborts a thread or tears down the runtime mid-computation.

**Quality.** Faces are usually coherent and the style polished, but this is a small, fast model: eyes, mouths, and especially hands are sometimes malformed, and a requested attribute is occasionally missed. Generating another is the remedy. No face restoration, upscaling, or beauty filtering is applied.

## Adding image models from Hugging Face

For experimenting, **Settings → Image Generation → Add from Hugging Face** adds other Stable Diffusion 1.x models:

1. Enter a repository (`owner/name` or its address) and choose **Look up**. The app lists the repository's GGUF files and reads each one's header (the first 512 KB or so, by range request) to tell what it holds. Nothing else is downloaded. A file's name and repository are not trusted; its tensors are.
2. Each file shows its size, its model family, its weight format, and whether the text encoder and VAE are included, or why it cannot be used:
   - **Stable Diffusion 1.x, self-contained**: can be added.
   - **Stable Diffusion 1.x missing its text encoder or VAE** (such as UNet-only conversions): refused; separate component files are not supported yet.
   - **Inpainting or image-editing variants**: refused; they need a source image.
   - **SD 2.x, SDXL, SD3, FLUX, and other families**: refused; only SD 1.x has been verified with this app's runtime and memory budget.
   - **Language models**: refused, with a pointer to Local Models.
   - **The built-in DreamShaper file**: recognized by its hash and pointed to the built-in entry.
3. Choose a supported file and enter the settings its model card recommends: sampler, steps, and CFG. The defaults (Euler ancestral, 20 steps, CFG 7) suit an ordinary checkpoint and are the runtime's documented SD 1.5 example. When the file name mentions LCM, Turbo, Lightning, or Hyper, the dialog points out that such models need different settings, but it changes nothing itself.
4. Acknowledge that the model's compatibility is unverified and that its license applies, then **Add and download**.

Added models are pinned to the repository revision they were looked up at, verified against Hugging Face's SHA-256 hashes, marked **Unverified** in Settings, and show the settings they were given. Gated repositories use the Hugging Face access token saved under **Local Models → Add from Hugging Face**. Photos are 512×512 for every model.

What is not supported yet: `.safetensors` and `.ckpt` checkpoints (only GGUF), separate VAE, text encoder, or LoRA files, other resolutions, choosing a scheduler explicitly (the runtime's default for the sampler is used), and editing an added model's settings (remove and add it again). The header check identifies the architecture reliably but cannot prove a file was converted correctly; an unverified model may fail to load, or draw poorly with the wrong settings.

## Platforms

| | Status |
|---|---|
| Windows x64 | **Verified**: build, publish, and real generation, pixel-identical to the experiment's in-process and CLI runs, with LLamaSharp's runtime loaded in the same process. |
| Linux x64 | **Not verified.** Build and publish include the pinned upstream Linux CPU runtime, renamed and checked structurally, but no generation has been run on Linux. The upstream build targets Ubuntu 26.04 and needs a recent `libstdc++` (`GLIBCXX_3.4.32`, GCC 13 or later) and `libgomp`; older distributions may not load it. |
| macOS, ARM | Not included. The Image Generation section explains that image generation is unavailable on this system. |

The CPU backend comes in variants for different instruction sets (SSE4.2 to AVX-512); ggml loads the best one the CPU supports. CPUs without SSE4.2 are not supported.

## The native runtime

Images are generated in-process by [stable-diffusion.cpp](https://github.com/leejet/stable-diffusion.cpp), pinned to commit `5dcfa79` (release `master-1104-5dcfa79`), through a small P/Invoke binding (`Integrations/ImageGeneration/StableDiffusionNative.cs`). No Python, Docker, local server, external executable, or online service is involved.

- **Packaging.** The build downloads the pinned release archive for the system being built for, checks its SHA-256 hash, and copies only the libraries the app loads (the runtime, ggml, and its CPU backend variants), plus their MIT notices, to `StableDiffusion/<rid>/` beside the app: 46 MB on Windows, 65 MB on Linux. Users never install anything. The archive is cached under the project's `obj/` folder; a build without network access warns and leaves image generation out, while a publish fails instead. The native libraries are never committed to the repository. See `build/native/StableDiffusionRuntime.targets`.
- **Renamed ggml.** stable-diffusion.cpp ships its own ggml, which conflicts with the ggml LLamaSharp loads for chat and embedding models. The Windows and Linux loaders bind a dependency to an already loaded library of the same name, so whichever runtime loaded second failed ("The specified procedure could not be found"), in either order. The build therefore renames the runtime's private ggml to `sdml` (`sdml.dll`, `sdml-base.dll`; `libsdml.so.1`, `libsdml-base.so.1`). Only file names, and the dependency names inside the libraries (PE import and export names, ELF `DT_NEEDED` and `DT_SONAME`), are rewritten, in place and at the same length; no code changes. The build fails if a library does not name the expected dependency, as it would after an unreviewed runtime upgrade. The renamed runtime produced the experiment's image pixel for pixel with LLamaSharp loaded first.
- **Safety checks.** At load, the binding refuses a runtime built from any commit but `5dcfa79`. Before writing parameters, it checks that each field it writes holds the library's documented default, which would not hold if the struct layout differed from the pinned header. Native contexts are owned by a `SafeHandle` and released after every photo, and every native result buffer is freed.
- **GPU.** Not used. The backend is explicitly `cpu`; a GPU backend would need its own runtime build, tests, and memory policy.

**Upgrading the runtime:** update the release URL, commit, and archive hashes in `StableDiffusionRuntime.targets` and `StableDiffusionNative.PINNED_COMMIT`; diff the new `include/stable-diffusion.h` against the prefix structs and default checks in `StableDiffusionNative.cs`; then run the `ImageModel` tests with the real model, which compare a generated image's pixels with a reference.

## Licensing

The model and the runtime have different licenses.

- **DreamShaper 8 LCM** ([Lykon/dreamshaper-8-lcm](https://huggingface.co/Lykon/dreamshaper-8-lcm), a Stable Diffusion 1.5 fine-tune), converted to GGUF by [NishQ1/vora-create-sd15](https://huggingface.co/NishQ1/vora-create-sd15), is under **CreativeML Open RAIL-M**. That license allows use and redistribution, but with use-based restrictions (its Attachment A). Paragraph 5 requires anyone who lets others use the model to require them to comply. So before downloading, the app shows a notice summarizing the restrictions and asks the user to agree; the full license is linked from Settings and downloaded with the model. The app does not host or redistribute the weights: they are downloaded directly from the publisher's Hugging Face repository at a pinned revision.
- **stable-diffusion.cpp** and **ggml** are MIT. Their license notices are included beside the libraries in every build and publish (`StableDiffusion/<rid>/LICENSE.*.txt`) and credited in `Attributions.md`.
- Models added from Hugging Face come with their own licenses, shown in the add dialog and in Settings; the user is responsible for them.

Open questions, not legal advice:

- Whether the in-app notice and acknowledgment are enough to "require" users' compliance under paragraph 5, or whether the app's own terms should incorporate Attachment A.
- Whether a public release should show the license text in the app itself rather than linking to it.
- Paragraph 7 lets the licensor restrict use and asks users to use the latest version of the model; the app pins one revision and has no update mechanism.
- Generated images carry no watermark or provenance metadata. The license does not require one, but some jurisdictions' AI-content rules may expect disclosure.

## Privacy

Descriptions and generated photos never leave the computer. Network use is limited to downloading a model you choose (and, for "Add from Hugging Face", reading repository information and file headers), from Hugging Face.

## For developers

- Business: `Modules/ImageGeneration` (`IImageGenerator`, `IImageModelLibrary`, `PortraitPromptComposer`, `PortraitService`). Integrations: `ImageGeneration/` (catalog, library, GGUF header inspector, native binding, generator). Presentation: the Image Generation section in Settings, `AddImageModelWindow`, and `GeneratePhotoWindow`.
- Image models are a category of their own: never contacts' agents, never embedding models.
- Tests in the `ImageModel` category use the real DreamShaper checkpoint: set `HOMILIA_IMAGE_TEST_MODEL` to its path (or download it in Settings). They are inconclusive without it, and include a pixel-for-pixel comparison with the experiment's reference image and a cancellation test. All other image generation tests use a fake runtime and a fake Hugging Face.
