using HomiliaMessenger.Business.Modules.Agents;
using HomiliaMessenger.Business.Modules.Contacts;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HomiliaMessenger.Presentation.Desktop.Models;

/// <summary>
/// A choice in the contact editor's gender selector.
/// </summary>
public sealed record GenderOption(string Label, Gender? Value)
{
    public static GenderOption NotSpecified { get; } = new("Not specified", null);

    public static IReadOnlyList<GenderOption> All { get; } =
    [
        NotSpecified,
        new("Male", Gender.Male),
        new("Female", Gender.Female)
    ];

    public static GenderOption For(Gender? gender)
    {
        return All.FirstOrDefault(option => option.Value == gender) ?? NotSpecified;
    }

    public override string ToString()
    {
        return Label;
    }
}

/// <summary>
/// A choice in the contact editor's agent selector: no agent (simulated replies), or a model offered in Settings.
/// </summary>
public sealed record AgentOption(string Label, AgentAssignment? Assignment)
{
    public static AgentOption None { get; } = new("None (simulated replies)", null);

    /// <summary>
    /// No agent, then each local model, noting those that still need downloading.
    /// </summary>
    public static IReadOnlyList<AgentOption> From(ILocalModelLibrary library)
    {
        ArgumentNullException.ThrowIfNull(library);

        return
        [
            None,
            .. library.List().Select(model => new AgentOption(
                model.IsDownloaded ? model.DisplayName : $"{model.DisplayName} (not downloaded)",
                new AgentAssignment(library.Provider, model.Id)))
        ];
    }

    public override string ToString()
    {
        return Label;
    }
}
