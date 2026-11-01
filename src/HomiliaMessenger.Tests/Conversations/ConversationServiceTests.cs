using FluentValidation.Results;
using HomiliaMessenger.Business.Modules.Contacts;
using HomiliaMessenger.Business.Modules.Conversations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace HomiliaMessenger.Tests.Conversations;

[TestClass]
public sealed class ConversationServiceTests
{
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// A database whose simulated contacts reply after <paramref name="replyDelay"/>, instantly by default.
    /// </summary>
    internal static Task<TestDatabase> CreateDatabaseAsync(TestContext testContext, TimeSpan replyDelay = default)
    {
        return TestDatabase.CreateAsync(testContext, services =>
            services.AddSingleton(new SimulatedReplies(new Random(7), TimeProvider.System, replyDelay, replyDelay)));
    }

    internal static async Task<Contact> AddContactAsync(TestDatabase database, string name, TestContext testContext)
    {
        Contact contact = new() { Id = ContactId.New(), GivenName = name, Birthday = new DateOnly(1970, 1, 1) };
        ValidationResult result = await database.Services.GetRequiredService<IContactService>().SaveAsync(contact, testContext.CancellationToken);
        Assert.IsTrue(result.IsValid);

        return contact;
    }

    [TestMethod]
    public async Task SendAsync_StartsTheContactsConversationWithTheMessage()
    {
        using TestDatabase database = await CreateDatabaseAsync(TestContext);
        IConversationService service = database.Services.GetRequiredService<IConversationService>();
        Contact walt = await AddContactAsync(database, "Walt", TestContext);

        SendMessageResult result = await service.SendAsync(walt.Id, "  Hi Walt  ", TestContext.CancellationToken);
        IReadOnlyList<Conversation> conversations = await service.ListAsync(TestContext.CancellationToken);

        Assert.IsTrue(result.Validation.IsValid);
        Assert.IsNotNull(result.Conversation);
        Assert.IsNotNull(result.Message);
        Assert.AreEqual("Hi Walt", result.Message.Text);
        Assert.AreEqual(MessageSender.User, result.Message.Sender);
        Assert.HasCount(1, conversations);
        Assert.AreEqual(walt.Id, conversations[0].ContactId);
        Assert.AreEqual(result.Message, conversations[0].LastMessage);
    }

    [TestMethod]
    public async Task SendAsync_ContinuesTheContactsExistingConversation()
    {
        using TestDatabase database = await CreateDatabaseAsync(TestContext);
        IConversationService service = database.Services.GetRequiredService<IConversationService>();
        Contact walt = await AddContactAsync(database, "Walt", TestContext);

        SendMessageResult first = await service.SendAsync(walt.Id, "One", TestContext.CancellationToken);
        SendMessageResult second = await service.SendAsync(walt.Id, "Two", TestContext.CancellationToken);
        IReadOnlyList<ChatMessage> messages = await service.ListMessagesAsync(first.Conversation!.Id, TestContext.CancellationToken);

        Assert.AreEqual(first.Conversation.Id, second.Conversation!.Id);
        Assert.AreSequenceEqual(new[] { "One", "Two" }, messages.Select(message => message.Text).ToArray());
    }

    [TestMethod]
    public async Task SendAsync_RejectsInvalidMessagesWithoutStartingAConversation()
    {
        using TestDatabase database = await CreateDatabaseAsync(TestContext);
        IConversationService service = database.Services.GetRequiredService<IConversationService>();
        Contact walt = await AddContactAsync(database, "Walt", TestContext);

        SendMessageResult blank = await service.SendAsync(walt.Id, "   ", TestContext.CancellationToken);
        SendMessageResult tooLong = await service.SendAsync(walt.Id, new string('a', ChatMessageValidator.MAX_TEXT_LENGTH + 1), TestContext.CancellationToken);
        SendMessageResult unknownContact = await service.SendAsync(ContactId.New(), "Hello?", TestContext.CancellationToken);
        IReadOnlyList<Conversation> conversations = await service.ListAsync(TestContext.CancellationToken);

        Assert.IsFalse(blank.Validation.IsValid);
        Assert.IsFalse(tooLong.Validation.IsValid);
        Assert.IsFalse(unknownContact.Validation.IsValid);
        Assert.IsNull(blank.Message);
        Assert.IsEmpty(conversations);
    }

