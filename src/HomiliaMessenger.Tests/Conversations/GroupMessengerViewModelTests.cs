using HomiliaMessenger.Business.Modules.Contacts;
using HomiliaMessenger.Business.Modules.Conversations;
using HomiliaMessenger.Business.Modules.Memories;
using HomiliaMessenger.Presentation.Desktop.Models;
using HomiliaMessenger.Presentation.Desktop.ViewModels;
using HomiliaMessenger.Tests.Speech;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace HomiliaMessenger.Tests.Conversations;

[TestClass]
public sealed class GroupMessengerViewModelTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task InvitingFromAChat_OpensANewGroupAndKeepsTheChat()
    {
        using TestDatabase database = await GroupTestKit.CreateDatabaseAsync(TestContext);
        Contact walt = await GroupTestKit.AddContactAsync(database, "Walt", TestContext);
        Contact rose = await GroupTestKit.AddContactAsync(database, "Rose", TestContext);
        MessengerViewModel messenger = CreateViewModel(database);

        await messenger.InitializeAsync();
        await messenger.StartChatCommand.ExecuteAsync(walt);
        messenger.DraftText = "Hi Walt";
        await messenger.SendCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => messenger.Messages.Count == 2 && !messenger.IsContactTyping);
        ConversationId direct = messenger.ActiveConversationId!.Value;

        Assert.IsTrue(messenger.CanInvite);
        Assert.AreSequenceEqual(new[] { rose.Id }, messenger.InviteCandidates.Select(contact => contact.Id).ToArray());

        await messenger.InviteContactCommand.ExecuteAsync(rose);
        // A new group: the contact just invited says hello.
        await WaitUntilAsync(() => messenger.Messages.Count >= 1 && !messenger.IsReplyPending);

        Assert.IsTrue(messenger.IsGroupThread);
        Assert.IsFalse(messenger.CanInvite, "A group already has as many contacts as it may.");
        Assert.AreEqual("Walt & Rose", messenger.ActiveTitle);
        Assert.AreNotEqual(direct, messenger.ActiveConversationId);
        Assert.HasCount(2, messenger.Conversations);
        Assert.AreSame(messenger.Conversations.Single(item => item.IsGroup), messenger.SelectedConversation);
        Assert.AreEqual(rose.Id, messenger.Messages[0].Sender!.Id);
        Assert.IsTrue(messenger.Messages[0].ShowsSenderName);
        Assert.AreEqual("Rose", messenger.Messages[0].SenderName);
        Assert.HasCount(2, await database.Services.GetRequiredService<IConversationService>().ListMessagesAsync(direct, TestContext.CancellationToken));

        // Back to the private chat, unchanged.
        await messenger.StartChatCommand.ExecuteAsync(walt);
        Assert.IsFalse(messenger.IsGroupThread);
        Assert.AreEqual(direct, messenger.ActiveConversationId);
        Assert.HasCount(2, messenger.Messages);
        Assert.IsFalse(messenger.Messages[1].ShowsSenderName);
    }

    [TestMethod]
    public async Task SendingToAGroup_GetsAttributedRepliesWithinTheTurnLimit()
    {
        using TestDatabase database = await GroupTestKit.CreateDatabaseAsync(TestContext);
        Contact walt = await GroupTestKit.AddContactAsync(database, "Walt", TestContext);
        Contact rose = await GroupTestKit.AddContactAsync(database, "Rose", TestContext);
        await database.Services.GetRequiredService<IConversationService>().CreateGroupAsync([walt.Id, rose.Id], TestContext.CancellationToken);
        MessengerViewModel messenger = CreateViewModel(database);

        await messenger.InitializeAsync();
        messenger.SelectedConversation = messenger.Conversations.Single();
        await WaitUntilAsync(() => messenger.IsGroupThread && !messenger.IsReplyPending && messenger.Messages.Count >= 1);
        int before = messenger.Messages.Count;

        messenger.DraftText = "What are you two up to?";
        await messenger.SendCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => messenger.Messages.Count >= before + 3 && !messenger.IsReplyPending);
        await Task.Delay(200, TestContext.CancellationToken);

        List<MessageItem> round = [.. messenger.Messages.Skip(before + 1)];
        Assert.IsTrue(messenger.Messages[before].IsFromUser);
        Assert.IsInRange(2, new GroupCoordinatorOptions().MaxAutomaticTurns, round.Count);
        Assert.IsTrue(round.All(item => item.Sender is not null));
        Assert.IsTrue(new[] { walt.Id, rose.Id }.All(id => round.Any(item => item.Sender!.Id == id)), "Both were asked.");
        Assert.IsTrue(round.Zip(round.Skip(1)).All(pair => pair.First.Sender!.Id != pair.Second.Sender!.Id), "No one writes twice in a row.");
        Assert.StartsWith(round[^1].SenderName + ": ", messenger.Conversations.Single().Preview);
    }

    [TestMethod]
    public async Task LeavingAGroup_StopsItsContactsTalking()
    {
        using TestDatabase database = await GroupTestKit.CreateDatabaseAsync(TestContext, TimeSpan.FromMilliseconds(500));
        Contact walt = await GroupTestKit.AddContactAsync(database, "Walt", TestContext);
        Contact rose = await GroupTestKit.AddContactAsync(database, "Rose", TestContext);
        IConversationService service = database.Services.GetRequiredService<IConversationService>();
        MessengerViewModel messenger = CreateViewModel(database);

        await messenger.InitializeAsync();
        await messenger.StartChatCommand.ExecuteAsync(walt);
        await messenger.InviteContactCommand.ExecuteAsync(rose);
        ConversationId group = messenger.ActiveConversationId!.Value;
        await WaitUntilAsync(() => messenger.IsContactTyping);

        // Rose is still typing her hello when the user goes back to Walt.
        await messenger.StartChatCommand.ExecuteAsync(walt);
        await Task.Delay(900, TestContext.CancellationToken);

        Assert.IsFalse(messenger.IsContactTyping);
        Assert.IsFalse(messenger.Conversations.Single(item => item.Id == group).IsTyping);
        Assert.IsEmpty(await service.ListMessagesAsync(group, TestContext.CancellationToken));

        // Leaving the messenger page altogether stops a group too.
        messenger.SelectedConversation = messenger.Conversations.Single(item => item.Id == group);
        await WaitUntilAsync(() => messenger.IsGroupThread);
        messenger.Suspend();
        await Task.Delay(900, TestContext.CancellationToken);

        Assert.IsEmpty(await service.ListMessagesAsync(group, TestContext.CancellationToken), "Nothing is written while away.");
    }

    [TestMethod]
    public async Task Stopping_EndsTheGroupsRoundUntilTheUserWritesAgain()
    {
        using TestDatabase database = await GroupTestKit.CreateDatabaseAsync(TestContext, TimeSpan.FromMilliseconds(400));
        Contact walt = await GroupTestKit.AddContactAsync(database, "Walt", TestContext);
        Contact rose = await GroupTestKit.AddContactAsync(database, "Rose", TestContext);
        IConversationService service = database.Services.GetRequiredService<IConversationService>();
        GroupCreationResult created = await service.CreateGroupAsync([walt.Id, rose.Id], TestContext.CancellationToken);
        await service.SetInitiationAsync(created.Group!.Id, GroupInitiation.Off, TestContext.CancellationToken);
        MessengerViewModel messenger = CreateViewModel(database);

        await messenger.InitializeAsync();
        messenger.SelectedConversation = messenger.Conversations.Single();
        await WaitUntilAsync(() => messenger.IsGroupThread);
        Assert.IsFalse(messenger.IsGroupInitiationOn);
        Assert.IsFalse(messenger.IsContactTyping, "With initiation off, opening the group starts nothing.");

        messenger.DraftText = "Walt, you there?";
        await messenger.SendCommand.ExecuteAsync(null);
        Assert.IsTrue(messenger.StopRepliesCommand.CanExecute(null), "A round can be stopped even before anyone is typing.");
        await WaitUntilAsync(() => messenger.IsContactTyping);
        Assert.Contains("Walt is typing", messenger.ActiveSubtitle);
        messenger.StopRepliesCommand.Execute(null);
        await Task.Delay(800, TestContext.CancellationToken);

        Assert.IsFalse(messenger.IsContactTyping);
        Assert.HasCount(1, await service.ListMessagesAsync(created.Group.Id, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task GroupInitiationSetting_IsSaved()
    {
        using TestDatabase database = await GroupTestKit.CreateDatabaseAsync(TestContext);
        Contact walt = await GroupTestKit.AddContactAsync(database, "Walt", TestContext);
        Contact rose = await GroupTestKit.AddContactAsync(database, "Rose", TestContext);
        IConversationService service = database.Services.GetRequiredService<IConversationService>();
        GroupCreationResult created = await service.CreateGroupAsync([walt.Id, rose.Id], TestContext.CancellationToken);
        MessengerViewModel messenger = CreateViewModel(database);

        await messenger.InitializeAsync();
        messenger.SelectedConversation = messenger.Conversations.Single();
        await WaitUntilAsync(() => messenger.IsGroupThread && !messenger.IsReplyPending);
        Assert.IsTrue(messenger.IsGroupInitiationOn);

        // Changed back and forth quickly: the last choice is the one kept.
        messenger.IsGroupInitiationOn = false;
        messenger.IsGroupInitiationOn = true;
        messenger.IsGroupInitiationOn = false;
        await WaitUntilAsync(() => messenger.ActiveGroup?.Initiation == GroupInitiation.Off);
        await Task.Delay(200, TestContext.CancellationToken);

        Assert.IsFalse(messenger.IsGroupInitiationOn);
        Assert.AreEqual(GroupInitiation.Off, messenger.ActiveGroup!.Initiation);
        Assert.AreEqual(GroupInitiation.Off, (await service.GetAsync(created.Group!.Id, TestContext.CancellationToken))!.Initiation);
    }

    [TestMethod]
    public async Task GroupReplies_AreSpokenInOrderEachInItsSendersVoice()
    {
        FakeSpeech speech = new();
        using TestDatabase database = await GroupTestKit.CreateDatabaseAsync(TestContext);
        Contact walt = await GroupTestKit.AddContactAsync(database, "Walt", TestContext, voice: "am_adam");
        Contact rose = await GroupTestKit.AddContactAsync(database, "Rose", TestContext, voice: "af_heart");
        await database.Services.GetRequiredService<IConversationService>().CreateGroupAsync([walt.Id, rose.Id], TestContext.CancellationToken);
        MessengerViewModel messenger = CreateViewModel(database, speech);

        await messenger.InitializeAsync();
        messenger.SelectedConversation = messenger.Conversations.Single();
        await WaitUntilAsync(() => messenger.IsGroupThread && !messenger.IsReplyPending && messenger.Messages.Count >= 1);
        messenger.ToggleSpeechCommand.Execute(null);
        Assert.IsTrue(messenger.IsSpeechOn);
        int before = messenger.Messages.Count;

        messenger.DraftText = "What are you two up to?";
        await messenger.SendCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => messenger.Messages.Count >= before + 3 && !messenger.IsReplyPending);
        await WaitUntilAsync(() => speech.Spoken.Count == messenger.Messages.Count - before - 1);

        List<MessageItem> round = [.. messenger.Messages.Skip(before + 1)];
        Assert.AreSequenceEqual(round.Select(item => (item.Text, item.Sender!.VoiceId!)).ToArray(), speech.Spoken.ToArray());
    }

    [TestMethod]
    public async Task AContactWithoutAVoice_IsNotSpokenAndTheGroupCarriesOn()
    {
        FakeSpeech speech = new();
        using TestDatabase database = await GroupTestKit.CreateDatabaseAsync(TestContext);
        Contact walt = await GroupTestKit.AddContactAsync(database, "Walt", TestContext, voice: "am_adam");
        Contact rose = await GroupTestKit.AddContactAsync(database, "Rose", TestContext);
        await database.Services.GetRequiredService<IConversationService>().CreateGroupAsync([walt.Id, rose.Id], TestContext.CancellationToken);
        MessengerViewModel messenger = CreateViewModel(database, speech);

        await messenger.InitializeAsync();
        messenger.SelectedConversation = messenger.Conversations.Single();
        await WaitUntilAsync(() => messenger.IsGroupThread && !messenger.IsReplyPending && messenger.Messages.Count >= 1);
        messenger.ToggleSpeechCommand.Execute(null);
        int before = messenger.Messages.Count;

        messenger.DraftText = "What are you two up to?";
        await messenger.SendCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => messenger.Messages.Count >= before + 3 && !messenger.IsReplyPending);
        await Task.Delay(200, TestContext.CancellationToken);

        List<MessageItem> round = [.. messenger.Messages.Skip(before + 1)];
        Assert.Contains(item => item.Sender!.Id == rose.Id, round);
        Assert.AreSequenceEqual(round.Where(item => item.Sender!.Id == walt.Id).Select(item => item.Text).ToArray(), speech.Spoken.Select(spoken => spoken.Text).ToArray());
        Assert.IsTrue(speech.Spoken.All(spoken => spoken.Voice == "am_adam"));
    }

    [TestMethod]
    public async Task AMessageSentWhileTheGroupLoads_StaysInTheThread()
    {
        using TestDatabase database = await GroupTestKit.CreateDatabaseAsync(TestContext);
        Contact walt = await GroupTestKit.AddContactAsync(database, "Walt", TestContext);
        Contact rose = await GroupTestKit.AddContactAsync(database, "Rose", TestContext);
        IConversationService service = database.Services.GetRequiredService<IConversationService>();
        GroupCreationResult created = await service.CreateGroupAsync([walt.Id, rose.Id], TestContext.CancellationToken);
        await service.SetInitiationAsync(created.Group!.Id, GroupInitiation.Off, TestContext.CancellationToken);
        await service.SendAsync(created.Group.Id, "Earlier message", TestContext.CancellationToken);
        InterruptibleConversations conversations = new(service);
        MessengerViewModel messenger = new(conversations, database.Services.GetRequiredService<IContactService>(), database.Services.GetRequiredService<IMemoryService>());

        await messenger.InitializeAsync();
        conversations.HoldListMessages = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        messenger.SelectedConversation = messenger.Conversations.Single();
        await WaitUntilAsync(() => messenger.IsGroupThread);

        // Sent while the group's earlier messages are still loading.
        messenger.DraftText = "Sent during the load";
        await messenger.SendCommand.ExecuteAsync(null);
        conversations.HoldListMessages.SetResult();
        conversations.HoldListMessages = null;
        await WaitUntilAsync(() => messenger.Messages.Any(item => item.Text == "Earlier message") && !messenger.IsReplyPending);

        Assert.AreEqual("Earlier message", messenger.Messages[0].Text);
        Assert.AreEqual("Sent during the load", messenger.Messages[1].Text);
    }

    [TestMethod]
    public async Task AGroupSettingThatCannotBeSaved_ShowsWhatIsSaved()
    {
        using TestDatabase database = await GroupTestKit.CreateDatabaseAsync(TestContext);
        Contact walt = await GroupTestKit.AddContactAsync(database, "Walt", TestContext);
        Contact rose = await GroupTestKit.AddContactAsync(database, "Rose", TestContext);
        IConversationService service = database.Services.GetRequiredService<IConversationService>();
        GroupCreationResult created = await service.CreateGroupAsync([walt.Id, rose.Id], TestContext.CancellationToken);
        InterruptibleConversations conversations = new(service) { FailSetInitiation = true };
        MessengerViewModel messenger = new(conversations, database.Services.GetRequiredService<IContactService>(), database.Services.GetRequiredService<IMemoryService>());

        await messenger.InitializeAsync();
        messenger.SelectedConversation = messenger.Conversations.Single();
        await WaitUntilAsync(() => messenger.IsGroupThread && !messenger.IsReplyPending);
        messenger.IsGroupInitiationOn = false;
        await WaitUntilAsync(() => messenger.IsGroupInitiationOn);

        Assert.StartsWith("Could not save the group setting", messenger.Status);
        Assert.AreEqual(GroupInitiation.Occasional, (await service.GetAsync(created.Group!.Id, TestContext.CancellationToken))!.Initiation);
    }

    [TestMethod]
    public async Task TurningInitiationOff_StopsAContactStartingAConversation()
    {
        using TestDatabase database = await GroupTestKit.CreateDatabaseAsync(TestContext, TimeSpan.FromMilliseconds(500));
        Contact walt = await GroupTestKit.AddContactAsync(database, "Walt", TestContext);
        Contact rose = await GroupTestKit.AddContactAsync(database, "Rose", TestContext);
        IConversationService service = database.Services.GetRequiredService<IConversationService>();
        GroupCreationResult created = await service.CreateGroupAsync([walt.Id, rose.Id], TestContext.CancellationToken);
        MessengerViewModel messenger = CreateViewModel(database);

        await messenger.InitializeAsync();
        messenger.SelectedConversation = messenger.Conversations.Single();
        await WaitUntilAsync(() => messenger.IsContactTyping);

        // Rose is typing her hello when the user turns starting conversations off.
        messenger.IsGroupInitiationOn = false;
        await Task.Delay(900, TestContext.CancellationToken);

        Assert.IsFalse(messenger.IsContactTyping);
        Assert.IsEmpty(await service.ListMessagesAsync(created.Group!.Id, TestContext.CancellationToken));
        Assert.AreEqual(GroupInitiation.Off, (await service.GetAsync(created.Group.Id, TestContext.CancellationToken))!.Initiation);
    }

    [TestMethod]
    public async Task AFailedSaveAfterASuccessfulOne_ShowsTheSettingAsSaved()
    {
        using TestDatabase database = await GroupTestKit.CreateDatabaseAsync(TestContext);
        Contact walt = await GroupTestKit.AddContactAsync(database, "Walt", TestContext);
        Contact rose = await GroupTestKit.AddContactAsync(database, "Rose", TestContext);
        IConversationService service = database.Services.GetRequiredService<IConversationService>();
        GroupCreationResult created = await service.CreateGroupAsync([walt.Id, rose.Id], TestContext.CancellationToken);
        InterruptibleConversations conversations = new(service);
        MessengerViewModel messenger = new(conversations, database.Services.GetRequiredService<IContactService>(), database.Services.GetRequiredService<IMemoryService>());

        await messenger.InitializeAsync();
        messenger.SelectedConversation = messenger.Conversations.Single();
        await WaitUntilAsync(() => messenger.IsGroupThread && !messenger.IsReplyPending);
        messenger.IsGroupInitiationOn = false;
        await WaitUntilAsync(() => messenger.ActiveGroup?.Initiation == GroupInitiation.Off);

        conversations.FailSetInitiation = true;
        messenger.IsGroupInitiationOn = true;
        await WaitUntilAsync(() => messenger.Status.StartsWith("Could not save the group setting", StringComparison.Ordinal));

        Assert.IsFalse(messenger.IsGroupInitiationOn);
        Assert.AreEqual(GroupInitiation.Off, messenger.ActiveGroup!.Initiation);
        Assert.AreEqual(GroupInitiation.Off, (await service.GetAsync(created.Group!.Id, TestContext.CancellationToken))!.Initiation);
    }

    private static MessengerViewModel CreateViewModel(TestDatabase database, FakeSpeech? speech = null)
    {
        return new MessengerViewModel(
            database.Services.GetRequiredService<IConversationService>(),
            database.Services.GetRequiredService<IContactService>(),
            database.Services.GetRequiredService<IMemoryService>(), speech);
    }

    private async Task WaitUntilAsync(Func<bool> condition)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();

        while (!condition())
        {
            if (stopwatch.Elapsed > TimeSpan.FromSeconds(10))
            {
                Assert.Fail("Timed out waiting for the messenger to update.");
            }

            await Task.Delay(10, TestContext.CancellationToken);
        }
    }
}
