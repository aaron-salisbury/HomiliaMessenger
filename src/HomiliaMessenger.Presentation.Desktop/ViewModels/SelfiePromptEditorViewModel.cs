using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HomiliaMessenger.Business.Modules.ImageGeneration;
using System;

namespace HomiliaMessenger.Presentation.Desktop.ViewModels;

/// <summary>
/// Checking, and changing, the words a planned selfie is drawn with before it is taken.
/// </summary>
public partial class SelfiePromptEditorViewModel : ObservableObject
{
    private readonly SelfiePrompt _planned;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PromptLength), nameof(IsChanged))]
    [NotifyCanExecuteChangedFor(nameof(TakeCommand), nameof(ResetCommand))]
    private string _prompt;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NegativePromptLength), nameof(IsChanged))]
    [NotifyCanExecuteChangedFor(nameof(ResetCommand))]
    private string _negativePrompt;

    public SelfiePromptEditorViewModel(string contactName, SelfiePrompt planned)
    {
        ArgumentNullException.ThrowIfNull(contactName);
        _planned = planned ?? throw new ArgumentNullException(nameof(planned));
        ContactName = contactName;
        _prompt = planned.Prompt;
        _negativePrompt = planned.NegativePrompt;
    }

    public string ContactName { get; }

    public string Heading => $"{ContactName}'s selfie";

    public string Explanation => $"{ContactName} planned this selfie from your chat. These are the words it will be drawn with; "
        + "change anything before it is taken. The face comes from their profile photo. "
        + (IsPlain
            ? "The selfie model reads plain sentences: describe what should happen, and its consequences, as you would to a person."
            : "Weights such as (laughing:1.3) make a word count for more.");

    /// <summary>
    /// Whether the selfie model reads plain sentences, rather than weighted phrases read in chunks of tokens.
    /// </summary>
    public bool IsPlain => _planned.Style == ImagePromptStyle.Plain;

    /// <summary>
    /// Whether the selfie model uses a negative prompt; at CFG 1 it does not, and the box is hidden.
    /// </summary>
    public bool SupportsNegativePrompt => _planned.SupportsNegativePrompt;

    /// <summary>
    /// Whether the user said to take the selfie, rather than closing the dialog.
    /// </summary>
    public bool IsAccepted { get; private set; }

    public bool IsChanged => Prompt != _planned.Prompt || NegativePrompt != _planned.NegativePrompt;

    /// <summary>
    /// How long the prompt is against the one chunk the model reads at a time. Longer prompts are drawn in full, but tend
    /// to be followed less faithfully.
    /// </summary>
    public string PromptLength => Length(Prompt);

    public string NegativePromptLength => Length(NegativePrompt);

    /// <summary>
    /// Raised when the user is done, either way.
    /// </summary>
    public event EventHandler? Closed;

    /// <summary>
    /// The words to draw with, as the user left them.
    /// </summary>
    public SelfiePrompt ToPrompt()
    {
        return _planned with { Prompt = Prompt.Trim(), NegativePrompt = SupportsNegativePrompt ? NegativePrompt.Trim() : string.Empty };
    }

    [RelayCommand(CanExecute = nameof(CanTake))]
    private void Take()
    {
        IsAccepted = true;
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private bool CanTake()
    {
        return !string.IsNullOrWhiteSpace(Prompt);
    }

    [RelayCommand]
    private void Cancel()
    {
        IsAccepted = false;
        Closed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Puts back the words as planned.
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsChanged))]
    private void Reset()
    {
        Prompt = _planned.Prompt;
        NegativePrompt = _planned.NegativePrompt;
    }

    private string Length(string text)
    {
        if (IsPlain)
        {
            return $"{(text ?? string.Empty).Length} characters";
        }

        int tokens = PortraitPromptComposer.EstimateTokens(text ?? string.Empty);
        return tokens > PortraitPromptComposer.CLIP_CHUNK_TOKENS
            ? $"About {tokens} tokens, past the {PortraitPromptComposer.CLIP_CHUNK_TOKENS} the model reads best at once"
            : $"About {tokens} of {PortraitPromptComposer.CLIP_CHUNK_TOKENS} tokens";
    }
}
