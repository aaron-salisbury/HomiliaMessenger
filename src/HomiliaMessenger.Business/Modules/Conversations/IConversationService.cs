using FluentValidation.Results;
using HomiliaMessenger.Business.Modules.Agents;
using HomiliaMessenger.Business.Modules.Contacts;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Business.Modules.Conversations;

/// <summary>
/// The outcome of sending a message. When <see cref="Validation"/> is valid, <see cref="Conversation"/> and
/// <see cref="Message"/> describe what was saved.
/// </summary>
public sealed record SendMessageResult(ValidationResult Validation, Conversation? Conversation, ChatMessage? Message);

/// <summary>
/// The outcome of asking a contact to reply. <see cref="Message"/> is the saved reply on success; <see cref="Problem"/>
/// explains any other status.
/// </summary>
public sealed record ConversationReply(ReplyStatus Status, ChatMessage? Message, string? Problem);

/// <summary>
/// The outcome of creating a group. When <see cref="Validation"/> is valid, <see cref="Group"/> is the group, which
/// <see cref="IsExisting"/> says the same contacts already had.
/// </summary>
public sealed record GroupCreationResult(ValidationResult Validation, Conversation? Group, bool IsExisting);

/// <summary>
/// What a contact is doing on the way to a reply, for showing in the conversation.
/// </summary>
public enum ReplyActivity
{
    /// <summary>
    /// Looking something up online.
    /// </summary>
    SearchingWeb,

    /// <summary>
    /// Writing the reply.
    /// </summary>
    Writing
}

public interface IConversationService
{
    /// <summary>
    /// Lists conversations with their most recent message, most recently active first.
    /// </summary>
    Task<IReadOnlyList<Conversation>> ListAsync(CancellationToken cancellationToken = default);

    Task<Conversation?> GetAsync(ConversationId conversationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The contact's direct conversation, if they have one.
    /// </summary>
    Task<Conversation?> FindByContactAsync(ContactId contactId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts a group with the user and <paramref name="contactIds"/> (between <see cref="GroupConversations.MIN_CONTACTS"/>
    /// and <see cref="GroupConversations.MAX_CONTACTS"/> contacts). The contacts' direct conversations are left as they
    /// are. When the same contacts already have a group, that group is returned instead of starting another.
    /// </summary>
    Task<GroupCreationResult> CreateGroupAsync(IReadOnlyList<ContactId> contactIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates and saves a message from the user to an existing conversation, direct or group.
    /// </summary>
    Task<SendMessageResult> SendAsync(ConversationId conversationId, string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// Decides who in a group writes next in <paramref name="cycle"/>, or <see langword="null"/> when the group should
    /// now wait for the user. A round opened with <see cref="GroupTrigger.Opened"/> uses up the group's chance to start a
    /// conversation, whether or not a contact takes it. Call it only while the user has the group open.
    /// </summary>
    Task<GroupTurn?> PlanGroupTurnAsync(ConversationId conversationId, GroupCycle cycle, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks <paramref name="turn"/>'s contact to write their next message in the group, with their own persona, agent,
    /// and memories, and saves it as theirs.
    /// </summary>
    Task<ConversationReply> ReplyInGroupAsync(ConversationId conversationId, GroupTurn turn, IProgress<ReplyActivity>? activity = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets whether a group's contacts may start conversations when it is opened.
    /// </summary>
    Task<bool> SetInitiationAsync(ConversationId conversationId, GroupInitiation initiation, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists a conversation's messages, oldest first.
    /// </summary>
    Task<IReadOnlyList<ChatMessage>> ListMessagesAsync(ConversationId conversationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates and saves a message from the user to a contact, starting the contact's conversation if it has none. An
    /// invalid message is not saved, and no conversation is started for it.
    /// </summary>
    Task<SendMessageResult> SendAsync(ContactId contactId, string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks the contact's agent to reply to the conversation so far, in character, and saves the reply. Contacts without
    /// an assigned agent reply with simulated canned text. The status is <see cref="ReplyStatus.NotFound"/> when the
    /// conversation no longer exists, for example because it was deleted while the contact was replying. When web search
    /// is on and the latest message calls for it, the contact first looks it up; a failed search never stops the reply.
    /// </summary>
    /// <param name="activity">Told when the contact starts searching the web, and when it starts writing.</param>
    Task<ConversationReply> ReplyAsync(ConversationId conversationId, IProgress<ReplyActivity>? activity = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Learns the lasting facts in older messages as the contact's memories, once there are too many messages to give an
    /// agent as they are, and embeds memories that are waiting for a vector. Meant to run after a reply, in the
    /// background. Contacts with simulated replies learn nothing.
    /// </summary>
    /// <returns>The conversation with its moved memory checkpoint, or <see langword="null"/> when it did not move.</returns>
    Task<Conversation?> UpdateMemoryAsync(ConversationId conversationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the conversation, its messages, and what the contact learned in it.
    /// </summary>
    Task<bool> DeleteAsync(ConversationId conversationId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Persistence capability required by the conversations module.
/// </summary>
public interface IConversationStore
{
    Task<IReadOnlyList<Conversation>> ListAsync(CancellationToken cancellationToken = default);
    Task<Conversation?> GetAsync(ConversationId id, CancellationToken cancellationToken = default);
    Task<Conversation?> FindByContactAsync(ContactId contactId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the contact's conversation, inserting <paramref name="conversation"/> when the contact has none yet.
    /// </summary>
    Task<Conversation> GetOrAddAsync(Conversation conversation, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ChatMessage>> ListMessagesAsync(ConversationId conversationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a message to its conversation. Returns <see langword="false"/> when the conversation does not exist, when
    /// the message's sender is a contact who does not take part in it, or, with <paramref name="onlyIfFirst"/>, when the
    /// conversation already has a message.
    /// </summary>
    /// <param name="onlyIfFirst">Adds the message only as the conversation's first, checked and added in one step.</param>
    Task<bool> AddMessageAsync(ChatMessage message, CancellationToken cancellationToken = default, bool onlyIfFirst = false);

    Task<bool> DeleteAsync(ConversationId id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves a new group with its participants, together, and returns it; or, when the same contacts already have a
    /// group (even one saved a moment before), saves nothing and returns that group. Returns <see langword="null"/> when
    /// any of its contacts no longer exists.
    /// </summary>
    Task<Conversation?> AddGroupAsync(Conversation group, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets whether a group's contacts may start conversations. Returns <see langword="false"/> when it is not a group.
    /// </summary>
    Task<bool> SetInitiationAsync(ConversationId id, GroupInitiation initiation, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records that the group had its chance to start a conversation at <paramref name="at"/>, only if that was last
    /// recorded as <paramref name="expected"/>, so two openings at once cannot both take it.
    /// </summary>
    Task<bool> ClaimInitiationAsync(ConversationId id, DateTimeOffset at, DateTimeOffset? expected, CancellationToken cancellationToken = default);
}
