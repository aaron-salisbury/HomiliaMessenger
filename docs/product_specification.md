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

Before a user message reaches an agent, Business builds the request. It includes the persona's current age (derived from the birthday), instructions to respond conversationally and in character as someone who fits the contact's details, who the user is (from the user profile) and how the contact is related to them, the contact's persona fields, the memories recalled for this reply, the recent messages, and the user's message. Who the user is comes right after the persona's identity, since small models attend most to the start of their instructions. Persona framing is plain application logic and can be tested without a provider.

### Conversation Memory

What the agent is given about earlier turns: the contact's memories recalled for the reply, plus the messages after the conversation's memory checkpoint.

#### Memories

A memory is one lasting thing a contact knows about the user, such as "Aaron's dog is Biscuit." Each is stored as its own record with its text, where it came from (learned from the chat, added or corrected by the user, or imported from the earlier notes), the conversation it belongs to, the message it was learned from, and when it was created and last changed.

- **Contact isolation.** Every memory belongs to exactly one contact, through the conversation it was learned in. Every read, search, and embedding is limited to one contact's memories; nothing is shared between contacts.
- **Lifecycle.** Deleting a contact deletes its memories and their vectors; so does deleting a chat, which starts the contact fresh, as deleting the notes did before. Memories survive restarts and changes to the contact's agent or model, since they belong to the contact, not the agent.
- **Never discarded for space.** Memories are kept however many there are. Only which of them are given with a reply is limited.

#### Learning memories

- Once the messages after the checkpoint grow past a budget, the contact's agent lists the lasting facts in all but the most recent ones, a batch of at most about 2,400 characters at a time, at most three facts per batch. This runs in the background after a reply.
- Only the user's messages are given to the agent for this. In testing with a small local model, including the contact's replies filled the few facts allowed with the persona's small talk, and the contact's invented dialogue is not authoritative about the user. A plan the contact proposed is remembered when the user's reply restates it, or the user adds it.
- Each new fact becomes a memory. A fact already remembered, differing only in case, punctuation, or spacing, is skipped. Facts that merely look alike, including ones that contradict a memory (a corrected day, a reversed relationship), are kept: losing a correction is the greater harm, and the user can delete a repeat.
- The new memories and the moved checkpoint are saved in one transaction, and only if the checkpoint has not moved meanwhile. If the agent fails, the work is canceled, the app closes, or the chat is deleted partway, nothing is saved and the batch is tried again after the next reply; the agent is still given the messages themselves until then. A model never rewrites existing memories, and a background update never overwrites the user's corrections.
- Folding happens in large steps, so the start of the prompt stays the same for most replies and local models can reuse their cache.
- Contacts with simulated replies learn no memories.

#### Embeddings

An embedding model turns a memory into a vector, so that memories can be found by meaning. Embedding models are a category of their own, separate from conversational models: they are listed under **Memory Search** in Settings, never offered as a contact's agent, and kept in their own folder (`Models/Embeddings`). They run on this device through llama.cpp, like GGUF conversational models, in a host of their own, so searching memories never unloads the contact's model.

- Two models are offered, pinned to a published revision and checked against its SHA-256 hash: BGE small (37 MB, 384 dimensions) and Nomic Embed Text v1.5 (84 MB, 768 dimensions). The first one downloaded is used; the user can choose another.
- Nothing is embedded until the user downloads a model. Embedding never calls a remote service.
- Each vector is stored as a BLOB of little-endian 32-bit floats with the model that made it and its length, at most one per memory. Vectors from different models are never compared; vectors of the wrong length, or with values that are not finite numbers, are refused when returned and skipped when read.
- Memories are embedded in the background: after each reply, after the user changes memories, and when the user chooses a model. A memory without a vector from the model in use (new, corrected, embedded by a model no longer chosen, or not embedded because embedding failed) is simply embedded on the next pass. Correcting a memory discards its old vector in the same transaction, and a vector is saved only if the memory still has the text that was embedded.
- Changing the model keeps every memory's text and old vector; memories are embedded again with the new model in the background, and are recalled by their words until then. Removing a model keeps the vectors, so choosing it again later needs no re-embedding.

#### Recall

For each reply:

