using HomiliaMessenger.Business.Modules.Contacts;
using HomiliaMessenger.Business.Modules.Conversations;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Business.Modules.Memories;

/// <summary>
/// Persistence capability required by the memories module, including the vectors memories are searched by. Every query
/// is limited to one contact's memories.
/// </summary>
public interface IMemoryStore
{
    /// <summary>
    /// Lists a contact's memories, oldest first.
    /// </summary>
    Task<IReadOnlyList<Memory>> ListAsync(ContactId contactId, CancellationToken cancellationToken = default);

    Task<Memory?> GetAsync(MemoryId id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a memory to a conversation, owned by the conversation's contact (whatever <see cref="Memory.ContactId"/>
    /// says). Returns <see langword="false"/> when the conversation does not exist.
    /// </summary>
    Task<bool> AddAsync(Memory memory, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds the memories listed from a batch of messages and moves the conversation's memory checkpoint to
    /// <paramref name="through"/>, together, only if the checkpoint is still <paramref name="expectedThrough"/>. Returns
    /// <see langword="false"/>, saving nothing, when it moved meanwhile or the conversation does not exist.
    /// </summary>
    Task<bool> AddExtractedAsync(ConversationId conversationId, IReadOnlyList<Memory> memories, MessageId through, MessageId? expectedThrough, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces a memory's text, only if it is still <paramref name="expectedText"/>, and discards its vector, which
    /// described the old text. Returns <see langword="false"/> when it changed meanwhile or no longer exists.
    /// </summary>
    Task<bool> UpdateAsync(MemoryId id, string text, MemoryOrigin origin, DateTimeOffset updatedAtUtc, string expectedText, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(MemoryId id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Memories with no vector from <paramref name="model"/>, oldest first: one contact's, or every contact's when
    /// <paramref name="contactId"/> is <see langword="null"/>.
    /// </summary>
    Task<IReadOnlyList<Memory>> ListUnembeddedAsync(ContactId? contactId, string model, int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves a memory's vector, replacing any other, only if its text is still <paramref name="embeddedText"/>. Returns
    /// <see langword="false"/> when the text changed meanwhile or the memory no longer exists.
    /// </summary>
    Task<bool> SaveEmbeddingAsync(MemoryId id, string embeddedText, string model, ReadOnlyMemory<float> vector, CancellationToken cancellationToken = default);

    /// <summary>
    /// A contact's memories most similar to <paramref name="query"/>, most similar first, compared only with vectors from
    /// <paramref name="model"/> of the same length. Stored vectors that are malformed are skipped.
    /// </summary>
    Task<IReadOnlyList<ScoredMemory>> FindSimilarAsync(ContactId contactId, string model, ReadOnlyMemory<float> query, int count, CancellationToken cancellationToken = default);
}
