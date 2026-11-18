using HomiliaMessenger.Business.Modules.Agents;
using HomiliaMessenger.Integrations.LocalModels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace HomiliaMessenger.Tests.Agents;

[TestClass]
public sealed class LlamaCacheTests
{
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Two contacts taking turns on one downloaded GGUF model, as in a group: once each has read their prompt, a turn
    /// picks up from their own cache rather than reading the whole prompt again. Skipped when no GGUF model is downloaded.
    /// </summary>
    [TestMethod]
    [TestCategory("LocalModel")]
    public async Task LlamaHost_ContactsTakingTurnsEachResumeTheirOwnCache()
    {
        string models = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HomiliaMessenger", "Models");
        LocalModelLibrary library = new(LocalModelCatalog.Models, models, new NoHttp(), new OnnxModelHost(), NullLogger<LocalModelLibrary>.Instance);
        LocalModelDefinition? model = library.Catalog.FirstOrDefault(candidate => candidate.Format == LocalModelFormat.Gguf && library.IsDownloaded(candidate));

        if (model is null)
        {
            Assert.Inconclusive("No GGUF model has been added and downloaded on this machine.");
        }

        await using LlamaModelHost host = new(NullLogger<LlamaModelHost>.Instance);
        string about = string.Join(" ", Enumerable.Repeat("They like hiking, old films, cooking pasta, and long talks about music and books.", 25));
        string marcus = "You are Marcus, a real person in a group chat. " + about;
        string elena = "You are Elena, a real person in a group chat. " + about;
        List<string> chat = [.. Enumerable.Range(0, 40).Select(index => $"Message {index}: so what did everyone think about the weekend plans and the trip up north?")];

        async Task<TimeSpan> TurnAsync(string instructions)
        {
            AgentRequest request = new(model.Id, instructions, [], ["\n\n"], Precise: true);
            List<ChatTemplateMessage> messages = [new("system", instructions), new("user", string.Join("\n", chat))];
            Stopwatch watch = Stopwatch.StartNew();
            string? reply = await host.GenerateAsync(library.ModelDirectory(model), model.EntryFile, messages, request, new GenerationSettings(MaxReplyTokens: 20), TestContext.CancellationToken);
            watch.Stop();
            Assert.IsFalse(string.IsNullOrWhiteSpace(reply));
            chat.Add(reply!);
            return watch.Elapsed;
        }

        await TurnAsync(marcus);
        TimeSpan elenaFirst = await TurnAsync(elena);
        TimeSpan marcusAgain = await TurnAsync(marcus);
        TimeSpan elenaAgain = await TurnAsync(elena);

        TestContext.WriteLine($"Elena's first turn {elenaFirst.TotalSeconds:0.0}s; then Marcus {marcusAgain.TotalSeconds:0.0}s and Elena {elenaAgain.TotalSeconds:0.0}s.");
        Assert.IsLessThan(elenaFirst / 2, marcusAgain);
        Assert.IsLessThan(elenaFirst / 2, elenaAgain);
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("No downloads are made.");
    }
}
