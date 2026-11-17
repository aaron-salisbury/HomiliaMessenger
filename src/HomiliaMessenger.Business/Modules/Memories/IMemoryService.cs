using FluentValidation.Results;
using HomiliaMessenger.Business.Modules.Contacts;
using HomiliaMessenger.Business.Modules.Conversations;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Business.Modules.Memories;

/// <summary>
/// The outcome of adding or correcting a memory. When <see cref="Validation"/> is valid, <see cref="Memory"/> is what was
/// saved.
/// </summary>
public sealed record MemorySaveResult(ValidationResult Validation, Memory? Memory);

/// <summary>
/// Reading and correcting what contacts remember. Memories are learned in the background as a chat grows (see
/// <see cref="IConversationService.UpdateMemoryAsync"/>); the user can add, correct, and delete them here.
/// </summary>
public interface IMemoryService
{
    /// <summary>
    /// Lists a contact's memories, oldest first.
    /// </summary>
    Task<IReadOnlyList<Memory>> ListAsync(ContactId contactId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a memory the user wrote to the contact of <paramref name="conversationId"/>. Fails without saving when the
    /// text is blank, too long, or already remembered.
    /// </summary>
    Task<MemorySaveResult> AddAsync(ConversationId conversationId, string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a memory the user wrote for <paramref name=contactId/>, as learned in <paramref name=conversationId/>,
    /// which the contact must take part in, as a group's contacts do. Fails without saving as the other overload does.
    /// </summary>
    Task<MemorySaveResult> AddAsync(ConversationId conversationId, ContactId contactId, string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves the user's correction to a memory, which makes it the user's own. Fails without saving when the text is
    /// blank or too long, or when the memory was changed after <paramref name="expectedText"/> was read.
    /// </summary>
    Task<MemorySaveResult> UpdateAsync(MemoryId id, string text, string expectedText, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(MemoryId id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Embeds the memories that have no vector from the embedding model in use: one contact's, or every contact's when
    /// <paramref name="contactId"/> is <see langword="null"/>. Meant to run in the background; memories that cannot be
    /// embedded now are tried again the next time.
    /// </summary>
    /// <returns>How many memories were embedded.</returns>
    Task<int> EmbedPendingAsync(ContactId? contactId, CancellationToken cancellationToken = default);
}
