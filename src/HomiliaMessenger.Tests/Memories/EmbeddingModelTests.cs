using HomiliaMessenger.Business.Modules.Agents;
using HomiliaMessenger.Business.Modules.Memories;
using HomiliaMessenger.Integrations;
using HomiliaMessenger.Integrations.Embeddings;
using HomiliaMessenger.Integrations.LocalModels;
using LLama.Native;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Tests.Memories;

[TestClass]
public sealed class EmbeddingModelTests
{
    private const string BASE_URL = "https://example.test/embeddings/";
    private static readonly byte[] SMALL = Encoding.UTF8.GetBytes("small embedding model");
    private static readonly byte[] LARGE = Encoding.UTF8.GetBytes("larger embedding model");

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void Catalog_OffersPinnedModelsThatAreNeverConversationalModels()
    {
        IReadOnlyList<EmbeddingModelDefinition> models = EmbeddingModelCatalog.Models;
        IEnumerable<string> conversational = LocalModelCatalog.Models.Select(model => model.Id);

        Assert.IsGreaterThanOrEqualTo(2, models.Count);
        Assert.HasCount(models.Count, models.Select(model => model.Id).Distinct(StringComparer.OrdinalIgnoreCase));
        Assert.IsTrue(models.All(model => Regex.IsMatch(model.BaseUrl, "/resolve/[0-9a-f]{40}/$")));
        Assert.IsTrue(models.All(model => model.Dimensions > 0 && model.File.Size > 0 && model.File.Sha256.Length == 64 && model.MinSimilarity is > 0 and < 1));
        Assert.DoesNotContain(model => conversational.Contains(model.Id, StringComparer.OrdinalIgnoreCase), models);
    }

    [TestMethod]
    public void Composition_RegistersTheLocalEmbedderApartFromTheAgents()
    {
        using TestFolder folder = new();
        ServiceCollection services = new();
        services.AddLogging().AddHttpClient();
        services.RegisterInternalIntegrationsServices(folder.Path);
        using ServiceProvider provider = services.BuildServiceProvider();

        ITextEmbedder embedder = provider.GetRequiredService<ITextEmbedder>();
        IReadOnlyList<LocalModel> conversational = provider.GetRequiredService<ILocalModelLibrary>().List();
        IReadOnlyList<EmbeddingModel> embedding = provider.GetRequiredService<IEmbeddingModelLibrary>().List();

        Assert.IsInstanceOfType<LocalTextEmbedder>(embedder);
        Assert.IsNull(embedder.Model);
        Assert.DoesNotContain(model => embedding.Any(candidate => candidate.Id == model.Id), conversational);
    }

    [TestMethod]
    public async Task Library_UsesTheFirstModelDownloadedAndRemembersTheUsersChoice()
    {
        using TestFolder folder = new();
        FakeServer server = new();
        EmbeddingModelLibrary library = CreateLibrary(folder.Path, server);

        LocalModelDownloadResult small = await library.DownloadAsync("small", cancellationToken: TestContext.CancellationToken);
        LocalModelDownloadResult large = await library.DownloadAsync("large", cancellationToken: TestContext.CancellationToken);
        string? afterDownloads = library.Active?.Id;
        bool chosen = library.Select("large");
        bool unknown = library.Select("missing");
        EmbeddingModelLibrary restarted = CreateLibrary(folder.Path, server);

        Assert.AreEqual(LocalModelDownloadStatus.Success, small.Status);
        Assert.AreEqual(LocalModelDownloadStatus.Success, large.Status);
        Assert.AreEqual("small", afterDownloads);
        Assert.IsTrue(chosen);
        Assert.IsFalse(unknown);
        Assert.AreEqual("large", restarted.Active?.Id);
        Assert.IsTrue(restarted.List().Single(model => model.Id == "large").IsSelected);
    }

    [TestMethod]
    public async Task Embedder_IsUnavailableWithoutADownloadedModelAndNeverReachesTheNetwork()
    {
        using TestFolder folder = new();
        FakeServer server = new();
        EmbeddingModelLibrary library = CreateLibrary(folder.Path, server);
        await using LlamaEmbeddingHost host = new(NullLogger<LlamaEmbeddingHost>.Instance);
        LocalTextEmbedder embedder = new(library, NullLogger<LocalTextEmbedder>.Instance, host);

        EmbeddingResult before = await embedder.EmbedAsync(["Aaron is a nurse."], EmbeddingPurpose.Document, TestContext.CancellationToken);
        await library.DownloadAsync("small", cancellationToken: TestContext.CancellationToken);
        string? ready = embedder.Model;
        int requests = server.Requests;
        await library.DeleteAsync("small", TestContext.CancellationToken);

        Assert.AreEqual(EmbeddingStatus.Unavailable, before.Status);
        Assert.AreEqual("small", ready);
        Assert.IsNull(embedder.Model);
        Assert.AreEqual(1, requests);
        Assert.AreEqual(EmbeddingStatus.Unavailable, (await embedder.EmbedAsync(["x"], EmbeddingPurpose.Query, TestContext.CancellationToken)).Status);
        Assert.AreEqual(1, server.Requests);
    }

