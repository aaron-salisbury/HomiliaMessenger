using HomiliaMessenger.Business.Modules.Agents;
using HomiliaMessenger.Integrations.LocalModels;
using HomiliaMessenger.Presentation.Desktop.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Tests.Agents;

/// <summary>
/// Finding GGUF conversions of a model whose own repository offers none.
/// </summary>
public sealed partial class HuggingFaceModelTests
{
    private const string ORIGINAL = "Storyteller/Story-8B-v1";
    private const string CONVERSION = "quantizer/Story-8B-v1-GGUF";
    private const string CONVERSION_REVISION = "def456";

    [TestMethod]
    public async Task LookUp_OfAGgufRepositoryDoesNotSearchForConversions()
    {
        HuggingFace server = HuggingFace.WithTinyRepository();
        using TestFolder folder = new();

        HuggingFaceLookup lookup = await CreateLibrary(folder.Path, server, null).LookUpAsync(REPO, TestContext.CancellationToken);

        Assert.AreEqual(HuggingFaceLookupStatus.Found, lookup.Status);
        Assert.IsEmpty(lookup.Conversions);
        Assert.DoesNotContain(path => path.StartsWith("api/models?", StringComparison.Ordinal), server.Requested);
    }

    [TestMethod]
    public async Task LookUp_OfAnOriginalModelOffersItsVerifiedGgufConversions()
    {
        HuggingFace server = WithOriginalAndConversion();
        server.Responses[HuggingFaceClient.ConversionSearch(ORIGINAL)] = (HttpStatusCode.OK, "[" + string.Join(",",
            SearchResult(CONVERSION, ["Story-8B-v1-Q4_K_M.gguf", "Story-8B-v1-Q8_0.gguf", "README.md"], downloads: 900, parameters: 8_030_261_248),
            SearchResult("other/Story-8B-v1-quants", ["sub/story-q5_k_m.gguf", "story-Q6_K-00001-of-00002.gguf", "story-Q6_K-00002-of-00002.gguf"], downloads: 400, gated: true),
            SearchResult(CONVERSION, ["Story-8B-v1-Q4_K_M.gguf"]),
            SearchResult("incomplete/Story-8B-v1-GGUF", ["story-Q8_0-00001-of-00003.gguf", "mmproj-story-f16.gguf"]),
            SearchResult("weights/Story-8B-v1-GGUF", ["model.safetensors", "README.md"]),
            SearchResult(ORIGINAL, ["Story-8B-v1-Q4_K_M.gguf"]),
            SearchResult("../escape", ["x-Q4_K_M.gguf"]),
            """{"id":42,"siblings":[{"rfilename":"x-Q4_K_M.gguf"}]}""",
            """{"id":"no/files"}""",
            "17") + "]", $"<{HuggingFaceClient.SITE}api/models?cursor=next>; rel=\"next\"");
        using TestFolder folder = new();

        HuggingFaceLookup lookup = await CreateLibrary(folder.Path, server, null).LookUpAsync(ORIGINAL, TestContext.CancellationToken);

        Assert.AreEqual(HuggingFaceLookupStatus.NoModels, lookup.Status);
        Assert.Contains("contains the original model but no GGUF downloads", lookup.Problem!);
        Assert.AreSequenceEqual(new[] { CONVERSION, "other/Story-8B-v1-quants" }, lookup.Conversions.Select(conversion => conversion.RepoId).ToArray());
        Assert.IsTrue(lookup.Conversions.All(conversion => conversion.IsVerified));
        Assert.IsTrue(lookup.HasMoreConversions);

        HuggingFaceConversion first = lookup.Conversions[0];
        Assert.AreEqual("quantizer", first.Publisher);
        Assert.AreEqual(HuggingFaceClient.SITE + CONVERSION, first.Url);
        Assert.AreEqual(8_030_261_248, first.Parameters);
        Assert.AreEqual(900, first.Downloads);
        Assert.AreSequenceEqual(new[] { "Q4_K_M", "Q8_0" }, first.Quantizations.ToArray());
        Assert.IsTrue(lookup.Conversions[1].IsGated);
        Assert.AreSequenceEqual(new[] { "Q5_K_M", "Q6_K" }, lookup.Conversions[1].Quantizations.Order().ToArray());

        // Discovery reads only metadata: no file listing of the original, and nothing downloaded.
        Assert.DoesNotContain(path => path.Contains("/tree/", StringComparison.Ordinal) || path.Contains("/resolve/", StringComparison.Ordinal), server.Requested);
        Assert.Contains(path => path.Contains("base_model%3Aquantized%3AStoryteller%2FStory-8B-v1", StringComparison.Ordinal), server.Requested);
    }

