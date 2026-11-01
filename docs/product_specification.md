# HomiliaMessenger Product Specification

## Purpose

Homilia Messenger is a desktop messenger, styled after apps like Facebook Messenger or Snapchat, for experimenting with conversations with AI agents.

Every contact is an AI persona. The user describes who a contact is (name, birthday, gender, personality, and background), points the contact at an agent, and chats with them. The agent answers as that person would, in their voice and with their history, rather than as an assistant. For example, a contact might be a Vietnam veteran who is now a grandfather. He might tell war stories when asked, or be reluctant to.

The name comes from the Greek *homilia* (ὁμιλία), meaning conversation or companionable discourse.

## Product Principles

- **Contacts are people, not assistants.** Replies should read as the persona speaking conversationally, in character. The app never presents the persona as an AI helper unless the persona's own description says so.
- **The persona belongs to the contact; the agent is replaceable.** The contact's persona and its conversation history stay the same when the user changes the agent or model behind it. Changing agents mid-conversation is a supported experiment, not a reset.
- **Business owns persona framing.** The prompts that wrap user messages, telling the agent to answer conversationally as someone who fits the contact's details, are composed by Business from the contact's data. Agent providers only carry messages; they do not decide who the persona is.
- **Local and user-controlled.** Contacts, conversations, and memory are stored locally in SQLite. Nothing is sent to an agent provider except what a conversation the user starts requires.
- **Incremental.** Each phase delivers a usable app before the next adds capability. Abstractions are introduced only when a phase needs them.

## Users

The primary user is one person running the app on their own desktop to experiment with agents, personas, and models. Multi-user use, accounts, sync, and hosted operation are not goals.

## Architecture

The solution follows the repository's [development conventions](development_conventions.md):

- **Business** owns contacts, personas, conversations, persona framing, validation, and the capability contracts the app requires (such as persisting contacts or exchanging messages with an agent).
- **Data** implements persistence contracts using SQLite (Microsoft.Data.Sqlite with Dapper) and versioned schema migrations.
- **Integrations** adapts agent providers, and later speech providers, to Business capability contracts. It also provides the operating-system credential store (Windows Credential Manager, or Secret Service through `secret-tool` on Linux) behind the Business `ICredentialStore` contract.
- **Presentation.Desktop** provides Avalonia MVVM views and presentation state.
- **DesktopApp** is the executable and composition root. It owns startup, dependency injection, database initialization, logging, and the application lifetime.
- **Tests** contains automated tests across the solution, including architecture boundary tests.

The desktop app is the only host. Business capabilities must not depend on Avalonia, so another host could reuse them later.

## Core Concepts

### Contact

A persona the user can message. Contact details are deliberately limited to what shapes a conversation:

| Field | Purpose |
| --- | --- |
| Given name, family name, nickname | Who the persona is and how they are shown. At least one is required. The nickname, when set, is the display name. |
| Birthday | Required calendar date. The persona's age is derived from it, so personas age naturally over time. Personas must be at least 18 and no older than 150, judged against the current UTC date so the rule does not depend on the machine's time zone. |
| Gender | Free text, so it can say whatever the persona needs. |
| Personality | Temperament, values, humor, how they talk, and what they're guarded about. |
| Background | Life story, relationships, and experiences they can draw on. |
| Agent | The provider and model that speak for the contact. Optional until agents are integrated. Both values are required if either is set. |
| Photo | Optional JPEG or PNG avatar, 8 MiB or smaller. |
| Favorite | Pins the contact to the top of the list. |

Real-contact fields such as phone numbers, email addresses, postal addresses, organizations, social profiles, and vCard interchange are intentionally excluded.

### Agent Assignment

A reference to an agent: a **provider** (a backend the app knows how to talk to, such as a local Ollama server or a hosted API) and a **model** offered by that provider. Following the conventions' vocabulary, a provider implementation in Integrations satisfies the Business conversation capability, and a user's configured provider instance is an integration. Until providers are integrated, the provider and model are free-text identifiers.

### Conversation

The ongoing message history between the user and one contact. A conversation belongs to the contact, not to the agent, so it continues across agent changes. Each agent reply records which agent and model produced it.

### Persona Framing

Before a user message reaches an agent, Business builds the request. It includes the persona's current age (derived from the birthday), instructions to respond conversationally and in character as someone who fits the contact's details, the contact's persona fields, the relevant conversation memory, and the user's message. Persona framing is plain application logic and can be tested without a provider.

### Conversation Memory

What the agent is given about earlier turns. The initial approach is the most recent messages within a budget. A rolling summary of older history may be added once real conversations show it is needed. Memory is stored with the conversation, so it carries over when the contact's agent changes.

## Delivery Phases

### Phase 1: Persona Contacts

- Application foundation: solution layout, composition root, logging, SQLite persistence with migrations, and architecture tests.
- A contacts master/detail view: searchable list, favorites first, and a persona editor with photo.
- Add, edit, and delete contacts. Unsaved edits are protected by a discard confirmation.
- Business validation of contacts, with the result shown in the ribbon status.
- A Settings page whose theme choice (System, Light, or Dark) persists across restarts.

### Phase 2: Messenger (current)

- A messenger view: conversation list, chat thread with message bubbles, and a composer. It is built with the app's own controls; SukiUI is not adopted.
- Persisted conversations per contact.
- Simulated replies (random canned text) so the full messaging flow works before any agent is integrated.

### Phase 3: Agent Integration

- A Business conversation capability contract, with at least one provider implemented in Integrations.
- Persona framing and conversation memory as described above.
- Switching a contact's agent or model while keeping its conversation.
- Explicit handling of expected provider states (unavailable, unauthorized, invalid response) instead of surfacing raw exceptions.
- Provider secrets (such as API keys) stored through the existing `ICredentialStore` boundary, with only an opaque `CredentialReference` persisted, never the secret itself.

### Phase 4: Optional Voice

- Optional text-to-speech for persona replies (for example, Kokoro, as in AtaraxiaAI).
- Optional speech-to-text for composing messages.
- Both are opt-in messenger features and are never required to chat.

## Out of Scope

- Real human contacts, phone or email integration, and contact import or export (including vCard).
- Accounts, cloud sync, or multi-device use.
- Group conversations (may be reconsidered after Phase 3).
- Navigation or theming frameworks beyond the app's current Avalonia shell (for example, SukiUI or a new main-menu strategy).

## Persistence and Security

- Application data lives in the per-user application data directory: the SQLite database (`HomiliaMessenger.db`), desktop preferences (`desktop-settings.json`), and rolling daily logs.
- The schema changes only through versioned migrations recorded in `SchemaMigration`.
- Secrets, once providers need them, are resolved through `ICredentialStore` and never stored in provider configuration or SQLite.
