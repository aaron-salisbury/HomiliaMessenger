using HomiliaMessenger.Business.Modules.Contacts;
using HomiliaMessenger.Business.Modules.Conversations;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Tests.Conversations;

/// <summary>
/// The real conversation service, except that loading messages can be held until released, and saving a group's
/// setting can be made to fail, so tests can act at exactly those moments.
/// </summary>
internal sealed class InterruptibleConversations(IConversationService inner) : IConversationService
{
    /// <summary>
    /// While set, loading messages waits for it to complete.
    /// </summary>
    public TaskCompletionSource? HoldListMessages { get; set; }

    public bool FailSetInitiation { get; set; }

    public async Task<IReadOnlyList<ChatMessage>> ListMessagesAsync(ConversationId conversationId, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ChatMessage> messages = await inner.ListMessagesAsync(conversationId, cancellationToken);

        if (HoldListMessages is { } hold)
        {
            await hold.Task.WaitAsync(cancellationToken);
        }

        return messages;
    }

    public Task<bool> SetInitiationAsync(ConversationId conversationId, GroupInitiation initiation, CancellationToken cancellationToken = default)
    {
        return FailSetInitiation ? Task.FromException<bool>(new IOException("The database is locked.")) : inner.SetInitiationAsync(conversationId, initiation, cancellationToken);
    }

    public Task<IReadOnlyList<Conversation>> ListAsync(CancellationToken cancellationToken = default) => inner.ListAsync(cancellationToken);
    public Task<Conversation?> GetAsync(ConversationId conversationId, CancellationToken cancellationToken = default) => inner.GetAsync(conversationId, cancellationToken);
    public Task<Conversation?> FindByContactAsync(ContactId contactId, CancellationToken cancellationToken = default) => inner.FindByContactAsync(contactId, cancellationToken);
    public Task<GroupCreationResult> CreateGroupAsync(IReadOnlyList<ContactId> contactIds, CancellationToken cancellationToken = default) => inner.CreateGroupAsync(contactIds, cancellationToken);
    public Task<SendMessageResult> SendAsync(ConversationId conversationId, string text, CancellationToken cancellationToken = default) => inner.SendAsync(conversationId, text, cancellationToken);
    public Task<GroupTurn?> PlanGroupTurnAsync(ConversationId conversationId, GroupCycle cycle, CancellationToken cancellationToken = default) => inner.PlanGroupTurnAsync(conversationId, cycle, cancellationToken);
    public Task<ConversationReply> ReplyInGroupAsync(ConversationId conversationId, GroupTurn turn, IProgress<ReplyActivity>? activity = null, CancellationToken cancellationToken = default) => inner.ReplyInGroupAsync(conversationId, turn, activity, cancellationToken);
    public Task<SendMessageResult> SendAsync(ContactId contactId, string text, CancellationToken cancellationToken = default) => inner.SendAsync(contactId, text, cancellationToken);
    public Task<ConversationReply> ReplyAsync(ConversationId conversationId, IProgress<ReplyActivity>? activity = null, CancellationToken cancellationToken = default) => inner.ReplyAsync(conversationId, activity, cancellationToken);
    public Task<ConversationReply> ContinueAsync(ConversationId conversationId, IProgress<ReplyActivity>? activity = null, CancellationToken cancellationToken = default) => inner.ContinueAsync(conversationId, activity, cancellationToken);
    public Task<Conversation?> UpdateMemoryAsync(ConversationId conversationId, CancellationToken cancellationToken = default) => inner.UpdateMemoryAsync(conversationId, cancellationToken);
    public Task<bool> DeleteAsync(ConversationId conversationId, CancellationToken cancellationToken = default) => inner.DeleteAsync(conversationId, cancellationToken);
}
