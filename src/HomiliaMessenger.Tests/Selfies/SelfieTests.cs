using HomiliaMessenger.Business.Modules.Agents;
using HomiliaMessenger.Business.Modules.Contacts;
using HomiliaMessenger.Business.Modules.Conversations;
using HomiliaMessenger.Business.Modules.ImageGeneration;
using HomiliaMessenger.Business.Modules.Selfies;
using HomiliaMessenger.Tests.Conversations;
using HomiliaMessenger.Tests.ImageGeneration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Tests.Selfies;

/// <summary>
/// Asking a chat for a selfie: who it is of, what the contact plans it to show, and how it arrives in the chat.
/// </summary>
[TestClass]
public sealed class SelfieTests
{
    private static readonly byte[] PHOTO = FakeImageGenerator.TinyPng(120);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void Plan_ReadsEachLabelledLine_AndLeavesWhatIsMissingOrNoneUnknown()
    {
        SelfiePlan plan = SelfieDirection.ReadPlan("""
            Sure! Here's the plan:
            **Expression:** laughing
            - Clothing: a yellow raincoat.
            Location: none
            Activity: waiting for the bus
            Weather: "pouring rain"
            Light: N/A
            Caption: Guess who forgot an umbrella 🙃
            Expression: crying
            """);

        Assert.AreEqual("laughing", plan.Expression, "The first answer for a label counts.");
        Assert.AreEqual("a yellow raincoat", plan.Clothing);
        Assert.IsNull(plan.Scene.Location);
        Assert.AreEqual("waiting for the bus", plan.Scene.Activity);
        Assert.IsNull(plan.Scene.Props);
        Assert.AreEqual("pouring rain", plan.Scene.Weather);
        Assert.IsNull(plan.Scene.Lighting);
        Assert.AreEqual("Guess who forgot an umbrella 🙃", plan.Caption);
        Assert.AreEqual(SelfiePlan.Unknown, SelfieDirection.ReadPlan("I can't help with that."));
    }

    [TestMethod]
    public void Plan_KeepsEachPartShort()
    {
        SelfiePlan plan = SelfieDirection.ReadPlan($"Location: {new string('a', 500)}\nCaption: {new string('b', 900)}");

        Assert.HasCount(SelfieDirection.MAX_PART_LENGTH, plan.Scene.Location!);
        Assert.HasCount(SelfieDirection.MAX_CAPTION_LENGTH, plan.Caption!);
    }

    [TestMethod]
    public void Appearance_IsThePersonInThePhotosDescription_OtherwiseTheirGenderAndAge()
    {
        DateOnly today = new(2026, 11, 18);
        Contact described = new() { GivenName = "Walt", Gender = Gender.Male, Birthday = new DateOnly(1950, 1, 1), Appearance = "An older man with a long white beard and glasses, wearing a plaid shirt, in his workshop." };
        Contact woman = new() { GivenName = "Elena", Gender = Gender.Female, Birthday = new DateOnly(1990, 6, 1) };
        Contact unknown = new() { GivenName = "Sam", Birthday = new DateOnly(1970, 6, 1) };

        Assert.AreEqual("older man with a long white beard and eyeglasses", SelfieDirection.Appearance(described, today));
        Assert.AreEqual("woman in her thirties", SelfieDirection.Appearance(woman, today));
        Assert.AreEqual("person in their fifties", SelfieDirection.Appearance(unknown, today));
        Assert.AreEqual("a plaid shirt", SelfieDirection.Clothing(described, SelfiePlan.Unknown));
        Assert.AreEqual("a tuxedo", SelfieDirection.Clothing(described, SelfiePlan.Unknown with { Clothing = "a tuxedo" }));
    }

    [TestMethod]
    public void Description_SaysWhatTheSelfieShows_ForThoseWhoCannotSeeIt()
    {
        SelfiePlan plan = new("laughing", "a yellow raincoat", new SelfieScene("a bus stop", "waiting for the bus", "an umbrella", "pouring rain", null), "Ha!");

        Assert.AreEqual("Sent a selfie: laughing, waiting for the bus, at a bus stop, pouring rain, wearing a yellow raincoat, with an umbrella", SelfieDirection.Describe(plan));
        Assert.AreEqual("Sent a selfie: smiling, in the kitchen", SelfieDirection.Describe(SelfiePlan.Unknown with { Scene = new SelfieScene("in the kitchen") }));
    }

