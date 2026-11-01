using HomiliaMessenger.Business.Modules.Agents;
using HomiliaMessenger.Business.Modules.Conversations;
using LLama.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntimeGenAI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Integrations.LocalModels;

/// <summary>
/// Answers with a model running on this device, without a server or GPU: ONNX models through ONNX Runtime GenAI, and GGUF
/// models through llama.cpp. Contacts use it with the provider <c>Local</c> and a model name, such as
/// <c>phi-3.5-mini</c>. Only one model is kept loaded across both runtimes, to bound memory use.
/// </summary>
internal sealed class LocalConversationAgent : IConversationAgent
{
    private static readonly GenerationSettings SETTINGS = new();

    private readonly LocalModelLibrary _library;
    private readonly OnnxModelHost _host;
    private readonly LlamaModelHost? _llamaHost;

    // Held across choosing a runtime and generating, so replies on different runtimes never load two models at once.
    private readonly SemaphoreSlim _runtimeGate = new(1, 1);
    private readonly ILogger<LocalConversationAgent> _logger;

    public LocalConversationAgent(LocalModelLibrary library, OnnxModelHost host, ILogger<LocalConversationAgent> logger, LlamaModelHost? llamaHost = null)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _llamaHost = llamaHost;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string Provider => LocalModelLibrary.PROVIDER;

    public async Task<AgentReply> ReplyAsync(AgentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        LocalModelDefinition? model = _library.Find(request.Model);

        if (model is null)
        {
            string known = string.Join(", ", _library.Catalog.Select(candidate => candidate.Id));
            return AgentReply.Unavailable($"There is no local model named \"{request.Model}\". Local models: {known}.");
        }

        if (!_library.IsDownloaded(model))
        {
            return AgentReply.Unavailable($"{model.DisplayName} has not been downloaded yet. Download it from Settings.");
        }

        await _runtimeGate.WaitAsync(cancellationToken);

        try
        {
            string directory = _library.ModelDirectory(model);
            string? text;

            if (model.Format == LocalModelFormat.Gguf)
            {
                if (_llamaHost is null)
                {
                    return AgentReply.Unavailable($"{model.DisplayName} needs llama.cpp, which is not available here.");
                }

                await _host.UnloadAsync(null);
                text = await _llamaHost.GenerateAsync(directory, model.EntryFile, ToMessages(request), request, SETTINGS, cancellationToken);
            }
            else
            {
                if (_llamaHost is not null)
                {
                    await _llamaHost.UnloadAsync(null);
                }

                text = await _host.GenerateAsync(directory, model.ContextTokens, ToMessages(request), request, SETTINGS, cancellationToken);
            }

            if (text is null)
            {
                return AgentReply.Unavailable($"The contact's description and latest message are too long for {model.DisplayName}. Shorten the personality or background.");
            }

            return string.IsNullOrWhiteSpace(text)
                ? AgentReply.InvalidResponse($"{model.DisplayName} returned an empty reply.")
                : AgentReply.Success(text.Trim());
        }
        catch (Exception error) when (error is DirectoryNotFoundException or FileNotFoundException)
        {
            // Removed after the check above, while the reply was waiting its turn.
            return AgentReply.Unavailable($"{model.DisplayName} has not been downloaded yet. Download it from Settings.");
        }
        catch (Exception error) when (error is OnnxRuntimeGenAIException or RuntimeError or InvalidOperationException)
        {
            _logger.LogError(error, "Local model {ModelId} failed to reply.", model.Id);
            return AgentReply.Unavailable($"{model.DisplayName} could not run: {error.Message}");
        }
        finally
        {
            _runtimeGate.Release();
        }
    }

    /// <summary>
    /// The chat-template input: a system message with the persona instructions, then the conversation turns.
    /// </summary>
    internal static IReadOnlyList<ChatTemplateMessage> ToMessages(AgentRequest request)
    {
        List<ChatTemplateMessage> messages = [new("system", request.Instructions)];

        foreach (AgentTurn turn in request.Turns)
        {
            messages.Add(new(turn.Sender == MessageSender.User ? "user" : "assistant", turn.Text));
        }

        return messages;
    }
}
