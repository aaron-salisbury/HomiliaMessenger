![Homilia Messenger](./brand/logo_100.png)

# Homilia Messenger

Homilia Messenger is a desktop messenger for experimenting with conversations with AI agents. Every contact is an AI persona. You describe who they are, point them at an agent, and chat with them as if they were that person. You can change the agent or model behind a contact and carry on the same conversation.

See the [product specification](docs/product_specification.md) for the full direction.

## Current Features

- **Messenger:** a conversation list (most recent first, searchable, with message previews) and a chat thread with grouped message bubbles, timestamps, a typing indicator, and a composer (Enter sends, Shift+Enter adds a line). Start a chat with any contact, continue an existing one, or delete it. Each contact has one conversation, stored locally.
- **Memory:** as a chat grows, contacts with a model keep notes of what you told them earlier (names, plans, dates, promises), so they don't forget once older messages no longer fit. Open the notes from the chat header to read or correct what a contact remembers.
- **Optional spoken replies:** download and enable Kokoro in Settings (326 MB, Apache 2.0), then choose a contact's voice. The 28 American and British English voices are included with the app. Voice choices are filtered by gender; changing gender keeps the previously saved voice. Turn speech on from the chat ribbon to hear new replies alongside their text. Speech starts off after relaunch, and switching chats or leaving the Messenger page turns it off and cancels playback. Everything runs locally, without an account or GPU.
- **Persona contacts:** add, edit, and delete contacts with a name, nickname, birthday (required; personas must be 18 or older, and their age is calculated from it), gender (male or female), relationship to you (such as "Grandfather" or "Colleague"), personality, background, photo, and favorite flag.
- **Profile:** tell your contacts who you are: your name, age, gender, a few words about yourself, and a profile photo. Every contact knows it, along with how they are related to you, so your grandfather knows he is texting his grandson.
- **Local agents:** contacts can be answered by a small language model running on your own computer, with no server, account, or GPU. Download a model from Settings (Phi-3.5 mini, 2.8 GB, or Phi-4 mini, 4.9 GB, both MIT-licensed builds published by Microsoft), then choose it as a contact's agent in the contact editor. Replies are written in character from the contact's persona and the recent conversation. Downloads resume if interrupted and are checked against published SHA-256 hashes. Models run with [ONNX Runtime GenAI](https://github.com/microsoft/onnxruntime-genai).
- **Simulated replies:** contacts without an agent reply with random canned text, so the messenger works without any model.
- **Agent assignment:** each contact records the provider and model that speak for it, and each reply records which one wrote it. Changing a contact's agent keeps its conversation.
- Searchable contact list with favorites first, plus unsaved-change protection when switching contacts.
- Theme selection (System, Light, or Dark) that is remembered between sessions.
- Local SQLite persistence and rolling diagnostic logs.

Hosted agent providers and speech-to-text are planned for later phases.

## Privacy

All contacts and conversations are stored locally on your machine. Local chat and speech models run entirely on your computer, so chatting with them sends nothing anywhere. The only network use is downloading a model you choose: chat models from Hugging Face and Kokoro from GitHub. When hosted providers arrive, messages will be sent only to the provider you assign to a contact.

## Development

- The solution is `src/HomiliaMessenger.slnx`.
- Run `dotnet build/cake.cs` from the repository root to restore, build (Release) and test the solution; it uses [Cake.Sdk](https://cakebuild.net/docs/running-builds/runners/cake-sdk), pinned in `global.json`. Pass script arguments after `--` to change what runs, for example `dotnet build/cake.cs -- --target=Build --configuration=Debug` (without the `--`, `dotnet` consumes `--configuration` itself and the build stays in Release). Every run ends by deleting any empty `TestResults` folders in the repository; `--target=CleanTestResults` does only that.
- Run the app with `dotnet run --project src/HomiliaMessenger.DesktopApp`.
- Tests in the `LocalModel` category run the real Phi-3.5 mini model when it is downloaded on the machine (in the application data directory's `Models` folder) and are inconclusive otherwise. Exclude them with `--filter "TestCategory!=LocalModel"`.
- Tests in the `LocalSpeech` category synthesize real Kokoro audio without playing it. Set `HOMILIA_KOKORO_TEST_MODEL` to the official `kokoro.onnx` file from [KokoroSharpBinaries v2.0.0](https://github.com/Lyrcaxis/KokoroSharpBinaries/releases/tag/v2.0.0) to run them; they are inconclusive otherwise. Routine speech tests use fake downloads and playback. Windows playback uses NAudio; Linux/macOS playback requires a working OpenAL audio backend.
- As configured in the `Directory.Build.props` file, every project targets .NET 10, disables implicit using directives, and treats all types as not-nullable unless explicitly declared.
- The desktop application is built on the [Avalonia](https://github.com/AvaloniaUI/Avalonia) UI framework.
- Follow the [development conventions](docs/development_conventions.md).