    [TestMethod]
    public async Task LookUp_ListsTheOriginalsFilesWhenItsInformationDoesNotNameThem()
    {
        HuggingFace server = WithOriginalAndConversion();
        server.Responses[$"api/models/{ORIGINAL}"] = (HttpStatusCode.OK, $$"""{"id":"{{ORIGINAL}}","sha":"orig1","gated":false}""", null);
        server.Responses[$"api/models/{ORIGINAL}/tree/orig1?recursive=true"] = (HttpStatusCode.OK, FileList(("model.safetensors", SMALL)), null);
        using TestFolder folder = new();

        HuggingFaceLookup lookup = await CreateLibrary(folder.Path, server, null).LookUpAsync(ORIGINAL, TestContext.CancellationToken);

        Assert.AreEqual(HuggingFaceLookupStatus.NoModels, lookup.Status);
        Assert.AreEqual(CONVERSION, lookup.Conversions.Single().RepoId);
        Assert.Contains($"api/models/{ORIGINAL}/tree/orig1?recursive=true", server.Requested);
    }

    [TestMethod]
    public async Task LookUp_OfAGatedOriginalFindsConversionsWithoutAToken()
    {
        HuggingFace server = WithOriginalAndConversion();
        server.Responses[$"api/models/{ORIGINAL}"] = (HttpStatusCode.OK,
            $$"""{"id":"{{ORIGINAL}}","sha":"orig1","gated":"manual","siblings":[{"rfilename":"model.safetensors"}]}""", null);
        using TestFolder folder = new();

        HuggingFaceLookup lookup = await CreateLibrary(folder.Path, server, null).LookUpAsync(ORIGINAL, TestContext.CancellationToken);

        Assert.AreEqual(HuggingFaceLookupStatus.NoModels, lookup.Status);
        Assert.AreEqual(CONVERSION, lookup.Conversions.Single().RepoId);
    }

    [TestMethod]
    public async Task LookUp_JudgesRepositoriesByTheirFilesNotTheirNames()
    {
        HuggingFace server = new();
        const string misleading = "someone/Looks-Like-A-GGUF";
        const string plain = "someone/Plain-Name";
        server.Responses[$"api/models/{misleading}"] = (HttpStatusCode.OK, $$"""{"id":"{{misleading}}","sha":"m1","siblings":[{"rfilename":"model.safetensors"},{"rfilename":"notes.gguf.txt"}]}""", null);
        server.Responses[HuggingFaceClient.ConversionSearch(misleading)] = (HttpStatusCode.OK, "[" + string.Join(",",
            SearchResult("named/Looks-Like-A-GGUF-GGUF", ["model.safetensors"]),
            SearchResult("unnamed/Looks-Like-A-q", ["model-Q4_K_M.gguf"])) + "]", null);
        server.Responses[$"api/models/{plain}"] = (HttpStatusCode.OK, $$"""{"id":"{{plain}}","sha":"p1","siblings":[{"rfilename":"weights-Q5_K_M.gguf"}]}""", null);
        server.Responses[$"api/models/{plain}/tree/p1?recursive=true"] = (HttpStatusCode.OK, FileList(("weights-Q5_K_M.gguf", SMALL)), null);
        using TestFolder folder = new();
        LocalModelLibrary library = CreateLibrary(folder.Path, server, null);

        HuggingFaceLookup ggufInName = await library.LookUpAsync(misleading, TestContext.CancellationToken);
        HuggingFaceLookup ggufInFiles = await library.LookUpAsync(plain, TestContext.CancellationToken);

        Assert.AreEqual(HuggingFaceLookupStatus.NoModels, ggufInName.Status);
        Assert.AreEqual("unnamed/Looks-Like-A-q", ggufInName.Conversions.Single().RepoId);
        Assert.AreEqual(HuggingFaceLookupStatus.Found, ggufInFiles.Status);
        Assert.AreEqual("Q5_K_M", ggufInFiles.Variants.Single().Label);
    }

