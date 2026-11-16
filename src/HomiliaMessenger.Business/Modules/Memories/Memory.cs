using HomiliaMessenger.Business.Modules.Contacts;
using HomiliaMessenger.Business.Modules.Conversations;
using System;

namespace HomiliaMessenger.Business.Modules.Memories;

public readonly record struct MemoryId(Guid Value)
{
    public static MemoryId New()
    {
        return new(Guid.NewGuid());
    }
}

/// <summary>
/// How a memory came to be.
/// </summary>
public enum MemoryOrigin
{
    /// <summary>
    /// Listed by the contact's agent from the user's messages, in the background.
    /// </summary>
    Extracted = 0,

    /// <summary>
    /// Written, or corrected, by the user.
    /// </summary>
    Manual = 1,

    /// <summary>
    /// A line of the memory notes kept before memories were individual records. The user could edit those notes, so
    /// imported lines are treated as the user's own.
    /// </summary>
    Imported = 2
}

/// <summary>
/// One lasting thing a contact remembers about the user, such as "Aaron's dog is Biscuit." A memory belongs to exactly
/// one contact, and is learned within that contact's conversation; it is removed with either.
/// </summary>
public sealed record Memory
{
    public const int MAX_TEXT_LENGTH = 300;

    public MemoryId Id { get; init; }
    public ContactId ContactId { get; init; }
    public ConversationId ConversationId { get; init; }
    public string Text { get; init; } = string.Empty;
    public MemoryOrigin Origin { get; init; }

    /// <summary>
    /// For an extracted memory, the last message of the batch it was listed from; <see langword="null"/> otherwise.
    /// </summary>
    public MessageId? SourceMessageId { get; init; }

    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }

    /// <summary>
    /// The embedding model the memory's stored vector came from, or <see langword="null"/> while it has none. A memory
    /// whose vector came from a model other than the one in use is waiting to be embedded again.
    /// </summary>
    public string? EmbeddingModel { get; init; }

    /// <summary>
    /// The length of the memory's stored vector, or <see langword="null"/> while it has none.
    /// </summary>
    public int? EmbeddingDimensions { get; init; }
}

/// <summary>
/// A memory found by similarity to a query, with its cosine similarity and the vector it was compared by.
/// </summary>
public sealed record ScoredMemory(Memory Memory, float Score, float[] Vector);