    [TestMethod]
    public void Host_ShortensLongTextsAndNormalizesVectors()
    {
        Assert.AreEqual("abc", LlamaEmbeddingHost.Fit("abc", 512));
        Assert.HasCount(1024, LlamaEmbeddingHost.Fit(new string('x', 5000), 512));
        float[] normalized = LlamaEmbeddingHost.Normalized([3, 4]);
        Assert.AreEqual(0.6f, normalized[0], 1e-6f);
        Assert.AreEqual(0.8f, normalized[1], 1e-6f);
    }

    /// <summary>
    /// Embeds with a real catalog model when it is in <c>HOMILIA_EMBEDDING_TEST_MODELS</c> or this machine's application
    /// data, to check that llama.cpp makes vectors of the right size whose similarity reflects meaning, and that the
    /// default threshold tells related memories from unrelated ones. Skipped otherwise, so the suite needs no download.
    /// </summary>
    [TestMethod]
    [TestCategory("LocalModel")]
    [DataRow(EmbeddingModelCatalog.BGE_SMALL)]
    [DataRow(EmbeddingModelCatalog.NOMIC_EMBED)]
    public async Task RealModel_EmbedsRelatedTextsCloserThanUnrelatedOnes(string modelId)
    {
        string directory = Environment.GetEnvironmentVariable("HOMILIA_EMBEDDING_TEST_MODELS")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HomiliaMessenger", "Models", "Embeddings");
        EmbeddingModelLibrary library = new(EmbeddingModelCatalog.Models, directory, new FakeServer(), NullLogger<EmbeddingModelLibrary>.Instance);
        EmbeddingModelDefinition model = library.Find(modelId)!;

        if (!library.IsDownloaded(model))
        {
            Assert.Inconclusive($"{model.DisplayName} is not downloaded on this machine.");
        }

        string[] queries = ["How is your dog doing?", "Are you still working nights?", "Have you visited your sister lately?"];
        string[] memories =
        [
            "Aaron's dog Biscuit has been sick.",
            "Aaron works night shifts at the hospital.",
            "Aaron's sister Jen lives in Portland.",
            "Aaron drinks his coffee black.",
            "Aaron sold his old car in March.",
            "Aaron's birthday is in May."
        ];

        await using LlamaEmbeddingHost host = new(NullLogger<LlamaEmbeddingHost>.Instance);
        IReadOnlyList<float[]> queryVectors = await host.EmbedAsync(library.ModelDirectory(model), model, [.. queries.Select(text => model.QueryPrefix + text)], TestContext.CancellationToken);
        IReadOnlyList<float[]> memoryVectors = await host.EmbedAsync(library.ModelDirectory(model), model, [.. memories.Select(text => model.DocumentPrefix + text)], TestContext.CancellationToken);
        float leastRelated = float.MaxValue;
        float mostUnrelated = float.MinValue;

        for (int query = 0; query < queries.Length; query++)
        {
            for (int memory = 0; memory < memories.Length; memory++)
            {
                float score = VectorSimilarity.Cosine(queryVectors[query], memoryVectors[memory]);
                leastRelated = query == memory ? Math.Min(leastRelated, score) : leastRelated;
                mostUnrelated = query == memory ? mostUnrelated : Math.Max(mostUnrelated, score);
            }
        }

        TestContext.WriteLine($"{model.Id}: least related {leastRelated:0.000}; most unrelated {mostUnrelated:0.000}.");

        Assert.IsTrue(queryVectors.Concat(memoryVectors).All(vector => vector.Length == model.Dimensions));
        Assert.IsGreaterThan(mostUnrelated, leastRelated);
        Assert.IsGreaterThanOrEqualTo(model.MinSimilarity, leastRelated);
        Assert.IsLessThan(model.MinSimilarity, mostUnrelated);
    }

    private static EmbeddingModelLibrary CreateLibrary(string directory, FakeServer server)
    {
        return new EmbeddingModelLibrary([Definition("small", SMALL), Definition("large", LARGE)], directory, server, NullLogger<EmbeddingModelLibrary>.Instance);
    }

    private static EmbeddingModelDefinition Definition(string id, byte[] content)
    {
        return new EmbeddingModelDefinition(id, id, "A test model.", "MIT", BASE_URL,
            new LocalModelFile($"{id}.gguf", content.Length, Convert.ToHexString(SHA256.HashData(content))),
            4, LLamaPoolingType.Mean, 512, "query: ", "document: ", 0.5f);
    }

    private sealed class FakeServer : HttpMessageHandler, IHttpClientFactory
    {
        private int _requests;

        public int Requests => _requests;

        public HttpClient CreateClient(string name)
        {
            return new HttpClient(this, disposeHandler: false);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            string name = request.RequestUri!.AbsoluteUri[BASE_URL.Length..];
            byte[] content = name.StartsWith("small", StringComparison.Ordinal) ? SMALL : LARGE;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) });
        }
    }

    private sealed class TestFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"HomiliaMessenger-embeddings-{Guid.NewGuid():N}");

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
