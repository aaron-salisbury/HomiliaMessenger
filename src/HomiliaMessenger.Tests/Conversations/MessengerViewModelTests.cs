using HomiliaMessenger.Business.Modules.Contacts;
using HomiliaMessenger.Business.Modules.Conversations;
using HomiliaMessenger.Tests.Speech;
using HomiliaMessenger.Presentation.Desktop.Models;
using HomiliaMessenger.Presentation.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace HomiliaMessenger.Tests.Conversations;

[TestClass]
public sealed class MessengerViewModelTests
{
    private static readonly DateTimeOffset NOON = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void MessageItem_GroupsConsecutiveMessagesFromTheSameSender()
    {
        MessageItem first = MessageItem.Follow(null, Message(MessageSender.User, NOON), NOON);
        MessageItem second = MessageItem.Follow(first, Message(MessageSender.User, NOON.AddMinutes(1)), NOON);
        MessageItem reply = MessageItem.Follow(second, Message(MessageSender.Contact, NOON.AddMinutes(2)), NOON);
        MessageItem later = MessageItem.Follow(reply, Message(MessageSender.Contact, NOON.AddMinutes(2) + MessageItem.GROUP_GAP + TimeSpan.FromSeconds(1)), NOON);

        Assert.IsTrue(first.IsFirstInGroup);
        Assert.IsFalse(first.IsLastInGroup);
        Assert.IsFalse(second.IsFirstInGroup);
        Assert.IsTrue(second.IsLastInGroup);
        Assert.IsTrue(reply.IsFirstInGroup);
        Assert.IsTrue(reply.ShowsAvatar);
        Assert.IsTrue(later.IsFirstInGroup);
        Assert.IsFalse(first.ShowsAvatar);
    }

    [TestMethod]
    public void MessageItem_SeparatesMessagesAfterALongGap()
    {
        MessageItem first = MessageItem.Follow(null, Message(MessageSender.User, NOON), NOON);
        MessageItem soon = MessageItem.Follow(first, Message(MessageSender.Contact, NOON.AddMinutes(10)), NOON);
        MessageItem later = MessageItem.Follow(soon, Message(MessageSender.Contact, NOON.AddMinutes(10) + MessageItem.SEPARATOR_GAP + TimeSpan.FromSeconds(1)), NOON);

        Assert.IsTrue(first.HasTimeSeparator);
        Assert.IsFalse(soon.HasTimeSeparator);
        Assert.IsTrue(later.HasTimeSeparator);
        Assert.IsTrue(soon.IsLastInGroup);
    }

    [TestMethod]
    public async Task SendingToANewContact_StartsAChatAndTheContactReplies()
    {
        using TestDatabase database = await ConversationServiceTests.CreateDatabaseAsync(TestContext);
        Contact walt = await ConversationServiceTests.AddContactAsync(database, "Walt", TestContext);
        MessengerViewModel messenger = CreateViewModel(database);

        await messenger.InitializeAsync();
        await messenger.StartChatCommand.ExecuteAsync(walt);
        bool canSendBlank = messenger.SendCommand.CanExecute(null);
        messenger.DraftText = "Hey Walt";
        await messenger.SendCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => messenger.Messages.Count == 2 && !messenger.IsContactTyping);