1. When all of the contact's memories fit within the memory budget (about 1,000 characters, what the notes allowed), they are all given, in the order they were learned, so the prompt stays the same between replies.
2. Otherwise the user's latest messages are embedded once, and compared exactly, in C#, with the contact's vectors from the same model. Memories below the model's relevance threshold are left out. Thresholds differ by model, so each catalog model carries one calibrated on a small set of memories and questions (BGE small 0.48, Nomic 0.59).
3. Memories without a comparable vector, and every memory when no embedding model is ready or embedding fails, are matched by the words they share with the user's latest messages.
4. The most relevant memories are chosen within the budget, then a few of the newest, as the notes always ended with the newest facts. A memory that repeats one already chosen (the same text, or a nearly identical vector) is given once; both stay stored.
5. The chosen memories are given in the order they were learned.

No model is asked to choose, so recall adds at most one embedding of the user's message to a reply. Memories are written into the instructions as a list of facts, one line each, after the persona's own details and before how to reply, so they read as things the persona knows rather than instructions. Memories are only learned from messages before the checkpoint, which the agent is no longer given, so recalled memories never repeat the recent messages.

Recall never fails a reply: an embedding provider that is unavailable, slow to load, or failing only means memories are matched by their words.

#### Reading and correcting memories

The user can read what a contact remembers from the chat header, in a dialog that keeps it out of the conversation. Each memory shows whether it was learned from the chat, added by the user, or imported from the earlier notes, and can be corrected or deleted; the user can add memories of their own. Changes are saved as they are made. Correcting a memory makes it the user's own, and an edit of a memory changed meanwhile is refused rather than overwriting it. Vectors and other implementation details are not shown.

#### Migration from memory notes

Before memories were individual records, a conversation kept them as one block of notes (`Conversation.MemoryNotes`) that a background update appended to, dropping the oldest lines when full. Schema version 6 imports each non-blank line of those notes as a memory of its own, marked as imported from the earlier notes, in their original order and without duplicates. The import runs in the migration's transaction, recorded in `SchemaMigration`, so it either completes once or leaves nothing behind and runs again at the next start. The notes column is kept unchanged as a copy of the notes as they stood, but is no longer read or written. `Conversation.MemoryThroughMessageId` keeps its meaning, the last message condensed into memory, so no message is condensed twice.

#### Limitations and extension points

- Search compares every vector a contact has. Measured in a Release build, that takes about 26 ms for 1,000 memories of 384 dimensions and 110 ms for 5,000 of 768 dimensions, well within the seconds a local model takes to reply. A contact learns at most three memories per 2,400 characters of chat, so this suffices; if contacts ever hold tens of thousands of memories, an indexed search such as the sqlite-vec extension can replace the store's similarity search without changing the memory contracts.
- Relevance thresholds are calibrated on a small sample and may need tuning for other kinds of memories. Other providers can report their own threshold; otherwise a default applies.
- Duplicate detection is deliberately strict, so a reworded repeat is stored twice (and recalled once). Contradictions are kept, and the agent is given both rather than a guess at which is true.
- The memory dialog lists every memory; it has no search, which a contact with very many memories may eventually need.
- Recall is by English-language embedding models and English word matching.

### Web Search

Optional, off by default. Before a contact with a model replies, Business decides from the latest message whether a search would help: deterministic rules for explicit requests and questions about recent things, and a constrained yes-or-no question to the contact's own model for questions about specific named things. The query is built by Business from that message alone, without names or contact details. Tavily (Integrations) runs at most one basic search per reply; Business enforces a local credit budget per billing period, respects limits Tavily reports, caches results by query, and never lets a failed search stop a reply. Up to three short, sanitized results are added to the persona instructions as untrusted information, and the query and sources are kept with the reply for the user, never as memories. See [web search](web_search.md).

## Persistence and Security

- Application data lives in the per-user application data directory: the SQLite database (`HomiliaMessenger.db`, including contacts' memories and their vectors), desktop preferences (`desktop-settings.json`), rolling daily logs, and downloaded local models (`Models`, with embedding models in `Models/Embeddings`).
- The schema changes only through versioned migrations recorded in `SchemaMigration`.
- Secrets are resolved through `ICredentialStore` and never stored in provider configuration or SQLite. The Hugging Face access token and the Tavily API key are kept this way.
- Web search settings, the Tavily credit count per period, cached search results, and each reply's lookup (query and sources) are stored in SQLite (schema version 7).
