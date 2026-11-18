# Image Generation: Contact Photos and Selfies

Contacts' photos can be generated on this computer from a description of the person, as an alternative to choosing a photo file. The feature is optional: it needs a 1.8 GB image model, downloaded only when you ask for it in Settings. It runs on the CPU without a GPU, account, or online service. The same models can also take selfies contacts send in chats (see [Selfies](#selfies)).

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

## Selfies

Contacts can send selfies in chats. A selfie shows the contact somewhere, doing something, looking some way, as the conversation suggests, and keeps the face in their profile photo.

### Asking for a selfie

- **The camera button** in the messenger's ribbon, beside the spoken-replies button, asks the open chat for a selfie. Its tooltip says what it will do, or why it cannot: no selfie model is downloaded, nobody in the chat has a profile photo, a new chat has no messages yet, or a selfie is already being taken (one at a time, since images are drawn one at a time).
- **While it is taken** (about 35 seconds on a fast desktop, longer on a laptop), a bubble beneath the messages shows who is taking it and how far along it is, with **Stop**. The chat goes on meanwhile: you can keep writing, switch chats, or leave the messenger. Replies wait for the image model to finish, since only one model is kept in memory at a time; the selfie waits for a reply already in progress. The selfie is saved when it is ready, after whatever was written in the meantime.
- **It arrives** as the contact's message: the picture, with what they say with it, if anything. Right-click the picture to save it as a PNG. Its tooltip says what it shows. In the chat list it shows as "📷 Selfie" when it has no words.
- **Contacts read it as words**: agents cannot see pictures, so each one is kept with a description ("Sent a selfie: laughing, waiting for the bus, at a bus stop, pouring rain"), which contacts are given in its place, so they can react to it. Nothing about a selfie is learned as a memory; memories come from your messages only.

### Who is in it

A selfie shows **one person**. Stable Diffusion 1.x mixes up the features of two people in one picture (a beard or hair color moves from one to the other), and face matching takes one face, so selfies are of one contact. Only contacts **with a profile photo** can be in a selfie.

- **In a chat** with one contact, it is of them.
- **In a group**, the group coordinator chooses, among those with a photo, whoever the conversation points to: the contact your latest message spoke to by name ("Elena, where are you?"), then one it named, then a contact who just offered a picture ("I'll send you a pic!"), then whoever wrote most recently. With nothing to go on, the contact who joined first. The choice is plain rules, like the rest of turn-taking, so the same conversation always gives the same person.
- **Later models** may draw more people. The selfie capability takes its subjects in order of likelihood and says how many the active model can draw; only the Stable Diffusion integration limits it to one. A model that draws several people faithfully would receive more of them without changes elsewhere.

### What it shows

The contact in the selfie plans it with **their own model**, before it is drawn. Their model is given who they are (their appearance, personality and background), the time of day, and the latest messages, and is asked for a few labelled lines: expression, clothing, location, activity, one prop, weather, light, and a caption in their own voice, to decide where they are and why they are sending a selfie now. Where the conversation says nothing, it chooses something ordinary that fits them. Anything missing or unclear is left out. A contact with simulated replies, or whose model cannot answer, sends a smiling selfie without words. Planning never stops a selfie.

**How the contact looks** comes from their profile photo, in two parts:

- **The face** is carried from the photo itself by face matching (IP-Adapter Plus-Face), at strength 0.3. In the experiment, likeness held equally at 0.25 to 0.4 (beards, glasses, hair, age), and lower strengths let less of the photo's clothing through.
- **The words**: when the photo was generated in the app, the description it was generated from is now saved with the contact (only the person, not its clothing or setting, is used); a photo chosen from a file is described by the contact's gender and age ("woman in her thirties"). Clothing comes from the plan, otherwise from that description.

### How a plan becomes a prompt

Again deterministic, following the local selfie experiment (three rounds, about 170 images):

