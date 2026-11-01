using HomiliaMessenger.Business.Modules.Agents;
using HomiliaMessenger.Business.Modules.Conversations;
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
/// Answers with a model running on this device through ONNX Runtime GenAI, without a server or GPU. Contacts use it with
/// the provider <c>Local</c> and a catalog model name, such as <c>phi-3.5-mini</c>.
/// </summary>
internal sealed class OnnxConversationAgent : IConversationAgent
{
    private static readonly GenerationSettings SETTINGS = new();

    private readonly LocalModelLibrary _library;
    private readonly OnnxModelHost _host;
    private readonly ILogger<OnnxConversationAgent> _logger;

    public OnnxConversationAgent(LocalModelLibrary library, OnnxModelHost host, ILogger<OnnxConversationAgent> logger)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _host = host ?? throw new ArgumentNullException(nameof(host));
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

        try
        {
            string? text = await _host.GenerateAsync(_library.ModelDirectory(model), model.ContextTokens, ToMessages(request), request, SETTINGS, cancellationToken);

            if (text is null)
            {
                return AgentReply.Unavailable($"The contact's description and latest message are too long for {model.DisplayName}. Shorten the personality or background.");
            }

            return string.IsNullOrWhiteSpace(text)
                ? AgentReply.InvalidResponse($"{model.DisplayName} returned an empty reply.")
                : AgentReply.Success(text.Trim());
        }
        catch (DirectoryNotFoundException)
        {
            // Removed after the check above, while the reply was waiting its turn.
            return AgentReply.Unavailable($"{model.DisplayName} has not been downloaded yet. Download it from Settings.");
        }
        catch (OnnxRuntimeGenAIException error)
        {
            _logger.LogError(error, "Local model {ModelId} failed to reply.", model.Id);
            return AgentReply.Unavailable($"{model.DisplayName} could not run: {error.Message}");
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