    [TestMethod]
    public async Task LookUp_FallsBackToUnconfirmedNameMatchesWhenTheModelTreeHasNone()
    {
        HuggingFace server = WithOriginalAndConversion();
        server.Responses[HuggingFaceClient.ConversionSearch(ORIGINAL)] = (HttpStatusCode.OK, "[]", null);
        server.Responses[HuggingFaceClient.NameSearch(ORIGINAL)] = (HttpStatusCode.OK, "[" + string.Join(",",
            SearchResult("guesser/Story-8B-v1-GGUF", ["story-Q4_K_M.gguf"]),
            SearchResult("tuner/Story-8B-v1-uncensored-GGUF", ["story-Q4_K_M.gguf"], tags: ["gguf", "base_model:tuner/Story-8B-v1-uncensored"]),
            SearchResult("Story-8B-v1-fan/Other-GGUF", ["other-Q4_K_M.gguf"])) + "]", null);
        using TestFolder folder = new();

        HuggingFaceLookup lookup = await CreateLibrary(folder.Path, server, null).LookUpAsync(ORIGINAL, TestContext.CancellationToken);

        Assert.AreEqual(HuggingFaceLookupStatus.NoModels, lookup.Status);
        Assert.Contains("not confirmed to be the same model", lookup.Problem!);
        HuggingFaceConversion match = lookup.Conversions.Single();
        Assert.AreEqual("guesser/Story-8B-v1-GGUF", match.RepoId);
        Assert.IsFalse(match.IsVerified);
    }