1. **The framing first**: "Close-up smartphone selfie from slightly above, face close to the camera." Put first, it made a selfie; later, the image read as a photo someone else took.
2. **The expression and the person**, with emphasis weights, such as `(irritated:1.4), (annoyed scowl:1.4), (furrowed brow:1.3) man in his fifties`. The experiment's reliable expressions use the phrasing that worked: laughing, grinning or proud, irritated, sad, exhausted, sleepy, cold, smiling. Subtle ones mostly did not render as faces (embarrassed, nervous, shocked); embarrassment is drawn as a gesture (a hand over half the face, blushing), which worked about half the time.
3. **States and things**: rain soaks the person (`(soaking wet:1.5)`, the experiment's most reliable state); clothing is weighted, which overrode the photo's; a prop is "held up beside the face" at weight 1.5, the only way a prop reliably appeared.
4. **The scene**: location, weather and light, and what they are doing, dropped first if the prompt runs past one CLIP chunk.
5. **A negative prompt** naming what the model drifts to instead (for irritated: happy, smiling, calm), plus "deformed, blurry". It takes effect because selfies use CFG 1.5.

Words from the conversation are kept, without the brackets and colons the runtime reads as emphasis.

### Settings

DreamShaper 8 LCM for selfies: 512×640 (a 512×768 canvas drew duplicated faces), LCM, 4 steps, CFG 1.5 (CFG 2 followed prompts equally but oversaturated more, at the same cost), face matching at 0.3.

| | DreamShaper 8 LCM selfie, 512×640 |
|---|---|
| Time per selfie | about 34 seconds on an i9-12900K (planning with the contact's model adds its own time) |
| Memory while drawing | about 4.5 GB process peak (4.9 GB measured in the test process with LLamaSharp loaded) |
| Disk, besides the model | 0.77 GB face matching |

### Installing

**Settings → Image Generation** has two parts:

- **Profile photos**: the model contact photos are generated with, and its **Add from Hugging Face**, as before.
- **Selfies**: the model selfies are taken with, its own **Add from Hugging Face**, and whether face matching is downloaded. DreamShaper 8 LCM is listed here too, and is used for selfies by default once downloaded for either. Its description says what choosing it for selfies adds: face matching, a 2.6 GB download (IP-Adapter Plus-Face, 98 MB, and the ViT-H/14 image encoder, 2.5 GB), made 0.8 GB once prepared. Downloading it for selfies downloads only what is missing.

Face matching is downloaded from [h94/IP-Adapter](https://huggingface.co/h94/IP-Adapter) at a pinned revision and checked against its SHA-256 hashes. The image encoder is then converted to Q8_0 with the integrated runtime (`convert_with_components`, called as the runtime's command-line tool calls it), in about 3 seconds, and the downloaded 2.5 GB file removed. The conversion is byte-for-byte the experiment's (the `ImageModel` test compares its SHA-256); the Q8_0 encoder drew selfies indistinguishable from the full-precision one, at 4.5 GB peak instead of 6.2 GB. An interrupted preparation is finished without downloading again.

Removing works per purpose: removing DreamShaper under **Selfies** removes face matching only (every selfie model needs it) and keeps the model for contact photos; removing it under **Profile photos** removes the model. A model added from Hugging Face for both is kept for the other when removed from one. A model added for selfies draws at 512×640 with the settings entered; its compatibility with face matching is not verified. Face matching is Apache 2.0; the license notice shown before downloading says so.

### Not included

- Selfies of more than one person (see above), and of the user.
- Choosing who a group's selfie is of; the coordinator decides.
- Viewing a selfie larger in the app; save it to view it full size.
- Contacts sending selfies unasked.

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

- Business: `Modules/ImageGeneration` (`IImageGenerator`, `ISelfieImageGenerator`, `IImageModelLibrary`, `PortraitPromptComposer`, `PortraitService`) and `Modules/Selfies` (`SelfieService`, `SelfieDirection` for planning and describing; subject choice is `GroupCoordinator.RankSelfieSubjects`). Integrations: `ImageGeneration/` (catalog with face matching, library, GGUF header inspector, native binding, one generator for photos and selfies, `SelfiePromptComposer`, `FaceReference`). Data: schema version 10 adds `ChatMessage.ImagePng`/`ImageDescription` and `Contact.Appearance`. Presentation: the Image Generation section in Settings, `AddImageModelWindow` (per purpose), `GeneratePhotoWindow`, and the selfie button and bubble in the messenger.
- The selfie `ImageModel` tests (`RealSelfieModelTests`) need `HOMILIA_IMAGE_TEST_MODEL`, `HOMILIA_FACE_MATCHING_TEST_DIR` (a folder named `face-matching` with the adapter and the prepared encoder), `HOMILIA_SELFIE_TEST_PHOTO` (a portrait), and, for the conversion test, `HOMILIA_IMAGE_ENCODER_TEST_FILE` (the published encoder).
- Image models are a category of their own: never contacts' agents, never embedding models.
- Tests in the `ImageModel` category use the real DreamShaper checkpoint: set `HOMILIA_IMAGE_TEST_MODEL` to its path (or download it in Settings). They are inconclusive without it, and include a pixel-for-pixel comparison with the experiment's reference image and a cancellation test. All other image generation tests use a fake runtime and a fake Hugging Face.