    [TestMethod]
    public void Subjects_InAGroup_AreWhoeverTheUserSpokeTo_ThenNamed_ThenWhoOfferedAPicture_ThenWhoWroteLast()
    {
        Contact marcus = new() { Id = ContactId.New(), GivenName = "Marcus" };
        Contact elena = new() { Id = ContactId.New(), GivenName = "Elena" };
        Conversation group = new() { Id = ConversationId.New(), Kind = ConversationKind.Group, Participants = [new(marcus.Id), new(elena.Id)] };

        IReadOnlyList<Contact> Rank(params ChatMessage[] messages)
        {
            return GroupCoordinator.RankSelfieSubjects(group, [marcus, elena], messages);
        }

        Assert.AreEqual(elena.Id, Rank(User("Elena, where are you right now?"), From(marcus, "I'm home."))[0].Id);
        Assert.AreEqual(elena.Id, Rank(User("I bet Elena is at the beach."), From(marcus, "Ha."))[0].Id);
        Assert.AreEqual(marcus.Id, Rank(User("What are you two up to?"), From(marcus, "Hiking! I'll send you a pic."), From(elena, "Nice."))[0].Id);
        Assert.AreEqual(elena.Id, Rank(User("What are you two up to?"), From(marcus, "Hiking."), From(elena, "Baking."))[0].Id);
        Assert.AreEqual(marcus.Id, Rank()[0].Id, "With nothing to go on, the order they joined.");
    }

