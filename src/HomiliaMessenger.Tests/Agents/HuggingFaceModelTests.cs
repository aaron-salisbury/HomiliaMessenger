using HomiliaMessenger.Business.Modules.Agents;
using HomiliaMessenger.Business.Modules.Conversations;
using HomiliaMessenger.Business.Modules.Credentials;
using HomiliaMessenger.Integrations.LocalModels;
using HomiliaMessenger.Presentation.Desktop.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Tests.Agents;

[TestClass]
public sealed class HuggingFaceModelTests
{
    private const string REPO = "Qwen/Tiny-Chat-GGUF";
    private const string REVISION = "abc123";
    private static readonly byte[] SMALL = Encoding.UTF8.GetBytes("small gguf weights");
    private static readonly byte[] MEDIUM = Encoding.UTF8.GetBytes("medium gguf weights, a bit longer");

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow("Qwen/Qwen2.5-1.5B-Instruct-GGUF", "Qwen/Qwen2.5-1.5B-Instruct-GGUF")]
    [DataRow("  https://huggingface.co/bartowski/Llama-3.2-3B-Instruct-GGUF/tree/main ", "bartowski/Llama-3.2-3B-Instruct-GGUF")]
    [DataRow("huggingface.co/owner/name", "owner/name")]
    [DataRow("just-a-name", null)]
    [DataRow("owner/../etc", null)]
    public void ParseRepository_AcceptsANameOrAddress(string text, string? expected)
    {
        Assert.AreEqual(expected, HuggingFaceClient.ParseRepository(text));
    }

    [TestMethod]
    [DataRow("qwen2.5-1.5b-instruct-q4_k_m.gguf", "Q4_K_M")]
    [DataRow("Llama-3.2-3B-Instruct-IQ4_XS.gguf", "IQ4_XS")]
    [DataRow("Llama-3.2-3B-Instruct-Q4_0_4_4.gguf", "Q4_0_4_4")]
    [DataRow("model-f16.gguf", "F16")]
    [DataRow("qwen2.5-1.5b-instruct-fp16.gguf", "FP16")]
    [DataRow("Qwen2.5-chat.gguf", null)]
    public void Quantization_ReadsTheLabelFromTheFileName(string name, string? expected)
    {
        Assert.AreEqual(expected, HuggingFaceClient.Quantization(name));
    }

    [TestMethod]
    public void Variants_GroupSplitFilesSkipProjectorsAndRecommendQ4KM()
    {
        HuggingFaceFile[] files =
        [
            new("model-Q8_0-00002-of-00002.gguf", 400, "b"),
            new("model-Q8_0-00001-of-00002.gguf", 500, "a"),
            new("model-Q4_K_M.gguf", 500, "c"),
            new("model-Q2_K.gguf", 300, "d"),
            new("model-Q6_K-00001-of-00003.gguf", 300, "e"),
            new("mmproj-model-f16.gguf", 100, "f"),
            new("README.md", 10, "g")
        ];

        IReadOnlyList<GgufVariant> variants = HuggingFaceClient.Variants(files);

        Assert.AreSequenceEqual(new[] { "Q2_K", "Q4_K_M", "Q8_0" }, variants.Select(variant => variant.Label).ToArray());
        Assert.IsTrue(variants.Single(variant => variant.Label == "Q4_K_M").IsRecommended);
        Assert.ContainsSingle(variant => variant.IsRecommended, variants);
        GgufVariant split = variants.Single(variant => variant.Label == "Q8_0");
        Assert.AreSequenceEqual(new[] { "model-Q8_0-00001-of-00002.gguf", "model-Q8_0-00002-of-00002.gguf" }, split.Files.Select(file => file.Path).ToArray());
        Assert.AreEqual(900, split.DownloadBytes);
    }

    [TestMethod]
    public async Task LookUp_FindsTheVariantsPinnedToTheCurrentRevision()
    {
        HuggingFace server = HuggingFace.WithTinyRepository();
        using TestFolder folder = new();
        LocalModelLibrary library = CreateLibrary(folder.Path, server, new MemoryCredentials());

        HuggingFaceLookup lookup = await library.LookUpAsync("https://huggingface.co/" + REPO, TestContext.CancellationToken);

        Assert.AreEqual(HuggingFaceLookupStatus.Found, lookup.Status, lookup.Problem);
        Assert.AreEqual(REPO, lookup.RepoId);
        Assert.AreEqual(REVISION, lookup.Revision);
        Assert.AreEqual("apache-2.0", lookup.License);
        Assert.AreSequenceEqual(new[] { "Q2_K", "Q4_K_M" }, lookup.Variants.Select(variant => variant.Label).ToArray());
        Assert.Contains($"api/models/{REPO}/tree/{REVISION}?recursive=true", server.Requested);
    }

    [TestMethod]
    public async Task LookUp_FollowsTheFileListAcrossPages()
    {
        HuggingFace server = HuggingFace.WithTinyRepository();
        server.Responses[$"api/models/{REPO}/tree/{REVISION}?recursive=true"] = (HttpStatusCode.OK,
            FileList(("tiny-Q2_K.gguf", SMALL)), $"<https://huggingface.co/api/models/{REPO}/tree/{REVISION}?recursive=true&cursor=next>; rel=\"next\"");
        server.Responses[$"api/models/{REPO}/tree/{REVISION}?recursive=true&cursor=next"] = (HttpStatusCode.OK, FileList(("tiny-Q4_K_M.gguf", MEDIUM)), null);
        using TestFolder folder = new();

        HuggingFaceLookup lookup = await CreateLibrary(folder.Path, server, null).LookUpAsync(REPO, TestContext.CancellationToken);

        Assert.HasCount(2, lookup.Variants);
    }

    [TestMethod]
    public async Task LookUp_ExplainsWhatIsMissing()
    {
        using TestFolder folder = new();
        HuggingFace gated = HuggingFace.WithTinyRepository(gated: true);
        HuggingFace missing = new();
        missing.Responses[$"api/models/{REPO}"] = (HttpStatusCode.Unauthorized, "{}", null);
        HuggingFace empty = HuggingFace.WithTinyRepository();
        empty.Responses[$"api/models/{REPO}/tree/{REVISION}?recursive=true"] = (HttpStatusCode.OK, FileList(("README.md", SMALL)), null);

        HuggingFaceLookup needsToken = await CreateLibrary(folder.Path, gated, new MemoryCredentials()).LookUpAsync(REPO, TestContext.CancellationToken);
        HuggingFaceLookup notFound = await CreateLibrary(folder.Path, missing, null).LookUpAsync(REPO, TestContext.CancellationToken);
        HuggingFaceLookup noModels = await CreateLibrary(folder.Path, empty, null).LookUpAsync(REPO, TestContext.CancellationToken);
        HuggingFaceLookup badName = await CreateLibrary(folder.Path, empty, null).LookUpAsync("not a repo", TestContext.CancellationToken);

        Assert.AreEqual(HuggingFaceLookupStatus.NeedsToken, needsToken.Status);
        Assert.AreEqual(HuggingFaceLookupStatus.NeedsToken, notFound.Status);
        Assert.Contains("gated or private", notFound.Problem!);
        Assert.AreEqual(HuggingFaceLookupStatus.NoModels, noModels.Status);
        Assert.AreEqual(HuggingFaceLookupStatus.NotFound, badName.Status);
    }

    [TestMethod]
    public async Task GatedRepositories_UseTheSavedTokenForLookupsAndDownloads()
    {
        HuggingFace server = HuggingFace.WithTinyRepository(gated: true);
        MemoryCredentials credentials = new();
        using TestFolder folder = new();
        LocalModelLibrary library = CreateLibrary(folder.Path, server, credentials);

        await library.SetHuggingFaceTokenAsync("hf_secret", TestContext.CancellationToken);
        HuggingFaceLookup lookup = await library.LookUpAsync(REPO, TestContext.CancellationToken);
        LocalModelAddResult added = await library.AddAsync(lookup, lookup.Variants[0], TestContext.CancellationToken);
        LocalModelDownloadResult downloaded = await library.DownloadAsync(added.ModelId!, null, TestContext.CancellationToken);

        Assert.AreEqual(HuggingFaceLookupStatus.Found, lookup.Status);
        Assert.AreEqual(LocalModelDownloadStatus.Success, downloaded.Status, downloaded.Problem);
        Assert.IsTrue(server.Authorizations.All(value => value == "Bearer hf_secret"));
        Assert.IsTrue(await library.HasHuggingFaceTokenAsync(TestContext.CancellationToken));

        await library.SetHuggingFaceTokenAsync(null, TestContext.CancellationToken);
        Assert.IsFalse(await library.HasHuggingFaceTokenAsync(TestContext.CancellationToken));
        Assert.IsEmpty(credentials.Values);
    }

    [TestMethod]
    public async Task AddedModels_AreRememberedDownloadedFromThePinnedRevisionAndForgottenWhenRemoved()
    {
        HuggingFace server = HuggingFace.WithTinyRepository();
        using TestFolder folder = new();
        LocalModelLibrary library = CreateLibrary(folder.Path, server, null);
        HuggingFaceLookup lookup = await library.LookUpAsync(REPO, TestContext.CancellationToken);
        GgufVariant q4 = lookup.Variants.Single(variant => variant.Label == "Q4_K_M");

        LocalModelAddResult added = await library.AddAsync(lookup, q4, TestContext.CancellationToken);
        LocalModelAddResult again = await library.AddAsync(lookup, q4, TestContext.CancellationToken);
        LocalModel listed = library.List().Single(model => model.Id == added.ModelId);
        LocalModelDownloadResult downloaded = await library.DownloadAsync(added.ModelId!, null, TestContext.CancellationToken);
        LocalModelLibrary reopened = CreateLibrary(folder.Path, server, null);

        Assert.AreEqual(LocalModelLibrary.ModelId(REPO, "sub folder/tiny-Q4_K_M.gguf"), added.ModelId);
        Assert.StartsWith("tiny-q4_k_m-", added.ModelId!);
        Assert.AreEqual(added.ModelId, again.ModelId);
        Assert.IsTrue(listed.IsUserAdded);
        Assert.AreEqual("Tiny-Chat · Q4_K_M", listed.DisplayName);
        Assert.AreEqual(LocalModelDownloadStatus.Success, downloaded.Status, downloaded.Problem);
        Assert.Contains($"{REPO}/resolve/{REVISION}/sub%20folder/tiny-Q4_K_M.gguf", server.Requested);
        Assert.IsTrue(reopened.List().Single(model => model.Id == added.ModelId).IsDownloaded);
        Assert.AreEqual(LocalModelFormat.Gguf, reopened.Find(added.ModelId)!.Format);

        bool removed = await reopened.DeleteAsync(added.ModelId!, TestContext.CancellationToken);

        Assert.IsTrue(removed);
        Assert.IsNull(reopened.Find(added.ModelId));
        Assert.IsNull(CreateLibrary(folder.Path, server, null).Find(added.ModelId));
        Assert.IsFalse(Directory.Exists(Path.Combine(folder.Path, added.ModelId!)));
    }

    [TestMethod]
    public void ModelId_IsStableForARepositoryAndDistinctAcrossRepositories()
    {
        string first = LocalModelLibrary.ModelId("A/Model-GGUF", "model-Q4_K_M.gguf");

        Assert.AreEqual(first, LocalModelLibrary.ModelId("a/model-gguf", "model-Q4_K_M.gguf"));
        Assert.AreNotEqual(first, LocalModelLibrary.ModelId("B/Model-GGUF", "model-Q4_K_M.gguf"));
        Assert.AreNotEqual(first, LocalModelLibrary.ModelId("A/Model-GGUF", "other/model-Q4_K_M.gguf"));
        Assert.MatchesRegex("^model-q4_k_m-[0-9a-f]{8}$", first);
    }

    [TestMethod]
    [DataRow(28, 12, 2, 1536, 32768, 4096)]
    [DataRow(32, 32, 32, 3072, 131072, 2048)]
    [DataRow(32, 32, 32, 4096, 4096, 1536)]
    [DataRow(22, 32, 4, 2048, 2048, 2048)]
    public void LlamaContext_FitsTheCacheBudgetWithinTheModelsLimit(int layers, int heads, int kvHeads, int embedding, int trained, int expected)
    {
        Assert.AreEqual(expected, LlamaModelHost.ContextTokens(layers, heads, kvHeads, embedding, trained));
    }

    [TestMethod]
    public async Task AddDialog_LooksUpSelectsTheRecommendedVariantAndAddsIt()
    {
        HuggingFace server = HuggingFace.WithTinyRepository();
        using TestFolder folder = new();
        LocalModelLibrary library = CreateLibrary(folder.Path, server, new MemoryCredentials());
        AddLocalModelViewModel dialog = new(library);
        bool closed = false;
        dialog.Added += (_, _) => closed = true;

        await dialog.InitializeAsync();
        bool canAddBeforeLookup = dialog.AddCommand.CanExecute(null);
        dialog.Repository = REPO;
        await dialog.LookUpCommand.ExecuteAsync(null);
        await dialog.AddCommand.ExecuteAsync(null);

        Assert.IsFalse(canAddBeforeLookup);
        Assert.AreEqual("Q4_K_M", dialog.SelectedVariant?.Label);
        Assert.Contains("recommended", dialog.SelectedVariant!.Details);
        Assert.IsTrue(closed);
        Assert.AreEqual(LocalModelLibrary.ModelId(REPO, "sub folder/tiny-Q4_K_M.gguf"), dialog.AddedModelId);
    }

    [TestMethod]
    public async Task AddDialog_IgnoresALookupForARepositoryNoLongerEntered()
    {
        HuggingFace server = HuggingFace.WithTinyRepository();
        server.Delay = TimeSpan.FromMilliseconds(200);
        using TestFolder folder = new();
        AddLocalModelViewModel dialog = new(CreateLibrary(folder.Path, server, null)) { Repository = REPO };

        Task lookingUp = dialog.LookUpCommand.ExecuteAsync(null);
        dialog.Repository = "someone/else-GGUF";
        await lookingUp;

        Assert.IsFalse(dialog.HasVariants);
        Assert.IsFalse(dialog.AddCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task AddDialog_AsksForATokenForGatedRepositoriesAndRetries()
    {
        HuggingFace server = HuggingFace.WithTinyRepository(gated: true);
        using TestFolder folder = new();
        AddLocalModelViewModel dialog = new(CreateLibrary(folder.Path, server, new MemoryCredentials())) { Repository = REPO };

        await dialog.LookUpCommand.ExecuteAsync(null);
        bool askedForToken = dialog.NeedsToken;
        dialog.Token = "hf_secret";
        await dialog.SaveTokenCommand.ExecuteAsync(null);

        Assert.IsTrue(askedForToken);
        Assert.IsFalse(dialog.NeedsToken);
        Assert.IsTrue(dialog.HasSavedToken);
        Assert.IsTrue(dialog.ShowsTokenSettings);
        Assert.IsTrue(dialog.HasVariants);
    }

    /// <summary>
    /// Runs a downloaded GGUF model with llama.cpp, when one has been added on this machine: replies come back, reuse the
    /// cache, stop at speaker labels, and shut down cleanly. Skipped otherwise.
    /// </summary>
    [TestMethod]
    [TestCategory("LocalModel")]
    public async Task LlamaHost_RepliesReusesItsCacheAndStopsAtSpeakerLabels()
    {
        string models = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HomiliaMessenger", "Models");
        LocalModelLibrary library = new(LocalModelCatalog.Models, models, new HuggingFace(), new OnnxModelHost(), NullLogger<LocalModelLibrary>.Instance);
        LocalModelDefinition? model = library.Catalog.FirstOrDefault(candidate => candidate.Format == LocalModelFormat.Gguf && library.IsDownloaded(candidate));

        if (model is null)
        {
            Assert.Inconclusive("No GGUF model has been added and downloaded on this machine.");
        }

        LlamaModelHost host = new(NullLogger<LlamaModelHost>.Instance);
        AgentRequest request = new(model.Id, "You are Walt, texting your grandson. Reply in one short sentence.", [], ["User:", "Aaron:", "\n\n"]);
        List<ChatTemplateMessage> messages = [new("system", request.Instructions), new("user", "Hey Grandpa, how's the knee?")];

        Stopwatch first = Stopwatch.StartNew();
        string? reply = await host.GenerateAsync(library.ModelDirectory(model), model.EntryFile, messages, request, new GenerationSettings(), TestContext.CancellationToken);
        first.Stop();
        messages.Add(new("assistant", reply!));
        messages.Add(new("user", "Want to get lunch Saturday?"));
        Stopwatch second = Stopwatch.StartNew();
        string? next = await host.GenerateAsync(library.ModelDirectory(model), model.EntryFile, messages, request with { Precise = true }, new GenerationSettings(), TestContext.CancellationToken);
        second.Stop();
        await host.DisposeAsync();

        TestContext.WriteLine($"{first.Elapsed.TotalSeconds:0.0}s: {reply}");
        TestContext.WriteLine($"{second.Elapsed.TotalSeconds:0.0}s: {next}");
        Assert.IsFalse(string.IsNullOrWhiteSpace(reply));
        Assert.IsFalse(string.IsNullOrWhiteSpace(next));
        Assert.DoesNotContain("User:", next!);
        Assert.DoesNotContain("\n\n", next!);
    }

    private static LocalModelLibrary CreateLibrary(string directory, HuggingFace server, ICredentialStore? credentials)
    {
        return new LocalModelLibrary([], directory, server, new OnnxModelHost(), NullLogger<LocalModelLibrary>.Instance, null, credentials);
    }

    private static string FileList(params (string Path, byte[] Content)[] files)
    {
        return "[" + string.Join(",", files.Select(file =>
            $$$"""{"type":"file","path":"{{{file.Path}}}","size":{{{file.Content.Length}}},"lfs":{"oid":"{{{Convert.ToHexString(SHA256.HashData(file.Content)).ToLowerInvariant()}}}","size":{{{file.Content.Length}}}}}""")) + "]";
    }

    /// <summary>
    /// Answers like Hugging Face's API and file downloads for a small repository.
    /// </summary>
    private sealed class HuggingFace : HttpMessageHandler, IHttpClientFactory
    {
        public Dictionary<string, (HttpStatusCode Status, string Body, string? Link)> Responses { get; } = [];
        public Dictionary<string, byte[]> Files { get; } = [];
        public List<string> Requested { get; } = [];
        public List<string?> Authorizations { get; } = [];
        public TimeSpan Delay { get; set; }

        public static HuggingFace WithTinyRepository(bool gated = false)
        {
            HuggingFace server = new();
            string gate = gated ? "\"manual\"" : "false";
            server.Responses[$"api/models/{REPO}"] = (HttpStatusCode.OK, $$$"""{"id":"{{{REPO}}}","sha":"{{{REVISION}}}","gated":{{{gate}}},"cardData":{"license":"apache-2.0"}}""", null);
            server.Responses[$"api/models/{REPO}/tree/{REVISION}?recursive=true"] = (HttpStatusCode.OK,
                FileList(("tiny-Q2_K.gguf", SMALL), ("sub folder/tiny-Q4_K_M.gguf", MEDIUM)), null);
            server.Files[$"{REPO}/resolve/{REVISION}/tiny-Q2_K.gguf"] = SMALL;
            server.Files[$"{REPO}/resolve/{REVISION}/sub%20folder/tiny-Q4_K_M.gguf"] = MEDIUM;
            return server;
        }

        public HttpClient CreateClient(string name)
        {
            return new HttpClient(this, disposeHandler: false);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }

            return Respond(request);
        }

        private HttpResponseMessage Respond(HttpRequestMessage request)
        {
            string path = request.RequestUri!.OriginalString[HuggingFaceClient.SITE.Length..];
            Requested.Add(path);
            Authorizations.Add(request.Headers.Authorization?.ToString());
            bool gated = Responses.TryGetValue($"api/models/{REPO}", out var info) && info.Body.Contains("\"manual\"");

            // A gated repository describes itself to anyone, but lists and serves its files only with a token.
            if (gated && request.Headers.Authorization is null && path != $"api/models/{REPO}")
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            if (Files.TryGetValue(path, out byte[]? file))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(file) };
            }

            if (Responses.TryGetValue(path, out var response))
            {
                HttpResponseMessage message = new(response.Status) { Content = new StringContent(response.Body) };

                if (response.Link is not null)
                {
                    message.Headers.Add("Link", response.Link);
                }

                return message;
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private sealed class MemoryCredentials : ICredentialStore
    {
        public Dictionary<string, string> Values { get; } = [];

        public Task DeleteAsync(CredentialReference reference, CancellationToken cancellationToken = default)
        {
            Values.Remove(reference.Value);
            return Task.CompletedTask;
        }

        public Task<CredentialMaterial?> GetAsync(CredentialReference reference, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Values.TryGetValue(reference.Value, out string? value) ? new CredentialMaterial(value) : null);
        }

        public Task StoreAsync(CredentialReference reference, CredentialMaterial material, CancellationToken cancellationToken = default)
        {
            Values[reference.Value] = material.Value;
            return Task.CompletedTask;
        }
    }

    private sealed class TestFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"HomiliaMessenger-gguf-{Guid.NewGuid():N}");

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
