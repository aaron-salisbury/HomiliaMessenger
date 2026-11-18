using HomiliaMessenger.Business.Modules.Agents;
using HomiliaMessenger.Business.Modules.Contacts;
using HomiliaMessenger.Business.Modules.Conversations;
using HomiliaMessenger.Business.Modules.ImageGeneration;
using HomiliaMessenger.Business.Modules.Profile;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Business.Modules.Selfies;

/// <summary>
/// Whether a chat's contacts can be asked for a selfie, and if not, why.
/// </summary>
/// <param name="NeedsModel">Whether downloading a selfie model in Settings would help.</param>
public sealed record SelfieAvailability(bool IsReady, bool NeedsModel, string? Problem);

public enum SelfieStage
{
    /// <summary>
    /// The contact is deciding what the selfie shows, from the conversation.
    /// </summary>
    Planning,

    /// <summary>
    /// The selfie is being drawn; <see cref="SelfieProgress.Image"/> says how far.
    /// </summary>
    Drawing
}

/// <param name="Subject">The contact the selfie is of, which is known from the first report.</param>
public sealed record SelfieProgress(ContactId Subject, SelfieStage Stage, ImageGenerationProgress? Image = null);

public enum SelfieStatus
{
    Success,

    /// <summary>
    /// No selfie model is ready, or no one in the chat has a photo.
    /// </summary>
    Unavailable,

    /// <summary>
    /// Another image is being generated; one is made at a time.
    /// </summary>
    Busy,

    /// <summary>
    /// The conversation no longer exists.
    /// </summary>
    NotFound,
    Canceled,
    Failed
}

/// <summary>
/// The outcome of asking for a selfie. <see cref="Message"/> is the saved selfie on success; <see cref="Problem"/> explains
/// any other status.
/// </summary>
public sealed record SelfieResult(SelfieStatus Status, ChatMessage? Message, string? Problem)
{
    internal static SelfieResult Unavailable(string problem)
    {
        return new(SelfieStatus.Unavailable, null, problem);
    }
}

/// <summary>
/// Lets the user ask the contacts in a chat for a selfie, which arrives in the chat like a message.
/// </summary>
public interface ISelfieService
{
    /// <summary>
    /// Whether <paramref name="contacts"/>, a chat's contacts, can be asked for a selfie: a selfie model is ready, and at
    /// least one of them has a photo for the selfie to look like.
    /// </summary>
    SelfieAvailability GetAvailability(IReadOnlyList<Contact> contacts);

    /// <summary>
    /// Asks the conversation for a selfie. In a group, the contacts with photos are weighed by what is going on (see
    /// <see cref="GroupCoordinator.RankSelfieSubjects"/>), and the selfie is of as many of the likeliest as the selfie model
    /// can draw. The contact in it plans the scene with their own model from the conversation, then it is drawn and saved
    /// as their message, with what they say with it. The conversation can go on meanwhile; the selfie is saved when it is
    /// ready, after whatever was written in the meantime.
    /// </summary>
    Task<SelfieResult> TakeAsync(ConversationId conversationId, IProgress<SelfieProgress>? progress = null, CancellationToken cancellationToken = default);
}

internal sealed class SelfieService : ISelfieService
{
    private readonly IConversationStore _store;
    private readonly IContactService _contacts;
    private readonly IUserProfileService _profile;
    private readonly IReadOnlyList<IConversationAgent> _agents;
    private readonly ISelfieImageGenerator _generator;
    private readonly TimeProvider _timeProvider;
    private readonly Func<long> _nextSeed;

    public SelfieService(IConversationStore store, IContactService contacts, IUserProfileService profile, IEnumerable<IConversationAgent> agents, ISelfieImageGenerator generator)
        : this(store, contacts, profile, agents, generator, TimeProvider.System, () => Random.Shared.NextInt64(0, int.MaxValue))
    {
    }

