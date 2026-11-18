# Group Conversations

A group is a chat between you and two contacts. Each contact stays who they are, with their own personality, model, voice, and memories, and the group decides turn by turn who writes, so it reads like friends talking rather than every contact answering every message.

## Using groups

- **Start one from a chat.** Open a chat with a contact and choose **Invite** (the person-plus icon in the chat header), then pick another contact. A new group with the two of them opens. The chat you started from is left exactly as it was, messages and memories included; a group never takes over a private chat. If those two contacts already have a group, it opens instead of a second one starting.
- **Find it in the chat list.** Groups sit in the same list as chats, named after their contacts ("Marcus & Elena") with both photos.
- **See who is in it.** The group icon in the header lists the contacts, their relationship to you and model, and opens what each one remembers.
- **Who wrote what.** Your messages look as they always do. Each contact's messages carry their photo, with their name above each run of their messages.
- **Size.** A group has exactly two contacts for now, plus you. Once a group has both, there is no invite button. The limit is one constant (`GroupConversations.MAX_CONTACTS`); storage already allows any number.
- **Deleting.** Deleting a group deletes its messages and what its contacts learned in it; your private chats with them are kept. Deleting a contact deletes the groups they were in, as it deletes their chat.

## Contacts keep who they are

A group never copies a contact or asks for one model for the group. Every message is written by its sender's own agent and model, from their own persona, with their own memories, and spoken in their own voice: Marcus can run Phi-4 mini through ONNX while Elena runs a GGUF model through llama.cpp. Each message records who wrote it and which model.

When it is a contact's turn, they are told who else is in the group and how each is related to you, and are given the group's recent messages with each line labelled by who wrote it, so they know who said what. Their own earlier messages are their own turns. They are asked to write only their next message, and stopped if they begin writing anyone else's: a reply that opens as you or another contact is discarded.

## Memory

Memory has three scopes:

- **What a contact remembers** belongs to that contact alone, wherever they learned it. In a group, a contact is given the memories of theirs that relate to the conversation, including things you told them privately, so Marcus can follow up on the project you told him about. Elena is never given Marcus's memories, and never his private chat.
- **The group's history** is the messages everyone in it saw. Each contact is given the recent part of it.
- **What a contact learns in a group** is learned as their own memory, from your messages in the group (as in a private chat, never from what the contacts said, so nothing one contact said is ever attributed to you). Each contact keeps their own checkpoint of how far they have learned, so the same messages are never learned twice, and one contact's memories are never written as another's.

Contacts are asked to use what they know naturally but not to volunteer private or sensitive things you told them alone, unless you bring them up or it is clearly appropriate. This is an instruction to a small model, not a guarantee: a contact may still let something slip, as a person might. What is guaranteed is the isolation: a contact can only reveal what they themselves were told.

## Who writes next

After you write, the group takes a round of at most three messages, then waits for you.

- **Spoken to by name, a contact answers.** "Marcus, what do you think?", "hey marcus", or "what about you, Elena?" goes to that contact, whether you or the other contact asked. No special syntax is needed (though "@Elena" works too): a name opening a sentence or set off by commas counts, while a name merely mentioned ("I asked Marcus", "did Marcus tell you?") does not. A name at the end without a comma ("how's it going Marcus?") is not recognized, so that message goes to whoever it bears on most.
- **A message for everyone** ("you two", "guys", "everyone") is answered by each.
- **Otherwise the message goes to whoever it bears on most:** what they remember about it, their personality and background, and who has done less of the talking lately.
- **Then the other contact may add something** (a reaction, a different view, a question), but need not. When the contact who just wrote uses the same model, that model is asked a one-word yes-or-no question about it, once per round; otherwise, or when its answer is unclear or it fails, chance decides, weighted by personality (talkative contacts speak up more), how much they have said lately, and whether a question was left open.
- **Never twice in a row, never more than three.** The same contact never writes twice in a row, and a round ends after three messages even if the contacts keep addressing each other.

Writing again during a round sets it aside and starts a new one that answers your new message. The stop button in the header ends a round at once. A contact who cannot reply (say their model is not downloaded) is shown as a problem and passed over for the rest of the round, so the other can still answer.

## Asking the group to keep talking

The small button beneath the photo of whoever wrote last offers two choices:

- **Continue with [name]** asks that contact for one more message, and only that one: the other contact is not prompted to answer it.
- **Continue group conversation** lets the group decide who writes next, as it does after you write: someone spoken to by name answers, otherwise whoever is likelier to speak up, never the contact who just wrote. That first message is always written, since you asked; the other contact may then add something, within the usual round of at most three messages.