    [TestMethod]
    public async Task Direct_TheContactPlansTheSelfieWithTheirModel_AndItArrivesAsTheirMessage()
    {
        ScriptedAgent agent = new();
        agent.Answer = request => request.Instructions.StartsWith("You plan the selfie", StringComparison.Ordinal)
            ? AgentReply.Success("Expression: soaking wet and laughing\nClothing: a yellow raincoat\nLocation: a bus stop\nActivity: waiting for the bus\nProps: none\nWeather: pouring rain\nLight: gray daylight\nCaption: Guess who forgot an umbrella")
            : null;
        FakeSelfieGenerator generator = new();
        using TestDatabase database = await DatabaseAsync(generator, agent);
        Contact walt = await ContactAsync(database, "Walt", photo: PHOTO, appearance: "An older man with a white beard, in his workshop.");
        Conversation chat = await SendAsync(database, walt, "Where are you? It's pouring here.");

        SelfieResult result = await Selfies(database).TakeAsync(chat.Id, cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual(SelfieStatus.Success, result.Status, result.Problem);
        ChatMessage selfie = result.Message!;
        Assert.AreEqual(walt.Id, selfie.SenderContactId);
        Assert.AreEqual("Guess who forgot an umbrella", selfie.Text);
        Assert.AreEqual("Sent a selfie: soaking wet and laughing, waiting for the bus, at a bus stop, pouring rain, wearing a yellow raincoat", selfie.Image!.Description);
        Assert.AreEqual(new AgentAssignment(GroupTestKit.PROVIDER, "walt-model"), selfie.Agent);

        SelfieImageRequest request = generator.Requests.Single();
        SelfieSubject subject = request.Subjects.Single();
        Assert.AreSequenceEqual(PHOTO, subject.Photo);
        Assert.AreEqual("older man with a white beard", subject.Appearance);
        Assert.AreEqual("soaking wet and laughing", subject.Expression);
        Assert.AreEqual("a yellow raincoat", subject.Clothing);
        Assert.AreEqual(new SelfieScene("a bus stop", "waiting for the bus", null, "pouring rain", "gray daylight"), request.Scene);

        // The plan was asked of Walt's model, from the conversation; and the selfie is saved with the chat.
        AgentRequest plan = agent.Requests.Single();
        Assert.Contains("Where are you? It's pouring here.", plan.Turns.Single().Text);
        Assert.IsTrue(plan.Precise);
        IReadOnlyList<ChatMessage> stored = await database.Services.GetRequiredService<IConversationService>().ListMessagesAsync(chat.Id, TestContext.CancellationToken);
        Assert.AreSequenceEqual(generator.Png, stored[^1].Image!.Png);
        Assert.AreEqual(selfie.Image.Description, stored[^1].Image!.Description);
    }

    [TestMethod]
    public async Task Selfie_IsReadByAgentsAsWhatItShows()
    {
        ScriptedAgent agent = new();
        agent.Answer = request => request.Instructions.StartsWith("You plan the selfie", StringComparison.Ordinal) ? AgentReply.Success("Expression: grinning\nLocation: the beach\nCaption: Wish you were here") : null;
        using TestDatabase database = await DatabaseAsync(new FakeSelfieGenerator(), agent);
        Contact walt = await ContactAsync(database, "Walt", photo: PHOTO);
        Conversation chat = await SendAsync(database, walt, "Send me a selfie!");
        await Selfies(database).TakeAsync(chat.Id, cancellationToken: TestContext.CancellationToken);
        IConversationService conversations = database.Services.GetRequiredService<IConversationService>();
        await conversations.SendAsync(walt.Id, "Looks fun!", TestContext.CancellationToken);

        await conversations.ReplyAsync(chat.Id, cancellationToken: TestContext.CancellationToken);

        AgentRequest reply = agent.ReplyRequests.Last();
        Assert.Contains(turn => turn.Sender == MessageSender.Contact && turn.Text == "[Sent a selfie: grinning, at the beach] Wish you were here", reply.Turns);
    }

    [TestMethod]
    public async Task Group_IsOfTheContactSpokenTo_AmongThoseWithPhotos_AndOnlyAsManyAsTheModelDraws()
    {
        ScriptedAgent agent = new();
        FakeSelfieGenerator generator = new();
        using TestDatabase database = await DatabaseAsync(generator, agent);
        Contact marcus = await ContactAsync(database, "Marcus", photo: PHOTO);
        Contact elena = await ContactAsync(database, "Elena", photo: FakeImageGenerator.TinyPng(7));
        Conversation group = await GroupTestKit.CreateGroupAsync(database, TestContext, marcus, elena);
        await database.Services.GetRequiredService<IConversationService>().SendAsync(group.Id, "Elena, how's the trip?", TestContext.CancellationToken);

        SelfieResult result = await Selfies(database).TakeAsync(group.Id, cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual(SelfieStatus.Success, result.Status, result.Problem);
        Assert.AreEqual(elena.Id, result.Message!.SenderContactId);
        Assert.AreEqual("Elena", generator.Requests.Single().Subjects.Single().Name);
        Assert.Contains("chat with Me and Marcus", agent.Requests.Single().Instructions);

        // Spoken to, but without a photo: the selfie is of someone who has one.
        Contact sam = await ContactAsync(database, "Sam");
        Conversation another = await GroupTestKit.CreateGroupAsync(database, TestContext, marcus, sam);
        await database.Services.GetRequiredService<IConversationService>().SendAsync(another.Id, "Sam, selfie please!", TestContext.CancellationToken);
        SelfieResult instead = await Selfies(database).TakeAsync(another.Id, cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual(marcus.Id, instead.Message!.SenderContactId);
    }

    [TestMethod]
    public async Task Availability_NeedsAModelAndSomeoneWithAPhoto()
    {
        FakeSelfieGenerator generator = new() { Availability = new SelfieGeneratorAvailability(false, null, 1, true, "Download a selfie model.") };
        using TestDatabase database = await DatabaseAsync(generator);
        Contact walt = await ContactAsync(database, "Walt", photo: PHOTO);
        Contact sam = await ContactAsync(database, "Sam");
        ISelfieService selfies = Selfies(database);

        SelfieAvailability noModel = selfies.GetAvailability([walt]);
        generator.Availability = new SelfieGeneratorAvailability(true, "Test", 1, true, null);
        SelfieAvailability noPhoto = selfies.GetAvailability([sam]);
        SelfieAvailability ready = selfies.GetAvailability([sam, walt]);
        Conversation chat = await SendAsync(database, sam, "Hi");
        SelfieResult refused = await selfies.TakeAsync(chat.Id, cancellationToken: TestContext.CancellationToken);

        Assert.IsFalse(noModel.IsReady);
        Assert.IsTrue(noModel.NeedsModel);
        Assert.IsFalse(noPhoto.IsReady);
        Assert.IsFalse(noPhoto.NeedsModel);
        Assert.Contains("Sam", noPhoto.Problem!);
        Assert.IsTrue(ready.IsReady);
        Assert.AreEqual(SelfieStatus.Unavailable, refused.Status);
        Assert.IsEmpty(generator.Requests);
    }

    [TestMethod]
    public async Task CannedContacts_SendASmilingSelfieWithoutWords_AndAFailedPlanNeverStopsOne()
    {
        ScriptedAgent agent = new() { Answer = _ => AgentReply.Unavailable("The model is not downloaded.") };
        FakeSelfieGenerator generator = new();
        using TestDatabase database = await DatabaseAsync(generator, agent);
        Contact canned = await ContactAsync(database, "Canned", photo: PHOTO, model: null);
        Contact failing = await ContactAsync(database, "Failing", photo: PHOTO);

        SelfieResult first = await Selfies(database).TakeAsync((await SendAsync(database, canned, "Hi")).Id, cancellationToken: TestContext.CancellationToken);
        SelfieResult second = await Selfies(database).TakeAsync((await SendAsync(database, failing, "Hi")).Id, cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual(SelfieStatus.Success, first.Status);
        Assert.AreEqual(SelfieStatus.Success, second.Status);
        Assert.AreEqual(string.Empty, first.Message!.Text);
        Assert.IsNull(first.Message.Agent);
        Assert.AreEqual("Sent a selfie: smiling", first.Message.Image!.Description);
        Assert.AreEqual(SelfieDirection.DEFAULT_EXPRESSION, generator.Requests[1].Subjects.Single().Expression);
    }

    [TestMethod]
    public async Task Failures_AreReported_AndNothingIsSaved()
    {
        FakeSelfieGenerator generator = new() { Result = ImageGenerationResult.Busy };
        using TestDatabase database = await DatabaseAsync(generator);
        Contact walt = await ContactAsync(database, "Walt", photo: PHOTO, model: null);
        Conversation chat = await SendAsync(database, walt, "Hi");

        SelfieResult busy = await Selfies(database).TakeAsync(chat.Id, cancellationToken: TestContext.CancellationToken);
        generator.Result = ImageGenerationResult.Canceled;
        SelfieResult canceled = await Selfies(database).TakeAsync(chat.Id, cancellationToken: TestContext.CancellationToken);
        SelfieResult missing = await Selfies(database).TakeAsync(ConversationId.New(), cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual(SelfieStatus.Busy, busy.Status);
        Assert.IsNotNull(busy.Problem);
        Assert.AreEqual(SelfieStatus.Canceled, canceled.Status);
        Assert.AreEqual(SelfieStatus.NotFound, missing.Status);
        Assert.HasCount(1, await database.Services.GetRequiredService<IConversationService>().ListMessagesAsync(chat.Id, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task Stopped_AfterTheSelfieIsDrawn_ItIsNotSaved()
    {
        using TestDatabase database = await DatabaseAsync(new FakeSelfieGenerator());
        Contact walt = await ContactAsync(database, "Walt", photo: PHOTO, model: null);
        Conversation chat = await SendAsync(database, walt, "Hi");
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        List<SelfieProgress> updates = [];

        // Stopped as the drawing finishes, before the selfie is saved.
        SelfieResult result = await Selfies(database).TakeAsync(chat.Id, new ListProgress(updates, update =>
        {
            if (update.Stage == SelfieStage.Drawing)
            {
                stop.Cancel();
            }
        }), stop.Token);

        Assert.AreEqual(SelfieStatus.Canceled, result.Status);
        Assert.HasCount(1, await database.Services.GetRequiredService<IConversationService>().ListMessagesAsync(chat.Id, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task Progress_SaysWhoIsTakingTheSelfie_FromTheStart()
    {
        using TestDatabase database = await DatabaseAsync(new FakeSelfieGenerator());
        Contact walt = await ContactAsync(database, "Walt", photo: PHOTO, model: null);
        Conversation chat = await SendAsync(database, walt, "Hi");
        List<SelfieProgress> updates = [];

        await Selfies(database).TakeAsync(chat.Id, new ListProgress(updates), TestContext.CancellationToken);

        Assert.AreEqual(new SelfieProgress(walt.Id, SelfieStage.Planning), updates[0]);
        Assert.Contains(update => update.Stage == SelfieStage.Drawing && update.Image?.Stage == ImageGenerationStage.Sampling, updates);
        Assert.IsTrue(updates.All(update => update.Subject == walt.Id));
    }

    [TestMethod]
    public async Task Store_KeepsAContactsAppearance_AndAMessagesPicture_ListingOnlyWhatItShows()
    {
        using TestDatabase database = await DatabaseAsync(new FakeSelfieGenerator());
        Contact walt = await ContactAsync(database, "Walt", photo: PHOTO, appearance: "An older man.", model: null);
        Conversation chat = await SendAsync(database, walt, "Hi");
        await Selfies(database).TakeAsync(chat.Id, cancellationToken: TestContext.CancellationToken);

        using TestDatabase reopened = await database.ReopenAsync(TestContext);
        Contact stored = (await reopened.Services.GetRequiredService<IContactService>().GetAsync(walt.Id, TestContext.CancellationToken))!;
        Conversation listed = (await reopened.Services.GetRequiredService<IConversationService>().ListAsync(TestContext.CancellationToken)).Single();
        ChatMessage selfie = (await reopened.Services.GetRequiredService<IConversationService>().ListMessagesAsync(chat.Id, TestContext.CancellationToken))[^1];

        Assert.AreEqual("An older man.", stored.Appearance);
        Assert.IsNotNull(listed.LastMessage!.Image);
        Assert.IsEmpty(listed.LastMessage.Image.Png);
        Assert.AreEqual("Sent a selfie: smiling", listed.LastMessage.Image.Description);
        Assert.AreSequenceEqual(FakeSelfieGenerator.PNG, selfie.Image!.Png);
        ChatMessage forAgents = (await reopened.Services.GetRequiredService<IConversationStore>().ListMessagesAsync(chat.Id, TestContext.CancellationToken, withPictures: false))[^1];
        Assert.IsEmpty(forAgents.Image!.Png);
        Assert.AreEqual("Sent a selfie: smiling", forAgents.Image.Description);
        Assert.IsNull((await reopened.Services.GetRequiredService<IConversationService>().ListMessagesAsync(chat.Id, TestContext.CancellationToken))[0].Image);
    }

    private ISelfieService Selfies(TestDatabase database)
    {
        return database.Services.GetRequiredService<ISelfieService>();
    }

    private Task<TestDatabase> DatabaseAsync(FakeSelfieGenerator generator, ScriptedAgent? agent = null)
    {
        return TestDatabase.CreateAsync(TestContext, services =>
        {
            services.AddSingleton<ISelfieImageGenerator>(generator);
            services.AddSingleton<IConversationAgent>(agent ?? new ScriptedAgent());
        });
    }

    private async Task<Contact> ContactAsync(TestDatabase database, string name, byte[]? photo = null, string? appearance = null, string? model = "default")
    {
        Contact contact = new()
        {
            Id = ContactId.New(),
            GivenName = name,
            Gender = Gender.Male,
            Birthday = new DateOnly(1960, 1, 1),
            Agent = model is null ? null : new AgentAssignment(GroupTestKit.PROVIDER, model == "default" ? name.ToLowerInvariant() + "-model" : model),
            Photo = photo,
            Appearance = appearance
        };

        Assert.IsTrue((await database.Services.GetRequiredService<IContactService>().SaveAsync(contact, TestContext.CancellationToken)).IsValid);
        return contact;
    }

    private async Task<Conversation> SendAsync(TestDatabase database, Contact contact, string text)
    {
        SendMessageResult sent = await database.Services.GetRequiredService<IConversationService>().SendAsync(contact.Id, text, TestContext.CancellationToken);
        Assert.IsTrue(sent.Validation.IsValid);
        return sent.Conversation!;
    }

    private static ChatMessage User(string text)
    {
        return new ChatMessage { Id = MessageId.New(), Sender = MessageSender.User, Text = text, SentAtUtc = DateTimeOffset.UtcNow };
    }

    private static ChatMessage From(Contact contact, string text)
    {
        return new ChatMessage { Id = MessageId.New(), Sender = MessageSender.Contact, SenderContactId = contact.Id, Text = text, SentAtUtc = DateTimeOffset.UtcNow };
    }

    private sealed class ListProgress : IProgress<SelfieProgress>
    {
        private readonly List<SelfieProgress> _updates;
        private readonly Action<SelfieProgress>? _onReport;

        public ListProgress(List<SelfieProgress> updates, Action<SelfieProgress>? onReport = null)
        {
            _updates = updates;
            _onReport = onReport;
        }

        public void Report(SelfieProgress value)
        {
            _updates.Add(value);
            _onReport?.Invoke(value);
        }
    }
}

/// <summary>
/// A selfie generator that answers as told, recording what it is asked to draw.
/// </summary>
internal sealed class FakeSelfieGenerator : ISelfieImageGenerator
{
    internal static readonly byte[] PNG = FakeImageGenerator.TinyPng(200);

    public SelfieGeneratorAvailability Availability { get; set; } = new(true, "Test selfie model", 1, true, null);

    public ImageGenerationResult Result { get; set; } = ImageGenerationResult.Success(PNG);

    /// <summary>
    /// Held at drawing until set, so a test can act while a selfie is being taken.
    /// </summary>
    public TaskCompletionSource? Hold { get; set; }

    public byte[] Png => PNG;

    public List<SelfieImageRequest> Requests { get; } = [];

    public SelfieGeneratorAvailability GetAvailability()
    {
        return Availability;
    }

    public async Task<ImageGenerationResult> GenerateAsync(SelfieImageRequest request, IProgress<ImageGenerationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        lock (Requests)
        {
            Requests.Add(request);
        }

        progress?.Report(new ImageGenerationProgress(ImageGenerationStage.Sampling, 1, 4));

        if (Hold is { } hold)
        {
            try
            {
                await hold.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return ImageGenerationResult.Canceled;
            }
        }

        return Result;
    }
}
