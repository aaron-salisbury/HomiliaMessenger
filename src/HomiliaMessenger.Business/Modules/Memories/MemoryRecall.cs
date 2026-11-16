using HomiliaMessenger.Business.Modules.Agents;
using HomiliaMessenger.Business.Modules.Contacts;
using HomiliaMessenger.Business.Modules.Conversations;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Business.Modules.Memories;

/// <summary>
/// How many memories an agent is given, and how alike a memory must be to be recalled. The composition root may register
/// different values.
/// </summary>
public sealed record MemoryRecallOptions
{
    /// <summary>
    /// The most memory text given with one request, about what the old memory notes allowed.
    /// </summary>
    public int MaxCharacters { get; init; } = 1000;

    /// <summary>
    /// Of <see cref="MaxCharacters"/>, how much may go to the newest memories even when nothing makes them relevant, as
    /// the old notes always ended with the newest facts.
    /// </summary>
    public int RecentCharacters { get; init; } = 300;

    /// <summary>
    /// The most similar memories considered for one request.
    /// </summary>
    public int MaxCandidates { get; init; } = 12;

    /// <summary>
    /// The cosine similarity below which a memory is not considered relevant to the query, for embedding providers that
    /// do not say what it is for their model.
    /// </summary>
    public float MinSimilarity { get; init; } = 0.5f;

    /// <summary>
    /// The cosine similarity at or above which two memories are taken to say the same thing, so only one is given. Only
    /// what is given is affected; both memories are kept.
    /// </summary>
    public float NearDuplicateSimilarity { get; init; } = 0.95f;

    /// <summary>
    /// The most text searched with: the end of the user's latest messages.
    /// </summary>
    public int MaxQueryCharacters { get; init; } = 1000;
}