    [TestMethod]
    public async Task ReplyAsync_AddsTheContactsReplyAfterTheUsersMessage()
    {
        using TestDatabase database = await CreateDatabaseAsync(TestContext);
        IConversationService service = database.Services.GetRequiredService<IConversationService>();
        Contact walt = await AddContactAsync(database, "Walt", TestContext);

        SendMessageResult sent = await service.SendAsync(walt.Id, "How was your day?", TestContext.CancellationToken);
        ChatMessage? reply = await service.ReplyAsync(sent.Conversation!.Id, TestContext.CancellationToken);
        IReadOnlyList<ChatMessage> messages = await service.ListMessagesAsync(sent.Conversation.Id, TestContext.CancellationToken);
        Conversation? conversation = await service.FindByContactAsync(walt.Id, TestContext.CancellationToken);

        Assert.IsNotNull(reply);
        Assert.AreEqual(MessageSender.Contact, reply.Sender);
        Assert.Contains(reply.Text, SimulatedReplies.Replies);
        Assert.AreSequenceEqual(new[] { MessageSender.User, MessageSender.Contact }, messages.Select(message => message.Sender).ToArray());
        Assert.AreEqual(reply, conversation!.LastMessage);
    }

    [TestMethod]
    public async Task ReplyAsync_ReturnsNullOnceTheConversationIsDeleted()
    {
        using TestDatabase database = await CreateDatabaseAsync(TestContext);
        IConversationService service = database.Services.GetRequiredService<IConversationService>();
        Contact walt = await AddContactAsync(database, "Walt", TestContext);

        SendMessageResult sent = await service.SendAsync(walt.Id, "Hello", TestContext.CancellationToken);
        bool deleted = await service.DeleteAsync(sent.Conversation!.Id, TestContext.CancellationToken);
        bool deletedAgain = await service.DeleteAsync(sent.Conversation.Id, TestContext.CancellationToken);
        ChatMessage? reply = await service.ReplyAsync(sent.Conversation.Id, TestContext.CancellationToken);
        IReadOnlyList<ChatMessage> messages = await service.ListMessagesAsync(sent.Conversation.Id, TestContext.CancellationToken);

        Assert.IsTrue(deleted);
        Assert.IsFalse(deletedAgain);
        Assert.IsNull(reply);
        Assert.IsEmpty(messages);
    }

    [TestMethod]
    public async Task DeletingAContact_DeletesTheirConversation()
    {
        using TestDatabase database = await CreateDatabaseAsync(TestContext);
        IConversationService service = database.Services.GetRequiredService<IConversationService>();
        Contact walt = await AddContactAsync(database, "Walt", TestContext);
        Contact rose = await AddContactAsync(database, "Rose", TestContext);

        await service.SendAsync(walt.Id, "Hi Walt", TestContext.CancellationToken);
        await service.SendAsync(rose.Id, "Hi Rose", TestContext.CancellationToken);
        await database.Services.GetRequiredService<IContactService>().DeleteAsync(walt.Id, TestContext.CancellationToken);
        IReadOnlyList<Conversation> conversations = await service.ListAsync(TestContext.CancellationToken);

        Assert.HasCount(1, conversations);
        Assert.AreEqual(rose.Id, conversations[0].ContactId);
    }

    [TestMethod]
    public async Task ListAsync_OrdersMostRecentlyActiveFirst()
    {
        using TestDatabase database = await CreateDatabaseAsync(TestContext);
        IConversationService service = database.Services.GetRequiredService<IConversationService>();
        Contact walt = await AddContactAsync(database, "Walt", TestContext);
        Contact rose = await AddContactAsync(database, "Rose", TestContext);

        SendMessageResult toWalt = await service.SendAsync(walt.Id, "First", TestContext.CancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(5), TestContext.CancellationToken);
        await service.SendAsync(rose.Id, "Second", TestContext.CancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(5), TestContext.CancellationToken);
        await service.ReplyAsync(toWalt.Conversation!.Id, TestContext.CancellationToken);

        IReadOnlyList<Conversation> conversations = await service.ListAsync(TestContext.CancellationToken);

        Assert.AreSequenceEqual(new[] { walt.Id, rose.Id }, conversations.Select(conversation => conversation.ContactId).ToArray());
    }

    [TestMethod]
    public async Task SimulatedReplies_NeverRepeatTheSameReplyTwiceInARow()
    {
        SimulatedReplies replies = new(new Random(42), TimeProvider.System, TimeSpan.Zero, TimeSpan.Zero);
        string previous = string.Empty;

        for (int i = 0; i < 200; i++)
        {
            string reply = await replies.NextAsync(TestContext.CancellationToken);
            Assert.AreNotEqual(previous, reply);
            previous = reply;
        }
    }
}