    [TestMethod]
    public async Task LookUp_ExplainsWhenNoConversionsAreFound()
    {
        HuggingFace server = WithOriginalAndConversion();
        server.Responses[HuggingFaceClient.ConversionSearch(ORIGINAL)] = (HttpStatusCode.OK, "[]", null);
        server.Responses[HuggingFaceClient.NameSearch(ORIGINAL)] = (HttpStatusCode.OK, "[]", null);
        using TestFolder folder = new();

        HuggingFaceLookup lookup = await CreateLibrary(folder.Path, server, null).LookUpAsync(ORIGINAL, TestContext.CancellationToken);

        Assert.AreEqual(HuggingFaceLookupStatus.NoModels, lookup.Status);
        Assert.IsEmpty(lookup.Conversions);
        Assert.Contains("no GGUF conversions of it were found", lookup.Problem!);
        Assert.Contains("search huggingface.co", lookup.Problem!);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.InternalServerError, "[]", "answered 500")]
    [DataRow(HttpStatusCode.TooManyRequests, "[]", "limiting requests")]
    [DataRow(HttpStatusCode.OK, "{not json", "failed")]
    [DataRow(HttpStatusCode.OK, """{"error":"unexpected"}""", "failed")]
    public async Task LookUp_ReportsAFailedSearchForConversions(HttpStatusCode status, string body, string expected)
    {
        HuggingFace server = WithOriginalAndConversion();
        server.Responses[HuggingFaceClient.ConversionSearch(ORIGINAL)] = (status, body, null);
        using TestFolder folder = new();

        HuggingFaceLookup lookup = await CreateLibrary(folder.Path, server, null).LookUpAsync(ORIGINAL, TestContext.CancellationToken);

        Assert.AreEqual(HuggingFaceLookupStatus.NoModels, lookup.Status);
        Assert.IsEmpty(lookup.Conversions);
        Assert.Contains("searching Hugging Face for conversions of it failed", lookup.Problem!);
        Assert.Contains(expected, lookup.Problem!);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.TooManyRequests, "{}", HuggingFaceLookupStatus.Failed, "limiting requests")]
    [DataRow(HttpStatusCode.OK, "{not json", HuggingFaceLookupStatus.Failed, "Could not reach Hugging Face")]
    [DataRow(HttpStatusCode.OK, """{"id":"x"}""", HuggingFaceLookupStatus.Failed, "Could not reach Hugging Face")]
    [DataRow(HttpStatusCode.NotFound, "{}", HuggingFaceLookupStatus.NotFound, "no repository named")]
    public async Task LookUp_ReportsAnUnusableRepositoryAnswer(HttpStatusCode status, string body, HuggingFaceLookupStatus expected, string problem)
    {
        HuggingFace server = new();
        server.Responses[$"api/models/{ORIGINAL}"] = (status, body, null);
        using TestFolder folder = new();

        HuggingFaceLookup lookup = await CreateLibrary(folder.Path, server, null).LookUpAsync(ORIGINAL, TestContext.CancellationToken);

        Assert.AreEqual(expected, lookup.Status);
        Assert.Contains(problem, lookup.Problem!);
        Assert.IsEmpty(lookup.Conversions);
    }

    [TestMethod]
    public async Task LookUp_ByAddressAndByNameFindTheSameConversions()
    {
        HuggingFace server = WithOriginalAndConversion();
        using TestFolder folder = new();
        LocalModelLibrary library = CreateLibrary(folder.Path, server, null);

        HuggingFaceLookup byName = await library.LookUpAsync(ORIGINAL, TestContext.CancellationToken);
        HuggingFaceLookup byAddress = await library.LookUpAsync($"https://huggingface.co/{ORIGINAL}/tree/main?not=used", TestContext.CancellationToken);

        Assert.AreEqual(byName.RepoId, byAddress.RepoId);
        Assert.AreEqual(CONVERSION, byName.Conversions.Single().RepoId);
        Assert.AreSequenceEqual(byName.Conversions.Select(conversion => conversion.RepoId).ToArray(), byAddress.Conversions.Select(conversion => conversion.RepoId).ToArray());
    }

    [TestMethod]
    public async Task LookUp_StopsWhenCanceledDuringDiscovery()
    {
        HuggingFace server = WithOriginalAndConversion();
        server.Stall = HuggingFaceClient.ConversionSearch(ORIGINAL);
        using TestFolder folder = new();
        using CancellationTokenSource canceling = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);

        Task<HuggingFaceLookup> lookingUp = CreateLibrary(folder.Path, server, null).LookUpAsync(ORIGINAL, canceling.Token);
        await server.Stalled.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.CancellationToken);
        await canceling.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => lookingUp);
    }

    [TestMethod]
    public async Task AddDialog_StopsALookupWhenCanceled()
    {
        HuggingFace server = WithOriginalAndConversion();
        server.Stall = HuggingFaceClient.ConversionSearch(ORIGINAL);
        using TestFolder folder = new();
        AddLocalModelViewModel dialog = new(CreateLibrary(folder.Path, server, null)) { Repository = ORIGINAL };

        Task lookingUp = dialog.LookUpCommand.ExecuteAsync(null);
        await server.Stalled.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.CancellationToken);
        dialog.CancelLookUp();
        await lookingUp;

        Assert.IsFalse(dialog.IsBusy);
        Assert.AreEqual("The lookup was stopped.", dialog.Status);
        Assert.IsFalse(dialog.ShowsConversions);
        Assert.IsTrue(dialog.LookUpCommand.CanExecute(null));
    }

    /// <summary>
    /// The main path: an original model's repository, a conversion chosen from those found, and its recommended variant
    /// added and downloaded exactly as if the conversion had been entered by name.
    /// </summary>
    [TestMethod]
    public async Task AddDialog_ChoosesAConversionThenAddsAndDownloadsItLikeAnEnteredRepository()
    {
        HuggingFace server = WithOriginalAndConversion();
        using TestFolder folder = new();
        LocalModelLibrary library = CreateLibrary(folder.Path, server, null);
        AddLocalModelViewModel dialog = new(library) { Repository = ORIGINAL };

        await dialog.LookUpCommand.ExecuteAsync(null);
        bool offeredConversions = dialog.ShowsConversions;
        string discovered = dialog.Status;
        GgufConversionOption option = dialog.Conversions.Single();
        await dialog.ChooseConversionCommand.ExecuteAsync(option);

        Assert.IsTrue(offeredConversions);
        Assert.Contains("Compatible GGUF conversions were found below", discovered);
        Assert.AreEqual("Story-8B-v1-GGUF", option.Name);
        Assert.AreEqual("by quantizer", option.Publisher);
        Assert.AreEqual(new Uri(HuggingFaceClient.SITE + CONVERSION), option.Url);
        Assert.AreEqual(CONVERSION, dialog.Repository);
        Assert.IsTrue(dialog.HasVariants);
        Assert.IsFalse(dialog.ShowsConversions);
        Assert.IsTrue(dialog.CanGoBackToConversions);
        Assert.AreEqual("Q4_K_M", dialog.SelectedVariant?.Label);
        Assert.StartsWith("A reasonable starting point.", dialog.SelectedVariant!.Guidance);

        await dialog.AddCommand.ExecuteAsync(null);
        LocalModelDownloadResult downloaded = await library.DownloadAsync(dialog.AddedModelId!, null, TestContext.CancellationToken);

        Assert.AreEqual(LocalModelLibrary.ModelId(CONVERSION, "Story-8B-v1-Q4_K_M.gguf"), dialog.AddedModelId);
        Assert.AreEqual(LocalModelDownloadStatus.Success, downloaded.Status, downloaded.Problem);
        Assert.Contains($"{CONVERSION}/resolve/{CONVERSION_REVISION}/Story-8B-v1-Q4_K_M.gguf", server.Requested);

        // Entering the conversion directly offers the same thing.
        HuggingFaceLookup entered = await library.LookUpAsync(CONVERSION, TestContext.CancellationToken);
        Assert.AreEqual(HuggingFaceLookupStatus.Found, entered.Status);
        Assert.AreEqual(dialog.AddedModelId, LocalModelLibrary.ModelId(entered.RepoId, entered.Variants.Single(variant => variant.IsRecommended).Files[0].Path));
    }

    [TestMethod]
    public async Task AddDialog_GoesBackToTheConversionsAndForgetsThemForAnotherRepository()
    {
        HuggingFace server = WithOriginalAndConversion();
        using TestFolder folder = new();
        AddLocalModelViewModel dialog = new(CreateLibrary(folder.Path, server, null)) { Repository = ORIGINAL };

        await dialog.LookUpCommand.ExecuteAsync(null);
        string discovered = dialog.Status;
        await dialog.ChooseConversionCommand.ExecuteAsync(dialog.Conversions[0]);
        dialog.ShowConversionsCommand.Execute(null);

        Assert.AreEqual(ORIGINAL, dialog.Repository);
        Assert.AreEqual(discovered, dialog.Status);
        Assert.IsTrue(dialog.ShowsConversions);
        Assert.IsFalse(dialog.HasVariants);
        Assert.IsFalse(dialog.AddCommand.CanExecute(null));

        dialog.Repository = "someone/else";

        Assert.IsEmpty(dialog.Conversions);
        Assert.IsFalse(dialog.ShowsConversions);
    }

    [TestMethod]
    public async Task AddDialog_DropsEarlierConversionsWhenALaterSearchFails()
    {
        HuggingFace server = WithOriginalAndConversion();
        using TestFolder folder = new();
        AddLocalModelViewModel dialog = new(CreateLibrary(folder.Path, server, null)) { Repository = ORIGINAL };

        await dialog.LookUpCommand.ExecuteAsync(null);
        bool offeredBefore = dialog.ShowsConversions;
        server.Responses[HuggingFaceClient.ConversionSearch(ORIGINAL)] = (HttpStatusCode.InternalServerError, "[]", null);
        await dialog.LookUpCommand.ExecuteAsync(null);

        Assert.IsTrue(offeredBefore);
        Assert.IsEmpty(dialog.Conversions);
        Assert.IsFalse(dialog.ShowsConversions);
        Assert.Contains("failed", dialog.Status);
    }

    [TestMethod]
    public void Variants_KeepASingleFileApartFromASplitModelOfTheSameName()
    {
        HuggingFaceFile[] files =
        [
            new("model-Q4_K_M.gguf", 500, "a"),
            new("model-Q4_K_M-00001-of-00003.gguf", 300, "b"),
            new("model-Q4_K_M-00002-of-00003.gguf", 300, "c")
        ];

        GgufVariant single = HuggingFaceClient.Variants(files).Single();

        Assert.AreEqual("Q4_K_M", single.Label);
        Assert.AreEqual("model-Q4_K_M.gguf", single.Files.Single().Path);
    }

    [TestMethod]
    public void ConversionOption_ListsCommonQuantizationsFirstAndSummarizesTheRest()
    {
        GgufConversionOption option = new(new HuggingFaceConversion("q/Model-GGUF", HuggingFaceClient.SITE + "q/Model-GGUF", true,
            ["IQ3_M", "Q2_K", "F16", "Q8_0", "Q4_K_M", "Q5_K_S", "Q6_K", "Q5_K_M"], 1_500_000_000, 12345, true));

        Assert.AreEqual("Q4_K_M, Q5_K_M, Q6_K, Q8_0, Q2_K and 3 more", option.Quantizations);
        Assert.AreEqual($"1.5B parameters · {12345:N0} downloads · needs an access token", option.Details);
        Assert.IsFalse(option.IsUnconfirmed);
    }

    [TestMethod]
    [DataRow("Q4_K_M", 4)]
    [DataRow("IQ4_XS", 4)]
    [DataRow("Q5_K_M", 5)]
    [DataRow("Q6_K", 6)]
    [DataRow("Q8_0", 8)]
    [DataRow("Q4_0_4_4", 4)]
    [DataRow("BF16", 16)]
    [DataRow("F32", 32)]
    [DataRow("model-name", null)]
    public void VariantOption_ReadsTheBitsFromTheQuantization(string label, int? expected)
    {
        Assert.AreEqual(expected, GgufVariantOption.Bits(label));
    }

    [TestMethod]
    public void VariantOption_ExplainsTheTradeoffWithoutGuessingAtUnknownLabels()
    {
        GgufVariantOption q5 = new(new GgufVariant("Q5_K_M", [new HuggingFaceFile("m-Q5_K_M.gguf", 5_700_000_000, "a")], false));
        GgufVariantOption named = new(new GgufVariant("my-model", [new HuggingFaceFile("my-model.gguf", 10, "b")], false));

        Assert.Contains("more memory", q5.Guidance);
        Assert.AreEqual("5.7 GB", q5.Details);
        Assert.IsFalse(named.HasGuidance);
    }

    /// <summary>
    /// An original model's repository, with only SafeTensors weights, and a GGUF conversion of it that Hugging Face's model
    /// tree records.
    /// </summary>
    private static HuggingFace WithOriginalAndConversion()
    {
        HuggingFace server = new();
        server.Responses[$"api/models/{ORIGINAL}"] = (HttpStatusCode.OK, $$"""
            {"id":"{{ORIGINAL}}","sha":"orig1","gated":false,"cardData":{"license":"cc-by-nc-4.0"},
             "siblings":[{"rfilename":"config.json"},{"rfilename":"model-00001-of-00002.safetensors"},{"rfilename":"model-00002-of-00002.safetensors"}]}
            """, null);
        server.Responses[HuggingFaceClient.ConversionSearch(ORIGINAL)] = (HttpStatusCode.OK,
            "[" + SearchResult(CONVERSION, ["Story-8B-v1-Q4_K_M.gguf", "Story-8B-v1-Q8_0.gguf"], downloads: 500, parameters: 8_000_000_000) + "]", null);
        server.Responses[HuggingFaceClient.NameSearch(ORIGINAL)] = (HttpStatusCode.OK, "[]", null);
        server.Responses[$"api/models/{CONVERSION}"] = (HttpStatusCode.OK,
            $$"""{"id":"{{CONVERSION}}","sha":"{{CONVERSION_REVISION}}","gated":false,"cardData":{"license":"cc-by-nc-4.0"},"siblings":[{"rfilename":"Story-8B-v1-Q4_K_M.gguf"},{"rfilename":"Story-8B-v1-Q8_0.gguf"}]}""", null);
        server.Responses[$"api/models/{CONVERSION}/tree/{CONVERSION_REVISION}?recursive=true"] = (HttpStatusCode.OK,
            FileList(("Story-8B-v1-Q4_K_M.gguf", SMALL), ("Story-8B-v1-Q8_0.gguf", MEDIUM)), null);
        server.Files[$"{CONVERSION}/resolve/{CONVERSION_REVISION}/Story-8B-v1-Q4_K_M.gguf"] = SMALL;
        server.Files[$"{CONVERSION}/resolve/{CONVERSION_REVISION}/Story-8B-v1-Q8_0.gguf"] = MEDIUM;
        return server;
    }

    /// <summary>
    /// A repository as Hugging Face's model search describes it, with the fields the lookup asks for.
    /// </summary>
    private static string SearchResult(string id, string[] files, string[]? tags = null, long downloads = 0, long? parameters = null, bool gated = false)
    {
        StringBuilder json = new($$"""{"id":"{{id}}","downloads":{{downloads}},"gated":{{(gated ? "\"auto\"" : "false")}}""");
        json.Append(",\"siblings\":[" + string.Join(",", files.Select(file => $$"""{"rfilename":"{{file}}"}""")) + "]");
        json.Append(",\"tags\":[" + string.Join(",", (tags ?? ["gguf"]).Select(tag => $"\"{tag}\"")) + "]");

        if (parameters is not null)
        {
            json.Append($$""","gguf":{"total":{{parameters}},"architecture":"llama","context_length":8192}""");
        }

        return json.Append('}').ToString();
    }
}