    internal SelfieService(IConversationStore store, IContactService contacts, IUserProfileService profile, IEnumerable<IConversationAgent> agents, ISelfieImageGenerator generator, TimeProvider timeProvider, Func<long> nextSeed)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _contacts = contacts ?? throw new ArgumentNullException(nameof(contacts));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        ArgumentNullException.ThrowIfNull(agents);
        _agents = [.. agents];
        _generator = generator ?? throw new ArgumentNullException(nameof(generator));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _nextSeed = nextSeed ?? throw new ArgumentNullException(nameof(nextSeed));
    }

    public SelfieAvailability GetAvailability(IReadOnlyList<Contact> contacts)
    {
        ArgumentNullException.ThrowIfNull(contacts);

        SelfieGeneratorAvailability generator = _generator.GetAvailability();

        if (!generator.IsReady)
        {
            return generator.IsRuntimeIncluded
                ? new SelfieAvailability(false, true, "To ask for selfies, download a selfie model in Settings, under Image Generation.")
                : new SelfieAvailability(false, false, generator.Problem ?? "Selfies are not available on this system.");
        }

        if (!contacts.Any(HasPhoto))
        {
            string names = string.Join(" or ", contacts.Select(contact => contact.DisplayName));
            return new SelfieAvailability(false, false, contacts.Count == 0
                ? "There is no one in this chat to ask for a selfie."
                : $"Give {names} a profile photo on the Contacts page to ask for a selfie. The selfie keeps the face in it.");
        }

        return new SelfieAvailability(true, false, null);
    }

    public async Task<SelfieResult> TakeAsync(ConversationId conversationId, IProgress<SelfieProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (await _store.GetAsync(conversationId, cancellationToken) is not { } conversation)
        {
            return new SelfieResult(SelfieStatus.NotFound, null, "The conversation no longer exists.");
        }

        List<Contact> contacts = [];

        foreach (ContactId id in conversation.ContactIds)
        {
            if (await _contacts.GetAsync(id, cancellationToken) is { } contact)
            {
                contacts.Add(contact);
            }
        }

        if (GetAvailability(contacts) is { IsReady: false } availability)
        {
            return SelfieResult.Unavailable(availability.Problem ?? "Selfies are not available.");
        }

        IReadOnlyList<ChatMessage> messages = await _store.ListMessagesAsync(conversationId, cancellationToken, withPictures: false);
        IReadOnlyList<Contact> candidates = [.. contacts.Where(HasPhoto)];
        IReadOnlyList<Contact> ranked = conversation.IsGroup ? GroupCoordinator.RankSelfieSubjects(conversation, candidates, messages) : candidates;

        // The model draws as many people as it can, from the likeliest; the rest are left out.
        int room = Math.Max(1, _generator.GetAvailability().MaxSubjects);
        IReadOnlyList<Contact> subjects = [.. ranked.Take(room)];
        Contact lead = subjects[0];
        IReadOnlyList<Contact> others = [.. contacts.Where(contact => contact.Id != lead.Id)];

        progress?.Report(new SelfieProgress(lead.Id, SelfieStage.Planning));
        (SelfiePlan plan, AgentAssignment? planner) = await PlanAsync(lead, others, conversation, messages, cancellationToken);
        DateOnly today = DateOnly.FromDateTime(_timeProvider.GetLocalNow().Date);

        // Only the lead's expression and clothing come from the plan; anyone beside them is described by their photo.
        List<SelfieSubject> people = [.. subjects.Select(contact => contact.Id == lead.Id
            ? new SelfieSubject(contact.DisplayName, contact.Photo!, SelfieDirection.Appearance(contact, today), plan.Expression ?? SelfieDirection.DEFAULT_EXPRESSION, SelfieDirection.Clothing(contact, plan))
            : new SelfieSubject(contact.DisplayName, contact.Photo!, SelfieDirection.Appearance(contact, today), SelfieDirection.DEFAULT_EXPRESSION, SelfieDirection.Clothing(contact, SelfiePlan.Unknown)))];

        IProgress<ImageGenerationProgress>? drawing = progress is null
            ? null
            : new ForwardingProgress<ImageGenerationProgress>(update => progress.Report(new SelfieProgress(lead.Id, SelfieStage.Drawing, update)));
        ImageGenerationResult image = await _generator.GenerateAsync(new SelfieImageRequest(people, plan.Scene, _nextSeed()), drawing, cancellationToken);

        if (!image.IsSuccessful)
        {
            return image.Status switch
            {
                ImageGenerationStatus.Canceled => new SelfieResult(SelfieStatus.Canceled, null, null),
                ImageGenerationStatus.Busy => new SelfieResult(SelfieStatus.Busy, null, image.Problem),
                ImageGenerationStatus.Unavailable => SelfieResult.Unavailable(image.Problem ?? "Selfies are not available."),
                _ => new SelfieResult(SelfieStatus.Failed, null, image.Problem ?? $"{lead.DisplayName}'s selfie could not be drawn.")
            };
        }

        string caption = plan.Caption ?? string.Empty;

        if (caption.Length > ChatMessageValidator.MAX_TEXT_LENGTH)
        {
            caption = caption[..ChatMessageValidator.MAX_TEXT_LENGTH];
        }

        ChatMessage selfie = new()
        {
            Id = MessageId.New(),
            ConversationId = conversation.Id,
            Sender = MessageSender.Contact,
            SenderContactId = lead.Id,
            Text = caption,
            SentAtUtc = _timeProvider.GetUtcNow(),
            Agent = planner,
            Image = new MessageImage(image.Png!, SelfieDirection.Describe(plan))
        };

        // Stopping counts until the selfie is saved; once saving starts, it is saved. It is saved even if the chat was left
        // meanwhile, as a reply is; a deleted conversation is reported.
        if (cancellationToken.IsCancellationRequested)
        {
            return new SelfieResult(SelfieStatus.Canceled, null, null);
        }

        return await _store.AddMessageAsync(selfie, CancellationToken.None)
            ? new SelfieResult(SelfieStatus.Success, selfie, null)
            : new SelfieResult(SelfieStatus.NotFound, null, "The conversation was deleted before the selfie was saved.");
    }

    /// <summary>
    /// Asks the contact's own model what the selfie shows. Contacts with canned replies, or whose model cannot answer, send
    /// a selfie of themselves smiling, somewhere ordinary; planning never stops the selfie.
    /// </summary>
    private async Task<(SelfiePlan Plan, AgentAssignment? Planner)> PlanAsync(Contact subject, IReadOnlyList<Contact> others, Conversation conversation, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken)
    {
        if (subject.Agent is not { } assignment || FindAgent(assignment) is not { } agent || agent is SimulatedAgent)
        {
            return (SelfiePlan.Unknown, null);
        }

        UserProfile user = await _profile.GetAsync(cancellationToken);
        AgentRequest request = SelfieDirection.PlanRequest(subject, user, others, conversation, messages, assignment.Model.Trim(), _timeProvider.GetLocalNow());

        try
        {
            AgentReply answer = await agent.ReplyAsync(request, cancellationToken);

            return answer.Status == ReplyStatus.Success ? (SelfieDirection.ReadPlan(answer.Text), assignment) : (SelfiePlan.Unknown, null);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return (SelfiePlan.Unknown, null);
        }
    }

    private IConversationAgent? FindAgent(AgentAssignment assignment)
    {
        return _agents.LastOrDefault(candidate => string.Equals(candidate.Provider, assignment.Provider.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasPhoto(Contact contact)
    {
        return contact.Photo is { Length: > 0 };
    }

    /// <summary>
    /// Passes progress on as it is reported; the caller's own progress decides which thread it is shown on.
    /// </summary>
    private sealed class ForwardingProgress<T> : IProgress<T>
    {
        private readonly Action<T> _report;

        public ForwardingProgress(Action<T> report)
        {
            _report = report;
        }

        public void Report(T value)
        {
            _report(value);
        }
    }
}
