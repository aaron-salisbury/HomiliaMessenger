using HomiliaMessenger.Business.Modules.Agents;
using HomiliaMessenger.Business.Modules.Contacts;
using HomiliaMessenger.Business.Modules.Conversations;
using HomiliaMessenger.Business.Modules.Profile;
using HomiliaMessenger.Integrations.LocalModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Tests.Agents;

[TestClass]
public sealed class LocalModelTests
{
    private const string BASE_URL = "https://models.example/tiny/";
    private static readonly byte[] WEIGHTS = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("weights ", 5000)));
    private static readonly byte[] CONFIG = Encoding.UTF8.GetBytes("{ \"model\": {} }");

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task DownloadAsync_DownloadsAndVerifiesEveryFile()
    {
        using TestFolder folder = new();
        FakeServer server = new();
        LocalModelLibrary library = CreateLibrary(folder.Path, server, TinyModel());
        List<LocalModelDownloadProgress> reports = [];

        bool downloadedBefore = library.List().Single().IsDownloaded;
        LocalModelDownloadResult result = await library.DownloadAsync("TINY", new SynchronousProgress(reports.Add), TestContext.CancellationToken);

        Assert.IsFalse(downloadedBefore);
        Assert.AreEqual(LocalModelDownloadStatus.Success, result.Status);
        Assert.IsTrue(library.List().Single().IsDownloaded);
        Assert.AreSequenceEqual(WEIGHTS, File.ReadAllBytes(Path.Combine(folder.Path, "tiny", "model.onnx.data")));
        Assert.IsFalse(Directory.Exists(Path.Combine(folder.Path, "tiny.download")));
        Assert.AreEqual(WEIGHTS.Length + CONFIG.Length, reports[^1].BytesDownloaded);
    }

    [TestMethod]
    public async Task DownloadAsync_ResumesAPartialFile()
    {
        using TestFolder folder = new();
        FakeServer server = new();
        LocalModelLibrary library = CreateLibrary(folder.Path, server, TinyModel());
        string partial = Path.Combine(folder.Path, "tiny.download");
        Directory.CreateDirectory(partial);
        File.WriteAllBytes(Path.Combine(partial, "model.onnx.data"), WEIGHTS[..1000]);

        LocalModelDownloadResult result = await library.DownloadAsync("tiny", null, TestContext.CancellationToken);

        Assert.AreEqual(LocalModelDownloadStatus.Success, result.Status);
        Assert.AreEqual(1000, server.RangeStarts["model.onnx.data"]);
        Assert.AreSequenceEqual(WEIGHTS, File.ReadAllBytes(Path.Combine(folder.Path, "tiny", "model.onnx.data")));
    }

    [TestMethod]
    public async Task DownloadAsync_DiscardsAFileThatDoesNotMatchItsChecksum()
    {
        using TestFolder folder = new();
        FakeServer server = new() { Corrupt = true };
        LocalModelLibrary library = CreateLibrary(folder.Path, server, TinyModel());

        LocalModelDownloadResult result = await library.DownloadAsync("tiny", null, TestContext.CancellationToken);

        Assert.AreEqual(LocalModelDownloadStatus.Failed, result.Status);
        Assert.Contains("checksum", result.Problem!);
        Assert.IsFalse(library.List().Single().IsDownloaded);
        Assert.IsFalse(File.Exists(Path.Combine(folder.Path, "tiny.download", "model.onnx.data")));
    }

    [TestMethod]
    public async Task DownloadAsync_ReportsAServerErrorAsAFailure()
    {
        using TestFolder folder = new();
        FakeServer server = new() { Status = HttpStatusCode.NotFound };
        LocalModelLibrary library = CreateLibrary(folder.Path, server, TinyModel());

        LocalModelDownloadResult result = await library.DownloadAsync("tiny", null, TestContext.CancellationToken);
        LocalModelDownloadResult unknown = await library.DownloadAsync("nope", null, TestContext.CancellationToken);

        Assert.AreEqual(LocalModelDownloadStatus.Failed, result.Status);
        Assert.AreEqual(LocalModelDownloadStatus.Failed, unknown.Status);
    }

    [TestMethod]
    public async Task DeleteAsync_RemovesADownloadedModel()
    {
        using TestFolder folder = new();
        LocalModelLibrary library = CreateLibrary(folder.Path, new FakeServer(), TinyModel());
        await library.DownloadAsync("tiny", null, TestContext.CancellationToken);

        bool deleted = await library.DeleteAsync("tiny", TestContext.CancellationToken);
        bool deletedAgain = await library.DeleteAsync("tiny", TestContext.CancellationToken);

        Assert.IsTrue(deleted);
        Assert.IsFalse(deletedAgain);
        Assert.IsFalse(library.List().Single().IsDownloaded);
    }

    [TestMethod]
    public async Task OnnxAgent_IsUnavailableUntilItsModelIsDownloaded()
    {
        using TestFolder folder = new();
        LocalModelLibrary library = CreateLibrary(folder.Path, new FakeServer(), TinyModel());
        using OnnxModelHost host = new();
        LocalConversationAgent agent = new(library, host, NullLogger<LocalConversationAgent>.Instance);

        AgentReply notDownloaded = await agent.ReplyAsync(new AgentRequest("tiny", "Be Walt.", []), TestContext.CancellationToken);
        AgentReply unknown = await agent.ReplyAsync(new AgentRequest("huge", "Be Walt.", []), TestContext.CancellationToken);

        Assert.AreEqual(ReplyStatus.Unavailable, notDownloaded.Status);
        Assert.Contains("Settings", notDownloaded.Problem!);
        Assert.AreEqual(ReplyStatus.Unavailable, unknown.Status);
        Assert.Contains("tiny", unknown.Problem!);
    }

    [TestMethod]
    public void OnnxAgent_SendsInstructionsAsTheSystemMessage()
    {
        AgentRequest request = new("tiny", "Be Walt.", [new AgentTurn(MessageSender.User, "Hi"), new AgentTurn(MessageSender.Contact, "Hey")]);

        string json = JsonSerializer.Serialize(LocalConversationAgent.ToMessages(request));

        Assert.AreEqual("""[{"role":"system","content":"Be Walt."},{"role":"user","content":"Hi"},{"role":"assistant","content":"Hey"}]""", json);
    }

    /// <summary>
    /// Runs the real Phi-3.5 mini model when it has been downloaded on this machine, to check that a persona reply comes
    /// back in a reasonable time. Skipped otherwise, since the model is a 2.8 GB download.
    /// </summary>
    [TestMethod]
    [TestCategory("LocalModel")]
    public async Task OnnxAgent_RepliesInCharacterWithPhi35Mini()
    {
        string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HomiliaMessenger");
        LocalModelLibrary library = new(LocalModelCatalog.Models, Path.Combine(appData, "Models"), new FakeServer(), new OnnxModelHost(), NullLogger<LocalModelLibrary>.Instance);

        if (!library.IsDownloaded(library.Find(LocalModelCatalog.PHI_3_5_MINI)!))
        {
            Assert.Inconclusive("Phi-3.5 mini is not downloaded on this machine.");
        }

        using OnnxModelHost host = new();
        LocalConversationAgent agent = new(library, host, NullLogger<LocalConversationAgent>.Instance);
        Contact walt = new() { Id = ContactId.New(), GivenName = "Walt", Birthday = new DateOnly(1948, 3, 14), Personality = "Gruff but warm grandfather." };
        AgentRequest request = new(LocalModelCatalog.PHI_3_5_MINI, $"You are {walt.DisplayName}, texting your grandkid. Reply in one short sentence.", [new AgentTurn(MessageSender.User, "Hey Grandpa, how's the knee?")]);

        Stopwatch stopwatch = Stopwatch.StartNew();
        AgentReply reply = await agent.ReplyAsync(request, TestContext.CancellationToken);
        TestContext.WriteLine($"{stopwatch.Elapsed.TotalSeconds:0.0}s (including load): {reply.Text}");

        Assert.AreEqual(ReplyStatus.Success, reply.Status, reply.Problem);
        Assert.IsFalse(string.IsNullOrWhiteSpace(reply.Text));
        Assert.IsLessThan(TimeSpan.FromSeconds(60), stopwatch.Elapsed);
    }

    /// <summary>
    /// Two contacts on the same model, as in a group, take turns on one loaded copy of it: each reply has its own persona
    /// and prompt, a reply canceled partway leaves the model usable for the next contact, and the weights are loaded
    /// once. Runs the real Phi-3.5 mini model when it has been downloaded on this machine.
    /// </summary>
    [TestMethod]
    [TestCategory("LocalModel")]
    public async Task OnnxAgent_ContactsOnTheSameModelShareOneLoadedCopy()
    {
        string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HomiliaMessenger");
        LocalModelLibrary library = new(LocalModelCatalog.Models, Path.Combine(appData, "Models"), new FakeServer(), new OnnxModelHost(), NullLogger<LocalModelLibrary>.Instance);

        if (!library.IsDownloaded(library.Find(LocalModelCatalog.PHI_3_5_MINI)!))
        {
            Assert.Inconclusive("Phi-3.5 mini is not downloaded on this machine.");
        }

        using OnnxModelHost host = new();
        LocalConversationAgent agent = new(library, host, NullLogger<LocalConversationAgent>.Instance);
        AgentRequest marcus = new(LocalModelCatalog.PHI_3_5_MINI, "You are Marcus, a gruff motorcycle mechanic in a group chat. Reply in one short sentence.", [new AgentTurn(MessageSender.User, "Aaron: How's the shop, Marcus?")]);
        AgentRequest elena = new(LocalModelCatalog.PHI_3_5_MINI, "You are Elena, a cheerful chemistry teacher in a group chat. Reply in one short sentence.", [new AgentTurn(MessageSender.User, "Aaron: How was class, Elena?")]);

        AgentReply first = await agent.ReplyAsync(marcus, TestContext.CancellationToken);

        // Elena's reply is canceled partway, as when the user writes again mid-round.
        using (CancellationTokenSource cancel = new(TimeSpan.FromMilliseconds(300)))
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => agent.ReplyAsync(elena, cancel.Token));
        }

        AgentReply second = await agent.ReplyAsync(elena, TestContext.CancellationToken);
        AgentReply third = await agent.ReplyAsync(marcus, TestContext.CancellationToken);
        TestContext.WriteLine($"Marcus: {first.Text}\nElena: {second.Text}\nMarcus: {third.Text}");

        Assert.AreEqual(ReplyStatus.Success, first.Status, first.Problem);
        Assert.AreEqual(ReplyStatus.Success, second.Status, second.Problem);
        Assert.AreEqual(ReplyStatus.Success, third.Status, third.Problem);
        Assert.AreEqual(1, host.LoadCount, "The model's weights were loaded once for both contacts.");
    }

    /// <summary>
    /// A round of a group with two personas on the real Phi-3.5 mini model, through the whole conversation flow: each
    /// message is the speaker's own, never written as someone else. Runs when the model has been downloaded.
    /// </summary>
    [TestMethod]
    [TestCategory("LocalModel")]
    public async Task GroupRound_WithPhi35Mini_GivesEachContactTheirOwnMessages()
    {
        string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HomiliaMessenger");
        LocalModelLibrary library = new(LocalModelCatalog.Models, Path.Combine(appData, "Models"), new FakeServer(), new OnnxModelHost(), NullLogger<LocalModelLibrary>.Instance);

        if (!library.IsDownloaded(library.Find(LocalModelCatalog.PHI_3_5_MINI)!))
        {
            Assert.Inconclusive("Phi-3.5 mini is not downloaded on this machine.");
        }

        using OnnxModelHost host = new();
        LocalConversationAgent agent = new(library, host, NullLogger<LocalConversationAgent>.Instance);
        using TestDatabase database = await Conversations.GroupTestKit.CreateDatabaseAsync(TestContext, agents: agent);
        IConversationService service = database.Services.GetRequiredService<IConversationService>();
        await database.Services.GetRequiredService<IUserProfileService>().SaveAsync(new UserProfile { Name = "Aaron" }, TestContext.CancellationToken);
        Contact marcus = await SaveLocalContactAsync(database, "Marcus", "Gruff, dry-humored motorcycle mechanic.", "Coworker");
        Contact elena = await SaveLocalContactAsync(database, "Elena", "Talkative, curious high school chemistry teacher.", "Sister");
        GroupCreationResult group = await service.CreateGroupAsync([marcus.Id, elena.Id], TestContext.CancellationToken);

        await service.SendAsync(group.Group!.Id, "What are you two doing this weekend?", TestContext.CancellationToken);
        IReadOnlyList<ChatMessage> replies = await Conversations.GroupTestKit.RunRoundAsync(service, group.Group.Id, GroupTrigger.UserMessage, TestContext.CancellationToken);

        foreach (ChatMessage reply in replies)
        {
            TestContext.WriteLine($"{(reply.SenderContactId == marcus.Id ? "Marcus" : "Elena")}: {reply.Text}");
        }

        Assert.IsGreaterThanOrEqualTo(2, replies.Count);
        Assert.IsTrue(replies.All(reply => !reply.Text.StartsWith("Marcus:", StringComparison.Ordinal) && !reply.Text.StartsWith("Elena:", StringComparison.Ordinal) && !reply.Text.StartsWith("Aaron:", StringComparison.Ordinal)));
        Assert.AreEqual(1, host.LoadCount);
    }

    private async Task<Contact> SaveLocalContactAsync(TestDatabase database, string name, string personality, string relationship)
    {
        Contact contact = new() { Id = ContactId.New(), GivenName = name, Birthday = new DateOnly(1985, 5, 5), Personality = personality, Relationship = relationship, Agent = new AgentAssignment(LocalModelLibrary.PROVIDER, LocalModelCatalog.PHI_3_5_MINI) };
        await database.Services.GetRequiredService<IContactService>().SaveAsync(contact, TestContext.CancellationToken);

        return contact;
    }

    [TestMethod]
    public async Task OnnxHost_ReportsAMissingModelFolder()
    {
        using OnnxModelHost host = new();
        string missing = Path.Combine(Path.GetTempPath(), $"HomiliaMessenger-missing-{Guid.NewGuid():N}");

        await Assert.ThrowsExactlyAsync<DirectoryNotFoundException>(() =>
            host.GenerateAsync(missing, 512, [new ChatTemplateMessage("user", "Hi")], new AgentRequest("tiny", string.Empty, []), new GenerationSettings(), TestContext.CancellationToken));
        Assert.AreEqual(0, host.LoadCount);
    }

    /// <summary>
    /// Shutting down while a reply is being generated stops it, then releases the model, instead of freeing the model
    /// while it is still in use.
    /// </summary>
    [TestMethod]
    [TestCategory("LocalModel")]
    public async Task OnnxHost_StopsAGenerationInProgressWhenDisposed()
    {
        string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HomiliaMessenger");
        LocalModelLibrary library = new(LocalModelCatalog.Models, Path.Combine(appData, "Models"), new FakeServer(), new OnnxModelHost(), NullLogger<LocalModelLibrary>.Instance);
        LocalModelDefinition model = library.Find(LocalModelCatalog.PHI_3_5_MINI)!;

        if (!library.IsDownloaded(model))
        {
            Assert.Inconclusive("Phi-3.5 mini is not downloaded on this machine.");
        }

        OnnxModelHost host = new();
        Task<string?> generating = host.GenerateAsync(library.ModelDirectory(model), model.ContextTokens,
            [new ChatTemplateMessage("user", "Write a long story about a garden.")], new AgentRequest(model.Id, string.Empty, []), new GenerationSettings(MaxReplyTokens: 1000), TestContext.CancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(6), TestContext.CancellationToken);

        await host.DisposeAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => generating);
    }

    private static LocalModelDefinition TinyModel()
    {
        return new LocalModelDefinition("tiny", "Tiny", "A test model.", "MIT", 512, BASE_URL,
        [
            new("genai_config.json", CONFIG.Length, Convert.ToHexString(SHA256.HashData(CONFIG))),
            new("model.onnx.data", WEIGHTS.Length, Convert.ToHexString(SHA256.HashData(WEIGHTS)))
        ]);
    }

    private static LocalModelLibrary CreateLibrary(string directory, FakeServer server, LocalModelDefinition model)
    {
        return new LocalModelLibrary([model], directory, server, new OnnxModelHost(), NullLogger<LocalModelLibrary>.Instance);
    }

    /// <summary>
    /// Reports progress inline, unlike <see cref="Progress{T}"/>, which posts to the thread pool.
    /// </summary>
    private sealed class SynchronousProgress : IProgress<LocalModelDownloadProgress>
    {
        private readonly Action<LocalModelDownloadProgress> _report;

        public SynchronousProgress(Action<LocalModelDownloadProgress> report)
        {
            _report = report;
        }

        public void Report(LocalModelDownloadProgress value)
        {
            _report(value);
        }
    }

    /// <summary>
    /// Serves the tiny model's files, honoring range requests.
    /// </summary>
    private sealed class FakeServer : HttpMessageHandler, IHttpClientFactory
    {
        public bool Corrupt { get; init; }
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public Dictionary<string, long> RangeStarts { get; } = [];

        public HttpClient CreateClient(string name)
        {
            return new HttpClient(this, disposeHandler: false);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Status != HttpStatusCode.OK)
            {
                return Task.FromResult(new HttpResponseMessage(Status));
            }

            string name = request.RequestUri!.AbsoluteUri[BASE_URL.Length..];
            byte[] content = name == "model.onnx.data" ? WEIGHTS : CONFIG;

            if (Corrupt && name == "model.onnx.data")
            {
                content = [.. content];
                content[^1] ^= 0xFF;
            }

            long start = request.Headers.Range?.Ranges.Single().From ?? 0;

            if (start > 0)
            {
                RangeStarts[name] = start;
            }

            HttpResponseMessage response = new(start > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content[(int)start..])
            };

            if (start > 0)
            {
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, content.Length - 1, content.Length);
            }

            return Task.FromResult(response);
        }
    }

    private sealed class TestFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"HomiliaMessenger-models-{Guid.NewGuid():N}");

        public TestFolder()
        {
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
