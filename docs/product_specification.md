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
- **Integrations** adapts agent and speech providers to Business capability contracts. It also provides the operating-system credential store (Windows Credential Manager, or Secret Service through `secret-tool` on Linux) behind the Business `ICredentialStore` contract.
- **Presentation.Desktop** provides Avalonia MVVM views and presentation state.
- **DesktopApp** is the executable and composition root. It owns startup, dependency injection, database initialization, logging, and the application lifetime.
- **Tests** contains automated tests across the solution, including architecture boundary tests.

The desktop app is the only host. Business capabilities must not depend on Avalonia, so another host could reuse them later.

## Core Concepts

### Agent Assignment

A reference to an agent: a **provider** (a backend the app knows how to talk to, such as a local Ollama server or a hosted API) and a **model** offered by that provider. Following the conventions' vocabulary, a provider implementation in Integrations satisfies the Business conversation capability, and a user's configured provider instance is an integration. Until providers are integrated, the provider and model are free-text identifiers.

### Conversation

The ongoing message history between the user and one contact. A conversation belongs to the contact, not to the agent, so it continues across agent changes. Each agent reply records which agent and model produced it.

### Persona Framing

Before a user message reaches an agent, Business builds the request. It includes the persona's current age (derived from the birthday), instructions to respond conversationally and in character as someone who fits the contact's details, who the user is (from the user profile) and how the contact is related to them, the contact's persona fields, the relevant conversation memory, and the user's message. Who the user is comes right after the persona's identity, since small models attend most to the start of their instructions. Persona framing is plain application logic and can be tested without a provider.

### Conversation Memory

What the agent is given about earlier turns: the contact's memory notes plus the messages the notes do not cover yet.

- Once the messages not yet in the notes grow past a budget, the contact's agent lists the facts worth remembering from all but the most recent ones (what the user shared about their life, the people, places, dates, and plans in it, and anything either side agreed to), and those facts are appended to the notes. This runs in the background after a reply.
- The notes are only appended to, never rewritten by a model: small models asked to rewrite a running summary lose earlier facts a little more each time. When the notes are full, the oldest facts give way.
- Folding happens in large steps, so the start of the prompt stays the same for most replies and local models can reuse their cache. The reply after a fold reads the whole prompt again.
- The user can read and correct the notes from the chat, in a dialog that keeps them out of the conversation itself. A background update never overwrites the user's corrections.
- Contacts with simulated replies keep no notes.

Memory is stored with the conversation, so it carries over when the contact's agent changes.

## Persistence and Security

- Application data lives in the per-user application data directory: the SQLite database (`HomiliaMessenger.db`), desktop preferences (`desktop-settings.json`), rolling daily logs, and downloaded local models (`Models`).
- The schema changes only through versioned migrations recorded in `SchemaMigration`.
- Secrets, once providers need them, are resolved through `ICredentialStore` and never stored in provider configuration or SQLite.