        Assert.IsFalse(canSendBlank);
        Assert.AreEqual(string.Empty, messenger.DraftText);
        Assert.IsTrue(messenger.Messages[0].IsFromUser);
        Assert.IsFalse(messenger.Messages[1].IsFromUser);
        Assert.HasCount(1, messenger.Conversations);
        Assert.AreSame(messenger.Conversations[0], messenger.SelectedConversation);
        Assert.AreEqual(messenger.Messages[1].Text, messenger.Conversations[0].Preview);
    }

    [TestMethod]
    public async Task WritingAgainWhileTheContactReplies_GetsOneReplyToBothMessages()
    {
        using TestDatabase database = await ConversationServiceTests.CreateDatabaseAsync(TestContext, TimeSpan.FromMilliseconds(400));
        Contact walt = await ConversationServiceTests.AddContactAsync(database, "Walt", TestContext);
        MessengerViewModel messenger = CreateViewModel(database);

        await messenger.InitializeAsync();
        await messenger.StartChatCommand.ExecuteAsync(walt);
        messenger.DraftText = "Hey";
        await messenger.SendCommand.ExecuteAsync(null);
        await Task.Delay(100, TestContext.CancellationToken);
        messenger.DraftText = "Are you there?";
        await messenger.SendCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => messenger.Messages.Count == 3 && !messenger.IsContactTyping);
        await Task.Delay(800, TestContext.CancellationToken);

        IReadOnlyList<ChatMessage> stored = await database.Services.GetRequiredService<IConversationService>()
            .ListMessagesAsync(messenger.ActiveConversationId!.Value, TestContext.CancellationToken);
        Assert.AreSequenceEqual(new[] { MessageSender.User, MessageSender.User, MessageSender.Contact }, stored.Select(message => message.Sender).ToArray());
        Assert.HasCount(3, messenger.Messages);
    }

    [TestMethod]
    public async Task SwitchingChats_KeepsEachChatsDraft()
    {
        using TestDatabase database = await ConversationServiceTests.CreateDatabaseAsync(TestContext);
        Contact walt = await ConversationServiceTests.AddContactAsync(database, "Walt", TestContext);
        Contact rose = await ConversationServiceTests.AddContactAsync(database, "Rose", TestContext);
        MessengerViewModel messenger = CreateViewModel(database);

        await messenger.InitializeAsync();
        await messenger.StartChatCommand.ExecuteAsync(walt);
        messenger.DraftText = "Half a thought";
        await messenger.StartChatCommand.ExecuteAsync(rose);
        string roseDraft = messenger.DraftText;
        await messenger.StartChatCommand.ExecuteAsync(walt);

        Assert.AreEqual(string.Empty, roseDraft);
        Assert.AreEqual("Half a thought", messenger.DraftText);
    }

    [TestMethod]
    public async Task SearchingByMessageText_UpdatesAsNewMessagesArrive()
    {
        // Slow enough replies that the search applies before the reply arrives.
        using TestDatabase database = await ConversationServiceTests.CreateDatabaseAsync(TestContext, TimeSpan.FromMilliseconds(500));
        Contact walt = await ConversationServiceTests.AddContactAsync(database, "Walt", TestContext);
        MessengerViewModel messenger = CreateViewModel(database);

        await messenger.InitializeAsync();
        await messenger.StartChatCommand.ExecuteAsync(walt);
        messenger.DraftText = "zebra crossing";
        await messenger.SendCommand.ExecuteAsync(null);
        messenger.SearchText = "zebra";
        int matchesBeforeReply = messenger.VisibleConversations.Count;
        await WaitUntilAsync(() => messenger.Messages.Count == 2 && !messenger.IsContactTyping);

        Assert.AreEqual(1, matchesBeforeReply);
        Assert.IsEmpty(messenger.VisibleConversations);
        Assert.HasCount(1, messenger.Conversations);
    }

    [TestMethod]
    public async Task DeletingTheOpenChat_ClosesItAfterConfirmation()
    {
        using TestDatabase database = await ConversationServiceTests.CreateDatabaseAsync(TestContext);
        Contact walt = await ConversationServiceTests.AddContactAsync(database, "Walt", TestContext);
        await database.Services.GetRequiredService<IConversationService>().SendAsync(walt.Id, "Hello", TestContext.CancellationToken);
        MessengerViewModel messenger = CreateViewModel(database);
        bool confirm = false;
        messenger.ConfirmAsync = (_, _) => Task.FromResult(confirm);

        await messenger.InitializeAsync();
        messenger.SelectedConversation = messenger.VisibleConversations.Single();
        await WaitUntilAsync(() => messenger.Messages.Count == 1);

        await messenger.DeleteConversationCommand.ExecuteAsync(null);
        int afterDeclining = messenger.Conversations.Count;
        confirm = true;
        await messenger.DeleteConversationCommand.ExecuteAsync(null);

        Assert.AreEqual(1, afterDeclining);
        Assert.IsEmpty(messenger.Conversations);
        Assert.IsFalse(messenger.HasActiveThread);
        Assert.IsEmpty(messenger.Messages);
        Assert.IsEmpty(await database.Services.GetRequiredService<IConversationService>().ListAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task Speech_StartsOffAndOnlyVoicesNewRepliesAfterOptingIn()
    {
        using TestDatabase database = await ConversationServiceTests.CreateDatabaseAsync(TestContext);
        IContactService contacts = database.Services.GetRequiredService<IContactService>();
        Contact walt = (await ConversationServiceTests.AddContactAsync(database, "Walt", TestContext)) with { VoiceId = "am_adam" };
        await contacts.SaveAsync(walt, TestContext.CancellationToken);
        FakeSpeech speech = new();
        MessengerViewModel messenger = CreateViewModel(database, speech);
        await messenger.InitializeAsync();
        await messenger.StartChatCommand.ExecuteAsync(walt);
        Assert.IsTrue(messenger.CanVoiceReplies);
        Assert.IsFalse(messenger.IsSpeechOn);

        messenger.DraftText = "Hello";
        await messenger.SendCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => messenger.Messages.Count == 2 && !messenger.IsContactTyping);
        Assert.IsEmpty(speech.Spoken);
        messenger.ToggleSpeechCommand.Execute(null);
        Assert.IsEmpty(speech.Spoken); // Turning on speech never reads existing history.
        messenger.DraftText = "How are you?";
        await messenger.SendCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => speech.Spoken.Count == 1);
        Assert.AreEqual(messenger.Messages[^1].Text, speech.Spoken[0].Text);
        Assert.AreEqual("am_adam", speech.Spoken[0].Voice);

        MessengerViewModel relaunched = CreateViewModel(database, speech);
        await relaunched.InitializeAsync();
        await relaunched.StartChatCommand.ExecuteAsync(walt);
        Assert.IsFalse(relaunched.IsSpeechOn);
        Assert.HasCount(1, speech.Spoken);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Speech_StopsPlaybackWhenSwitchedOffOrTheChatChanges(bool switchChat)
    {
        using TestDatabase database = await ConversationServiceTests.CreateDatabaseAsync(TestContext);
        Contact walt = (await ConversationServiceTests.AddContactAsync(database, "Walt", TestContext)) with { VoiceId = "am_adam" };
        Contact rose = await ConversationServiceTests.AddContactAsync(database, "Rose", TestContext);
        await database.Services.GetRequiredService<IContactService>().SaveAsync(walt, TestContext.CancellationToken);
        FakeSpeech speech = new() { HoldPlayback = true };
        MessengerViewModel messenger = CreateViewModel(database, speech);
        await messenger.InitializeAsync();
        await messenger.StartChatCommand.ExecuteAsync(walt);
        messenger.ToggleSpeechCommand.Execute(null);
        messenger.DraftText = "Hi";
        await messenger.SendCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => speech.Spoken.Count == 1);
        if (switchChat)
        {
            await messenger.StartChatCommand.ExecuteAsync(rose);
        }
        else
        {
            messenger.ToggleSpeechCommand.Execute(null);
        }

        await speech.Canceled.Task.WaitAsync(TestContext.CancellationToken);
        Assert.IsFalse(messenger.IsSpeechOn);
    }

    [TestMethod]
    public async Task Speech_AFailureKeepsTheTextAndSwitchesSpeechOff()
    {
        using TestDatabase database = await ConversationServiceTests.CreateDatabaseAsync(TestContext);
        Contact walt = (await ConversationServiceTests.AddContactAsync(database, "Walt", TestContext)) with { VoiceId = "am_adam" };
        await database.Services.GetRequiredService<IContactService>().SaveAsync(walt, TestContext.CancellationToken);
        FakeSpeech speech = new() { FailPlayback = true };
        MessengerViewModel messenger = CreateViewModel(database, speech);
        await messenger.InitializeAsync();
        await messenger.StartChatCommand.ExecuteAsync(walt);
        messenger.ToggleSpeechCommand.Execute(null);
        messenger.DraftText = "Hi";
        await messenger.SendCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => messenger.Messages.Count == 2 && !messenger.IsContactTyping);
        Assert.IsFalse(messenger.IsSpeechOn);
        Assert.Contains("Audio device unavailable", messenger.Status);
        Assert.IsFalse(string.IsNullOrWhiteSpace(messenger.Messages[^1].Text));
    }

    [TestMethod]
    public async Task Speech_RequiresBothAnAvailableModelAndAContactVoice()
    {
        using TestDatabase database = await ConversationServiceTests.CreateDatabaseAsync(TestContext);
        Contact walt = await ConversationServiceTests.AddContactAsync(database, "Walt", TestContext);
        FakeSpeech speech = new();
        MessengerViewModel messenger = CreateViewModel(database, speech);
        await messenger.InitializeAsync();
        await messenger.StartChatCommand.ExecuteAsync(walt);
        Assert.IsFalse(messenger.CanVoiceReplies);
        messenger.ToggleSpeechCommand.Execute(null);
        Assert.IsFalse(messenger.IsSpeechOn);

        speech.Model = speech.Model with { IsEnabled = false };
        await messenger.StartChatCommand.ExecuteAsync(walt with { Id = ContactId.New(), VoiceId = "am_adam" });
        messenger.ToggleSpeechCommand.Execute(null);
        Assert.IsFalse(messenger.CanVoiceReplies);
        Assert.IsFalse(messenger.IsSpeechOn);
        Assert.IsEmpty(speech.Spoken);
    }

    private static MessengerViewModel CreateViewModel(TestDatabase database, FakeSpeech? speech = null)
    {
        return new MessengerViewModel(
            database.Services.GetRequiredService<IConversationService>(),
            database.Services.GetRequiredService<IContactService>(), speech);
    }

    private static ChatMessage Message(MessageSender sender, DateTimeOffset sentAtUtc)
    {
        return new ChatMessage
        {
            Id = MessageId.New(),
            ConversationId = new ConversationId(Guid.Empty),
            Sender = sender,
            Text = "Text",
            SentAtUtc = sentAtUtc
        };
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
