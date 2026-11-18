using HomiliaMessenger.Business.Modules.Agents;
using HomiliaMessenger.Business.Modules.Contacts;
using HomiliaMessenger.Business.Modules.Conversations;
using HomiliaMessenger.Business.Modules.ImageGeneration;
using HomiliaMessenger.Business.Modules.Memories;
using HomiliaMessenger.Business.Modules.Selfies;
using HomiliaMessenger.Presentation.Desktop.Models;
using HomiliaMessenger.Presentation.Desktop.ViewModels;
using HomiliaMessenger.Tests.ImageGeneration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace HomiliaMessenger.Tests.Selfies;

/// <summary>
/// The selfie button in the messenger, and a selfie arriving in its chat while the chat goes on.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class SelfieMessengerTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Selfie_ShowsBeingTaken_LetsTheChatGoOn_AndArrivesAsAPictureMessage()
    {
        FakeSelfieGenerator generator = new() { Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        using TestDatabase database = await DatabaseAsync(generator);
        Contact walt = await ContactAsync(database, "Walt", FakeImageGenerator.TinyPng(50));
        MessengerViewModel messenger = CreateViewModel(database);
        await messenger.InitializeAsync();
        await messenger.StartChatCommand.ExecuteAsync(walt);

        Assert.IsTrue(messenger.IsSelfieButtonVisible);
        Assert.IsFalse(messenger.CanAskForSelfie, "A new chat has nothing going on to show yet.");
        Assert.Contains("first", messenger.SelfieHint);

        messenger.DraftText = "Where are you?";
        await messenger.SendCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => !messenger.IsReplyPending);
        Assert.IsTrue(messenger.CanAskForSelfie);
        Assert.AreEqual("Ask Walt for a selfie", messenger.SelfieHint);

        messenger.AskForSelfieCommand.Execute(null);
        await WaitUntilAsync(() => generator.Requests.Count == 1);

        Assert.IsTrue(messenger.IsSelfieInThread);
        Assert.AreEqual(walt.Id, messenger.SelfieContact?.Id);
        await WaitUntilAsync(() => messenger.SelfieStatus.StartsWith("Walt is", StringComparison.Ordinal));
        Assert.IsFalse(messenger.CanAskForSelfie, "One selfie at a time.");

        // The chat goes on while the selfie is taken.
        messenger.DraftText = "Take your time!";
        await messenger.SendCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => !messenger.IsReplyPending);
        int before = messenger.Messages.Count;

        generator.Hold.SetResult();
        await WaitUntilAsync(() => !messenger.IsSelfieInThread);

        MessageItem selfie = messenger.Messages[^1];
        Assert.HasCount(before + 1, messenger.Messages);
        Assert.IsTrue(selfie.HasImage);
        Assert.AreSequenceEqual(FakeSelfieGenerator.PNG, selfie.ImagePng!);
        Assert.AreEqual(walt.Id, selfie.Sender?.Id);
        Assert.StartsWith("Sent a selfie", selfie.ImageDescription!);
        Assert.IsTrue(messenger.CanAskForSelfie);
        Assert.AreEqual("📷 Selfie", messenger.Conversations.Single().Preview);
    }

    [TestMethod]
    public async Task Selfie_CanBeStopped_AndNothingArrives()
    {
        FakeSelfieGenerator generator = new() { Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        using TestDatabase database = await DatabaseAsync(generator);
        Contact walt = await ContactAsync(database, "Walt", FakeImageGenerator.TinyPng(50));
        MessengerViewModel messenger = CreateViewModel(database);
        await messenger.InitializeAsync();
        await messenger.StartChatCommand.ExecuteAsync(walt);
        messenger.DraftText = "Hi";
        await messenger.SendCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => !messenger.IsReplyPending);
        int before = messenger.Messages.Count;

        messenger.AskForSelfieCommand.Execute(null);
        await WaitUntilAsync(() => generator.Requests.Count == 1);
        messenger.StopSelfieCommand.Execute(null);
        await WaitUntilAsync(() => !messenger.IsSelfieInThread);

        Assert.HasCount(before, messenger.Messages);
        Assert.IsFalse(messenger.HasReplyProblem);
        Assert.IsTrue(messenger.CanAskForSelfie);
    }

    [TestMethod]
    public async Task Button_SaysWhyNot_WithoutAPhotoOrAModel()
    {
        FakeSelfieGenerator generator = new();
        using TestDatabase database = await DatabaseAsync(generator);
        Contact sam = await ContactAsync(database, "Sam", photo: null);
        MessengerViewModel messenger = CreateViewModel(database);
        await messenger.InitializeAsync();
        await messenger.StartChatCommand.ExecuteAsync(sam);
        messenger.DraftText = "Hi";
        await messenger.SendCommand.ExecuteAsync(null);

        Assert.IsFalse(messenger.CanAskForSelfie);
        Assert.Contains("profile photo", messenger.SelfieHint);

        generator.Availability = new SelfieGeneratorAvailability(false, null, 1, true, "No model.");
        await messenger.InitializeAsync();

        Assert.IsFalse(messenger.CanAskForSelfie);
        Assert.Contains("Settings", messenger.SelfieHint);
    }

    private Task<TestDatabase> DatabaseAsync(FakeSelfieGenerator generator)
    {
        return TestDatabase.CreateAsync(TestContext, services =>
        {
            services.AddSingleton<ISelfieImageGenerator>(generator);
            services.AddSingleton<IConversationAgent>(new SimulatedAgent(new Random(1), TimeProvider.System, TimeSpan.Zero, TimeSpan.Zero));
        });
    }

    private async Task<Contact> ContactAsync(TestDatabase database, string name, byte[]? photo)
    {
        Contact contact = new() { Id = ContactId.New(), GivenName = name, Birthday = new DateOnly(1970, 1, 1), Photo = photo };
        Assert.IsTrue((await database.Services.GetRequiredService<IContactService>().SaveAsync(contact, TestContext.CancellationToken)).IsValid);
        return contact;
    }

    private static MessengerViewModel CreateViewModel(TestDatabase database)
    {
        return new MessengerViewModel(
            database.Services.GetRequiredService<IConversationService>(),
            database.Services.GetRequiredService<IContactService>(),
            database.Services.GetRequiredService<IMemoryService>(),
            selfies: database.Services.GetRequiredService<ISelfieService>());
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();

        while (!condition())
        {
            if (stopwatch.Elapsed > TimeSpan.FromSeconds(10))
            {
                Assert.Fail("Timed out waiting for the messenger to update.");
            }

            await Task.Delay(10);
        }
    }
}