/// <summary>
/// Chooses which of a contact's memories an agent is given. When they all fit within the budget they are all given, in
/// the order they were learned, so the prompt stays the same between replies and local models can reuse their cache.
/// Otherwise the memories most similar to the user's latest messages are given, then the newest, within the budget.
/// </summary>
/// <remarks>
/// <para>
/// Similarity is by embedding vector when an embedding model is ready. Memories without a vector from that model (not
/// embedded yet, or embedded by a model no longer chosen), and every memory when no model is ready or embedding fails,
/// are matched by the words they share with the query instead. Recall never fails a reply.
/// </para>
/// <para>
/// Retrieval is a single embedding of the query and an exact comparison with the contact's stored vectors; no model is
/// asked to choose. Memories are only learned from messages before the conversation's memory checkpoint, which the agent
/// is no longer given, so recalled memories never repeat what the agent already has.
/// </para>
/// </remarks>
internal sealed class MemoryRecall
{
    private static readonly HashSet<string> STOP_WORDS = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "are", "but", "not", "you", "your", "with", "have", "has", "had", "was", "were", "this",
        "that", "they", "them", "their", "there", "what", "when", "where", "who", "why", "how", "from", "about", "into",
        "just", "like", "will", "would", "could", "should", "can", "did", "does", "doing", "been", "being", "its", "our",
        "out", "all", "any", "some", "too", "very", "yes", "yeah", "okay", "really", "know", "think", "got", "get", "his",
        "her", "him", "she", "today", "also", "than", "then", "one", "only", "tell", "more", "else", "again", "sure",
        "thanks", "thank", "please", "well", "said", "say"
    };

    // A latest message with fewer words than this to search by, such as "Tell me more", is searched together with the
    // user's messages before it, so a follow-up still finds what it follows up on.
    private const int MIN_QUERY_WORDS = 3;

    private readonly IMemoryStore _store;
    private readonly ITextEmbedder _embedder;
    private readonly MemoryRecallOptions _options;

    public MemoryRecall(IMemoryStore store, ITextEmbedder embedder, MemoryRecallOptions options)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    /// The contact's memories to give with a request searched by <paramref name="query"/>, in the order they were learned.
    /// </summary>
    internal async Task<IReadOnlyList<Memory>> RecallAsync(ContactId contactId, string query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        IReadOnlyList<Memory> memories = await _store.ListAsync(contactId, cancellationToken);

        if (memories.Count == 0)
        {
            return [];
        }

        if (memories.Sum(memory => (long)memory.Text.Length + 1) <= _options.MaxCharacters)
        {
            return memories;
        }

        (string? model, IReadOnlyList<ScoredMemory> similar, float? minSimilarity) = await FindSimilarAsync(contactId, query, cancellationToken);
        MemoryRecallOptions options = minSimilarity is { } threshold ? _options with { MinSimilarity = threshold } : _options;

        return Select(memories, similar, model, query, options);
    }

    /// <summary>
    /// The text to search a contact's memories with for a reply: the user's latest messages, written one after another,
    /// even when the contact has replied to them since (as when the user asks for another reply). When those say too
    /// little to search by, as a follow-up like "Tell me more" does, the user's messages before them are included.
    /// </summary>
    internal static string QueryFor(IReadOnlyList<ChatMessage> messages, int maxCharacters)
    {
        ArgumentNullException.ThrowIfNull(messages);

        List<string> runs = [];
        int end = messages.Count;
        int words = 0;

        while (end > 0 && words < MIN_QUERY_WORDS)
        {
            while (end > 0 && messages[end - 1].Sender != MessageSender.User)
            {
                end--;
            }

            int start = end;

            while (start > 0 && messages[start - 1].Sender == MessageSender.User)
            {
                start--;
            }

            if (start == end)
            {
                break;
            }

            string run = string.Join("\n", messages.Skip(start).Take(end - start).Select(message => message.Text));
            runs.Insert(0, run);
            words += Words(run).Count;
            end = start;
        }

        return Last(string.Join("\n", runs), maxCharacters);
    }

    /// <summary>
    /// The text to search with when listing the facts in <paramref name="batch"/>: what the user wrote in it.
    /// </summary>
    internal static string QueryForBatch(IEnumerable<ChatMessage> batch, int maxCharacters)
    {
        ArgumentNullException.ThrowIfNull(batch);

        return Last(string.Join("\n", batch.Where(message => message.Sender == MessageSender.User).Select(message => message.Text)), maxCharacters);
    }

    /// <summary>
    /// Chooses memories within the budget: those similar to the query by vector, then those sharing its words, then the
    /// newest, skipping any that repeat one already chosen. Returned in the order they were learned.
    /// </summary>
    /// <param name="model">The model <paramref name="similar"/> was compared with, or <see langword="null"/> without one.</param>
    internal static IReadOnlyList<Memory> Select(IReadOnlyList<Memory> memories, IReadOnlyList<ScoredMemory> similar, string? model, string query, MemoryRecallOptions options)
    {
        ArgumentNullException.ThrowIfNull(memories);
        ArgumentNullException.ThrowIfNull(similar);
        ArgumentNullException.ThrowIfNull(options);

        Dictionary<MemoryId, Memory> byId = memories.ToDictionary(memory => memory.Id);
        List<(Memory Memory, float[]? Vector)> ranked = [];

        foreach (ScoredMemory candidate in similar.Where(candidate => candidate.Score >= options.MinSimilarity).OrderByDescending(candidate => candidate.Score).Take(options.MaxCandidates))
        {
            if (byId.TryGetValue(candidate.Memory.Id, out Memory? memory))
            {
                ranked.Add((memory, candidate.Vector));
            }
        }

        // Memories the vectors could not speak for are matched by shared words.
        HashSet<string> queryWords = Words(query);
        IEnumerable<(Memory Memory, int Score)> matches = memories
            .Where(memory => model is null || memory.EmbeddingModel != model)
            .Select(memory => (Memory: memory, Score: Words(memory.Text).Count(queryWords.Contains)))
            .Where(match => match.Score > 0)
            .OrderByDescending(match => match.Score)
            .ThenByDescending(match => match.Memory.CreatedAtUtc)
            .Take(options.MaxCandidates);

        foreach ((Memory memory, int _) in matches)
        {
            ranked.Add((memory, null));
        }

        List<(Memory Memory, float[]? Vector)> chosen = [];
        long used = 0;

        void Choose(Memory memory, float[]? vector, long budget)
        {
            long size = memory.Text.Length + 1;

            if (used + size > budget || chosen.Any(existing => existing.Memory.Id == memory.Id || Repeats(existing, memory, vector, options)))
            {
                return;
            }

            chosen.Add((memory, vector));
            used += size;
        }

        foreach ((Memory memory, float[]? vector) in ranked)
        {
            Choose(memory, vector, options.MaxCharacters);
        }

        long recentBudget = Math.Min(options.MaxCharacters, used + options.RecentCharacters);

        foreach (Memory memory in memories.Reverse())
        {
            Choose(memory, null, recentBudget);
        }

        return [.. chosen.Select(entry => entry.Memory).OrderBy(memory => memory.CreatedAtUtc).ThenBy(memory => IndexOf(memories, memory))];
    }

    private async Task<(string? Model, IReadOnlyList<ScoredMemory> Similar, float? MinSimilarity)> FindSimilarAsync(ContactId contactId, string query, CancellationToken cancellationToken)
    {
        if (_embedder.Model is null || string.IsNullOrWhiteSpace(query))
        {
            return (null, [], null);
        }

        try
        {
            EmbeddingResult result = await _embedder.EmbedAsync([Last(query, _options.MaxQueryCharacters)], EmbeddingPurpose.Query, cancellationToken);

            if (!EmbeddingVectors.AreUsable(result, 1))
            {
                return (null, [], null);
            }

            IReadOnlyList<ScoredMemory> similar = await _store.FindSimilarAsync(contactId, result.Model!, result.Vectors[0], _options.MaxCandidates, cancellationToken);

            return (result.Model, similar, result.MinSimilarity);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // A provider failure must not stop the contact replying; memories are then matched by their words.
            return (null, [], null);
        }
    }

    private static bool Repeats((Memory Memory, float[]? Vector) existing, Memory memory, float[]? vector, MemoryRecallOptions options)
    {
        if (MemoryNotes.IsSameFact(existing.Memory.Text, memory.Text))
        {
            return true;
        }

        return existing.Vector is not null && vector is not null && existing.Vector.Length == vector.Length
            && VectorSimilarity.Cosine(existing.Vector, vector) >= options.NearDuplicateSimilarity;
    }

    private static int IndexOf(IReadOnlyList<Memory> memories, Memory memory)
    {
        for (int index = 0; index < memories.Count; index++)
        {
            if (memories[index].Id == memory.Id)
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// The distinct words of three or more letters or digits, in lower case, other than the most common ones.
    /// </summary>
    internal static HashSet<string> Words(string text)
    {
        HashSet<string> words = new(StringComparer.Ordinal);
        StringBuilder word = new();

        void Flush()
        {
            if (word.Length >= 3)
            {
                string candidate = word.ToString();

                if (!STOP_WORDS.Contains(candidate))
                {
                    words.Add(candidate);
                }
            }

            word.Clear();
        }

        foreach (char character in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                word.Append(character);
            }
            else
            {
                Flush();
            }
        }

        Flush();

        return words;
    }

    private static string Last(string text, int maxCharacters)
    {
        string trimmed = text.Trim();

        return trimmed.Length <= maxCharacters ? trimmed : trimmed[^maxCharacters..];
    }
}
