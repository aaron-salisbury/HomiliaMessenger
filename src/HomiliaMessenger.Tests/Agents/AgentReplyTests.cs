using HomiliaMessenger.Business.Modules.Agents;
using HomiliaMessenger.Business.Modules.Contacts;
using HomiliaMessenger.Business.Modules.Conversations;
using HomiliaMessenger.Tests.Conversations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Tests.Agents;

[TestClass]
public sealed class AgentReplyTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ReplyAsync_AsksTheContactsAgentAndRecordsItOnTheReply()
    {
        FakeAgent fake = new("Fake", AgentReply.Success("Walt: Still kicking.\n\n(Note: This reply stays in character.)"));
        using TestDatabase database = await ConversationServiceTests.CreateDatabaseAsync(TestContext, default, fake);
        IConversationService service = database.Services.GetRequiredService<IConversationService>();
        Contact walt = await SaveContactAsync(database, new AgentAssignment(" fake ", " model-x "));

        SendMessageResult sent = await service.SendAsync(walt.Id, "How are you?", TestContext.CancellationToken);
        ConversationReply result = await service.ReplyAsync(sent.Conversation!.Id, TestContext.CancellationToken);
        IReadOnlyList<ChatMessage> stored = await service.ListMessagesAsync(sent.Conversation.Id, TestContext.CancellationToken);

        Assert.AreEqual(ReplyStatus.Success, result.Status);
        Assert.AreEqual("Still kicking.", result.Message!.Text);
        Assert.AreEqual(new AgentAssignment(" fake ", " model-x "), stored[1].Agent);
        Assert.IsNull(stored[0].Agent);
        Assert.IsNotNull(fake.LastRequest);
        Assert.AreEqual("model-x", fake.LastRequest.Model);
        Assert.Contains("You are Walt", fake.LastRequest.Instructions);
        Assert.AreEqual(new AgentTurn(MessageSender.User, "How are you?"), fake.LastRequest.Turns[^1]);
    }

    [TestMethod]
    public async Task ReplyAsync_ReportsAnUnknownProviderWithoutSavingAReply()
    {
        using TestDatabase database = await ConversationServiceTests.CreateDatabaseAsync(TestContext);
        IConversationService service = database.Services.GetRequiredService<IConversationService>();
        Contact walt = await SaveContactAsync(database, new AgentAssignment("Nowhere", "model"));

        SendMessageResult sent = await service.SendAsync(walt.Id, "Hello?", TestContext.CancellationToken);
        ConversationReply result = await service.ReplyAsync(sent.Conversation!.Id, TestContext.CancellationToken);
        IReadOnlyList<ChatMessage> stored = await service.ListMessagesAsync(sent.Conversation.Id, TestContext.CancellationToken);

        Assert.AreEqual(ReplyStatus.Unavailable, result.Status);
        Assert.Contains("Nowhere", result.Problem!);
        Assert.HasCount(1, stored);
    }

    [TestMethod]
    [DataRow(ReplyStatus.Unavailable, "Model not downloaded.")]
    [DataRow(ReplyStatus.InvalidResponse, "Empty reply.")]
    public async Task ReplyAsync_PassesOnTheAgentsProblem(ReplyStatus status, string problem)
    {
        FakeAgent fake = new("Fake", new AgentReply(status, null, problem));
        using TestDatabase database = await ConversationServiceTests.CreateDatabaseAsync(TestContext, default, fake);
        IConversationService service = database.Services.GetRequiredService<IConversationService>();
        Contact walt = await SaveContactAsync(database, new AgentAssignment("Fake", "model"));

        SendMessageResult sent = await service.SendAsync(walt.Id, "Hello?", TestContext.CancellationToken);
        ConversationReply result = await service.ReplyAsync(sent.Conversation!.Id, TestContext.CancellationToken);

        Assert.AreEqual(status, result.Status);
        Assert.AreEqual(problem, result.Problem);
        Assert.IsNull(result.Message);
    }

    [TestMethod]
    public async Task ReplyAsync_TreatsAReplyOfOnlyCommentaryAsInvalid()
    {
        FakeAgent fake = new("Fake", AgentReply.Success("(Note: I would respond warmly here.)"));
        using TestDatabase database = await ConversationServiceTests.CreateDatabaseAsync(TestContext, default, fake);
        IConversationService service = database.Services.GetRequiredService<IConversationService>();
        Contact walt = await SaveContactAsync(database, new AgentAssignment("Fake", "model"));

        SendMessageResult sent = await service.SendAsync(walt.Id, "Hello?", TestContext.CancellationToken);
        ConversationReply result = await service.ReplyAsync(sent.Conversation!.Id, TestContext.CancellationToken);

        Assert.AreEqual(ReplyStatus.InvalidResponse, result.Status);
    }

    private async Task<Contact> SaveContactAsync(TestDatabase database, AgentAssignment agent)
    {
        Contact contact = new() { Id = ContactId.New(), GivenName = "Walt", Birthday = new DateOnly(1948, 3, 14), Agent = agent };
        await database.Services.GetRequiredService<IContactService>().SaveAsync(contact, TestContext.CancellationToken);

        return contact;
    }

    private sealed class FakeAgent : IConversationAgent
    {
        private readonly AgentReply _reply;

        public FakeAgent(string provider, AgentReply reply)
        {
            Provider = provider;
            _reply = reply;
        }

        public string Provider { get; }

        public AgentRequest? LastRequest { get; private set; }

        public Task<AgentReply> ReplyAsync(AgentRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(_reply);
        }
    }
}
