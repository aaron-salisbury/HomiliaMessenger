using FluentValidation;
using FluentValidation.Results;
using HomiliaMessenger.Business.Modules.Agents;
using HomiliaMessenger.Business.Modules.Contacts;
using HomiliaMessenger.Business.Modules.Conversations;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Business.Modules.Memories;

internal sealed class MemoryService : IMemoryService
{
    /// <summary>
    /// How many memories are embedded in one request to the embedding model.
    /// </summary>
    internal const int EMBED_BATCH_SIZE = 16;

    private readonly IMemoryStore _store;
    private readonly IConversationStore _conversations;
    private readonly ITextEmbedder _embedder;
    private readonly IValidator<Memory> _validator;
    private readonly TimeProvider _timeProvider;

    public MemoryService(IMemoryStore store, IConversationStore conversations, ITextEmbedder embedder, IValidator<Memory> validator)
        : this(store, conversations, embedder, validator, TimeProvider.System)
    {
    }

    internal MemoryService(IMemoryStore store, IConversationStore conversations, ITextEmbedder embedder, IValidator<Memory> validator, TimeProvider timeProvider)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public Task<IReadOnlyList<Memory>> ListAsync(ContactId contactId, CancellationToken cancellationToken = default)
    {
        return _store.ListAsync(contactId, cancellationToken);
    }

    public async Task<MemorySaveResult> AddAsync(ConversationId conversationId, string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);

        Conversation? conversation = await _conversations.GetAsync(conversationId, cancellationToken);

        if (conversation is null)
        {
            return Failed("Conversation", "The conversation no longer exists.");
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        Memory memory = new()
        {
            Id = MemoryId.New(),
            ContactId = conversation.ContactId,
            ConversationId = conversationId,
            Text = Clean(text),
            Origin = MemoryOrigin.Manual,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        ValidationResult result = await _validator.ValidateAsync(memory, cancellationToken);

        if (!result.IsValid)
        {
            return new MemorySaveResult(result, null);
        }

        IReadOnlyList<Memory> existing = await _store.ListAsync(conversation.ContactId, cancellationToken);

        if (existing.Any(known => MemoryNotes.IsSameFact(known.Text, memory.Text)))
        {
            return Failed("Memory", "That is already remembered.");
        }

        return await _store.AddAsync(memory, cancellationToken)
            ? new MemorySaveResult(result, memory)
            : Failed("Conversation", "The conversation no longer exists.");
    }

    public async Task<MemorySaveResult> UpdateAsync(MemoryId id, string text, string expectedText, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(expectedText);

        Memory? memory = await _store.GetAsync(id, cancellationToken);

        if (memory is null)
        {
            return Failed("Memory", "The memory no longer exists.");
        }

        Memory corrected = memory with
        {
            Text = Clean(text),
            Origin = MemoryOrigin.Manual,
            UpdatedAtUtc = _timeProvider.GetUtcNow(),
            EmbeddingModel = null,
            EmbeddingDimensions = null
        };

        ValidationResult result = await _validator.ValidateAsync(corrected, cancellationToken);

        if (!result.IsValid)
        {
            return new MemorySaveResult(result, null);
        }

        if (!string.Equals(memory.Text, expectedText, StringComparison.Ordinal)
            || !await _store.UpdateAsync(id, corrected.Text, corrected.Origin, corrected.UpdatedAtUtc, expectedText, cancellationToken))
        {
            return Failed("Memory", "The memory was changed while you were editing it. Open the memories again to see the latest.");
        }

        return new MemorySaveResult(result, corrected);
    }

    public Task<bool> DeleteAsync(MemoryId id, CancellationToken cancellationToken = default)
    {
        return _store.DeleteAsync(id, cancellationToken);
    }

    public async Task<int> EmbedPendingAsync(ContactId? contactId, CancellationToken cancellationToken = default)
    {
        int embedded = 0;

        while (_embedder.Model is { } model)
        {
            IReadOnlyList<Memory> pending = await _store.ListUnembeddedAsync(contactId, model, EMBED_BATCH_SIZE, cancellationToken);

            if (pending.Count == 0)
            {
                break;
            }

            EmbeddingResult result = await _embedder.EmbedAsync([.. pending.Select(memory => memory.Text)], EmbeddingPurpose.Document, cancellationToken);

            // Never store vectors from a model other than the one asked about, or that cannot be compared.
            if (!EmbeddingVectors.AreUsable(result, pending.Count) || result.Model != model)
            {
                break;
            }

            int saved = 0;

            for (int index = 0; index < pending.Count; index++)
            {
                // A memory corrected or deleted meanwhile is skipped; a correction is embedded on a later pass.
                if (await _store.SaveEmbeddingAsync(pending[index].Id, pending[index].Text, model, result.Vectors[index], cancellationToken))
                {
                    saved++;
                }
            }

            embedded += saved;

            if (saved == 0 || pending.Count < EMBED_BATCH_SIZE)
            {
                break;
            }
        }

        return embedded;
    }

    /// <summary>
    /// A memory is one line: surrounding space and a leading list marker are removed, and line breaks become spaces.
    /// </summary>
    internal static string Clean(string text)
    {
        string line = text.ReplaceLineEndings(" ").Trim();

        if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal) || line.StartsWith("• ", StringComparison.Ordinal))
        {
            line = line[2..].Trim();
        }

        return line;
    }

    private static MemorySaveResult Failed(string property, string message)
    {
        return new MemorySaveResult(new ValidationResult([new ValidationFailure(property, message)]), null);
    }
}
