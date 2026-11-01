# Homilia Messenger

Homilia Messenger is a desktop messenger for experimenting with conversations with AI agents. Every contact is an AI persona. You describe who they are, point them at an agent, and chat with them as if they were that person. You can change the agent or model behind a contact and carry on the same conversation.

See the [product specification](docs/product_specification.md) for the full direction and delivery phases.

## Current Features

- **Messenger:** a conversation list (most recent first, searchable, with message previews) and a chat thread with grouped message bubbles, timestamps, a typing indicator, and a composer (Enter sends, Shift+Enter adds a line). Start a chat with any contact, continue an existing one, or delete it. Each contact has one conversation, stored locally. Contacts answer with random canned replies until agents are connected.
- **Persona contacts:** add, edit, and delete contacts with a name, nickname, birthday (required; personas must be 18 or older, and their age is calculated from it), gender, personality, background, photo, and favorite flag.
- **Agent assignment:** record the provider and model that will speak for each contact. Agents are not connected yet.
- Searchable contact list with favorites first, plus unsaved-change protection when switching contacts.
- Theme selection (System, Light, or Dark) that is remembered between sessions.
- Local SQLite persistence and rolling diagnostic logs.

Agent integration and optional voice are planned for later phases.

## Privacy

All contacts are stored locally on your machine. The application does not send data anywhere. When agent integration arrives, messages are sent only to the agent provider you assign to a contact.

## Development

- The solution is `src/HomiliaMessenger.slnx`.
- Run `dotnet build/cake.cs` from the repository root to restore, build (Release) and test the solution; it uses [Cake.Sdk](https://cakebuild.net/docs/running-builds/runners/cake-sdk), pinned in `global.json`. Pass script arguments after `--` to change what runs, for example `dotnet build/cake.cs -- --target=Build --configuration=Debug` (without the `--`, `dotnet` consumes `--configuration` itself and the build stays in Release). Every run ends by deleting any empty `TestResults` folders in the repository; `--target=CleanTestResults` does only that.
- Run the app with `dotnet run --project src/HomiliaMessenger.DesktopApp`.
- As configured in the `Directory.Build.props` file, every project targets .NET 10, disables implicit using directives, and treats all types as not-nullable unless explicitly declared.
- The desktop application is built on the [Avalonia](https://github.com/AvaloniaUI/Avalonia) UI framework.
- Follow the [development conventions](docs/development_conventions.md).