Either way nothing is written as you: the contact is told, in that one prompt, that you have not written anything new and to build on the conversation, or bring up something related, without repeating themselves. It works whether or not the group may start conversations on its own, and does not use up that chance. While a round is under way the button is disabled; writing, stopping, or leaving the group ends it as it ends any round. In a private chat the same button asks the contact straight away.

## Contacts starting conversations

While you have a group open, its contacts may now and then start a conversation without waiting for you:

- **When you open the group** (or come back to it), the group gets one chance: if it has been quiet for half an hour, nothing is waiting on you (your own message without an answer, or a contact's question), and it has not had a chance in the last four hours, a contact starts something about half the time. A new group says hello instead; if the hello is interrupted (you leave first) or the model fails, it is tried again the next time you open the group, and once written it starts the cooldown.
- **Only while it is open.** Nothing is written for a group you are not looking at, while you are elsewhere in the app, or while the app is closed. There is no polling and nothing is scheduled; leaving the group stops anything under way.
- **Spaced out.** The chance is used up whether or not a contact takes it, and when it was taken is saved, so switching away and back does not bring a stream of unprompted messages.
- **Turning it off.** In the group's member list, switch **Occasionally start conversations** to **Wait for me to write first**. Contacts then write only after you do, though they may still answer each other within a round. It is on by default.

## Speech

With spoken replies on, each contact's messages are spoken in their own voice, one after another in the order they appear, never over each other. A contact without a voice is simply not spoken. The conversation never waits for speech, and turning speech off, switching chats, or leaving the messenger stops it.

## Models and resources

Contacts on the same model share one loaded copy of it. The ONNX and llama.cpp hosts each keep one model loaded, and one agent serves every contact, so when Marcus and Elena both use Phi-3.5 mini it is loaded once and they take turns on it. Only the weights are shared: each reply is generated from its own prompt, and the cache of a previous prompt is reused only for the exact tokens the new prompt has in common with it, so nothing of one contact's conversation carries into another's. A reply canceled partway leaves the model ready for the next one. This is checked against the real model when it is installed (`OnnxAgent_ContactsOnTheSameModelShareOneLoadedCopy` counts the loads).

Limitations of that design:

- When two contacts alternate on one model, their prompts differ from the start, so each reply reads its whole prompt again rather than continuing a cached one: a few seconds more per message on a CPU. Keeping a separate cache per contact (a context or sequence each) would avoid that at the cost of that much more memory per contact.
- Contacts on different models take turns too, and only one model is kept loaded at a time across both runtimes, so each change of speaker between different models unloads one and loads the other. Memory stays bounded at one model; the price is a load whenever the model changes. Groups whose contacts share a model are much faster.
- Learning memories in a group asks each contact's own model, so it costs one model call per contact when it runs.

## Design notes

- **One conversation model.** A group is a conversation of another kind, not a separate system. Its messages are stored with every other message, each contact's message recording its sender (`ChatMessage.SenderContactId`); a group lists its contacts in `ConversationParticipant`, each with their own memory checkpoint. A direct conversation keeps its contact on the conversation itself, so each contact still has exactly one.
- **Migration.** Schema version 8 makes a conversation's contact optional, which SQLite allows only by copying the table: it copies every row and column under the same IDs, with foreign keys off for the copy and checked before committing, so messages and memories keep their conversations. Existing contact replies are attributed to their conversation's contact. It runs in one transaction, like every migration.
- **Deterministic rules, model-assisted where cheap.** Membership, turn limits, cooldowns, direct address, cancellation, and the order of model use are plain rules. The one soft decision, whether a contact adds something unasked, uses the contact's own model only when it is already loaded, as a yes-or-no answer that is validated, at most once a round, and never recursively; anything else falls back to rules and chance. No coordinator model is needed or downloaded.
- **The messenger drives the rounds.** Business decides each turn and writes each message; the messenger asks for turns one at a time while the group is open and stops when it is not. That keeps "only while open" a property of what is on screen, with nothing running in the background.

## Selfies

The camera button asks the group for a selfie. A selfie shows one contact, chosen among those with a profile photo by the coordinator's rules: whoever your latest message spoke to by name, then one it named, then a contact who just offered a picture, then whoever wrote most recently. That contact plans it with their own model and sends it as their message; the others can react to it, reading it as a description. Asking for a selfie is not a round: it neither starts nor stops the group's turn-taking, and arrives even if you leave the group meanwhile. See [Selfies](image_generation.md#selfies).

## Not included

- Groups of more than two contacts, adding or removing members, and renaming a group (the title is stored, but there is no way to set it yet).
- Contacts messaging you on their own outside a group, such as following up on something you told them or unread messages waiting when the app starts. Contacts' identities, memories, and replies are already independent of any one conversation, so that could reuse them later.
- A per-contact cache on a shared model, and keeping more than one model loaded at once.
