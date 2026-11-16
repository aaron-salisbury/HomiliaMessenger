using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HomiliaMessenger.Business.Modules.Agents;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Presentation.Desktop.ViewModels;

/// <summary>
/// A GGUF variant offered in the add-model dialog.
/// </summary>
public sealed partial record GgufVariantOption(GgufVariant Variant)
{
    public string Label => Variant.Label;

    public string Details => FormatSize(Variant.DownloadBytes) + (Variant.Files.Count > 1 ? $" in {Variant.Files.Count} files" : string.Empty)
        + (Variant.IsRecommended ? " · recommended" : string.Empty);

    /// <summary>
    /// What the quantization trades between memory and fidelity, in general terms, or empty when its label says nothing.
    /// </summary>
    public string Guidance => (Variant.IsRecommended ? "A reasonable starting point. " : string.Empty) + Bits(Variant.Label) switch
    {
        null => string.Empty,
        <= 2 => "Smallest; quality drops noticeably.",
        3 => "Small; some loss of quality.",
        4 => "Balanced size and quality.",
        5 => "A little closer to the original than 4-bit, using more memory.",
        6 => "Close to the original, using more memory.",
        7 or 8 => "Nearly the original's quality; large.",
        16 or 32 => "Unquantized; very large, and rarely worth the memory.",
        _ => string.Empty
    };

    public bool HasGuidance => Guidance.Length > 0;

    internal static string FormatSize(long bytes)
    {
        return bytes >= 1_000_000_000 ? $"{bytes / 1_000_000_000d:0.0} GB" : $"{bytes / 1_000_000d:0} MB";
    }

    /// <summary>
    /// The bits per weight a quantization label names, such as 4 for <c>Q4_K_M</c> or 16 for <c>BF16</c>, or
    /// <see langword="null"/>.
    /// </summary>
    internal static int? Bits(string label)
    {
        Match match = BitsName().Match(label);

        return match.Success && int.TryParse(match.Groups["bits"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int bits) ? bits : null;
    }

    [GeneratedRegex(@"^(?:I?Q|B?FP?)(?<bits>\d{1,2})(?:_|$)", RegexOptions.IgnoreCase)]
    private static partial Regex BitsName();
}

/// <summary>
/// A GGUF conversion of a model, offered in the add-model dialog when the repository looked up has no GGUF files.
/// </summary>
public sealed record GgufConversionOption(HuggingFaceConversion Conversion)
{
    /// <summary>
    /// Quantizations most people choose between, listed first when a conversion offers them.
    /// </summary>
    private static readonly string[] COMMON = ["Q4_K_M", "Q5_K_M", "Q6_K", "Q8_0"];
    private const int QUANTIZATIONS_SHOWN = 5;

    public string RepoId => Conversion.RepoId;

    public string Name => RepoId[(RepoId.IndexOf('/') + 1)..];

    public string Publisher => "by " + Conversion.Publisher;

    public Uri Url => new(Conversion.Url);

    public string Details
    {
        get
        {
            List<string> parts = [];

            if (Conversion.Parameters is { } parameters)
            {
                parts.Add(parameters >= 1_000_000_000 ? $"{parameters / 1_000_000_000d:0.#}B parameters" : $"{parameters / 1_000_000d:0}M parameters");
            }

            parts.Add(Conversion.Downloads == 1 ? "1 download" : $"{Conversion.Downloads:N0} downloads");

            if (Conversion.IsGated)
            {
                parts.Add("needs an access token");
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>
    /// The quantizations offered, the common ones first.
    /// </summary>
    public string Quantizations
    {
        get
        {
            List<string> ordered = [.. Conversion.Quantizations
                .OrderBy(label => Array.FindIndex(COMMON, common => common.Equals(label, StringComparison.OrdinalIgnoreCase)) is int index and >= 0 ? index : COMMON.Length)
                .ThenBy(label => GgufVariantOption.Bits(label) ?? int.MaxValue)
                .ThenBy(label => label, StringComparer.OrdinalIgnoreCase)];

            return string.Join(", ", ordered.Take(QUANTIZATIONS_SHOWN)) + (ordered.Count > QUANTIZATIONS_SHOWN ? $" and {ordered.Count - QUANTIZATIONS_SHOWN} more" : string.Empty);
        }
    }

    /// <summary>
    /// Whether it was found only by its name, and may hold a different model.
    /// </summary>
    public bool IsUnconfirmed => !Conversion.IsVerified;
}

/// <summary>
/// Adding a GGUF model from a Hugging Face repository: look the repository up, choose a variant, and add it. A repository
/// without GGUF files offers its GGUF conversions instead; choosing one looks it up as if its name had been entered.
/// </summary>
public partial class AddLocalModelViewModel : ObservableObject
{
    private readonly ILocalModelLibrary _library;
    private HuggingFaceLookup? _lookup;
    private CancellationTokenSource? _lookingUp;

    /// <summary>
    /// The repository the offered conversions are of, and what was said about them.
    /// </summary>
    private (string Repository, string Status)? _conversionsOf;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LookUpCommand))]
    private string _repository = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LookUpCommand), nameof(AddCommand), nameof(SaveTokenCommand), nameof(ChooseConversionCommand), nameof(ShowConversionsCommand))]
    private bool _isBusy;

    [ObservableProperty] private string _status = "Enter a model's repository, such as Qwen/Qwen2.5-1.5B-Instruct, or paste its address, then look it up.";
    [ObservableProperty] private string? _license;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsTokenSettings))]
    private bool _needsToken;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsTokenSettings))]
    private bool _hasSavedToken;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveTokenCommand))]
    private string _token = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVariants), nameof(ShowsConversions), nameof(CanGoBackToConversions))]
    [NotifyCanExecuteChangedFor(nameof(ShowConversionsCommand))]
    private IReadOnlyList<GgufVariantOption> _variants = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private GgufVariantOption? _selectedVariant;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsConversions), nameof(CanGoBackToConversions))]
    [NotifyCanExecuteChangedFor(nameof(ShowConversionsCommand))]
    private IReadOnlyList<GgufConversionOption> _conversions = [];

    [ObservableProperty] private string? _moreConversions;

    public AddLocalModelViewModel(ILocalModelLibrary library)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
    }

    public bool HasVariants => Variants.Count > 0;

    /// <summary>
    /// Whether to list the conversions found: until one is chosen and its variants are shown.
    /// </summary>
    public bool ShowsConversions => Conversions.Count > 0 && !HasVariants;

    /// <summary>
    /// Whether a chosen conversion's variants are shown, so the other conversions can be gone back to.
    /// </summary>
    public bool CanGoBackToConversions => Conversions.Count > 0 && HasVariants;

    /// <summary>
    /// Whether to show the access token settings: when a repository needs a token, or one is saved and may be replaced
    /// or forgotten.
    /// </summary>
    public bool ShowsTokenSettings => NeedsToken || HasSavedToken;

    /// <summary>
    /// The model that was added, once <see cref="AddCommand"/> succeeds.
    /// </summary>
    public string? AddedModelId { get; private set; }

    /// <summary>
    /// Raised when a model has been added, so the dialog can close.
    /// </summary>
    public event EventHandler? Added;

    public async Task InitializeAsync()
    {
        HasSavedToken = await _library.HasHuggingFaceTokenAsync();
    }

    /// <summary>
    /// Stops a lookup in progress, as when the dialog closes.
    /// </summary>
    public void CancelLookUp()
    {
        _lookingUp?.Cancel();
    }

    partial void OnRepositoryChanged(string value)
    {
        // A different repository needs its own lookup before anything can be added.
        CancelLookUp();
        _lookup = null;
        Variants = [];
        SelectedVariant = null;
        License = null;

        // The conversions found stay offered while one of them, or the model they convert, is entered.
        if (_conversionsOf is { } source && value != source.Repository && !Conversions.Any(option => option.RepoId == value))
        {
            _conversionsOf = null;
            Conversions = [];
            MoreConversions = null;
        }
    }

    private bool CanLookUp()
    {
        return !IsBusy && !string.IsNullOrWhiteSpace(Repository);
    }

    [RelayCommand(CanExecute = nameof(CanLookUp))]
    private async Task LookUpAsync()
    {
        IsBusy = true;
        Status = "Looking up the repository on Hugging Face…";
        string repository = Repository;
        using CancellationTokenSource lookingUp = new();
        _lookingUp = lookingUp;

        try
        {
            HuggingFaceLookup lookup = await _library.LookUpAsync(repository, lookingUp.Token);

            // The user typed a different repository meanwhile; these results are not for it.
            if (Repository != repository)
            {
                Status = "The repository changed. Look it up again.";
                return;
            }

            NeedsToken = lookup.Status == HuggingFaceLookupStatus.NeedsToken;

            if (lookup.Status == HuggingFaceLookupStatus.NoModels && lookup.Conversions.Count > 0)
            {
                _conversionsOf = (repository, lookup.Problem ?? string.Empty);
                Conversions = [.. lookup.Conversions.Select(conversion => new GgufConversionOption(conversion))];
                MoreConversions = lookup.HasMoreConversions ? $"Showing the {Conversions.Count} most downloaded; the model's page on Hugging Face lists more." : null;
            }
            else if (_conversionsOf?.Repository == repository)
            {
                // Looking the model up again found no conversions this time, so the earlier ones are no longer shown.
                _conversionsOf = null;
                Conversions = [];
                MoreConversions = null;
            }

            if (lookup.Status != HuggingFaceLookupStatus.Found)
            {
                Status = lookup.Problem ?? "The repository could not be looked up.";
                return;
            }

            _lookup = lookup;
            License = lookup.License is null ? "License: see the repository." : $"License: {lookup.License}. Check that it allows how you plan to use the model.";
            Variants = [.. lookup.Variants.Select(variant => new GgufVariantOption(variant))];
            SelectedVariant = Variants.FirstOrDefault(option => option.Variant.IsRecommended) ?? Variants[0];
            Status = $"Found {Variants.Count} {(Variants.Count == 1 ? "variant" : "variants")} in {lookup.RepoId}. Smaller files use less memory and run faster; "
                + "larger ones stay closer to the original model. Running a model takes more memory than its file size, and more with longer conversations.";
        }
        catch (OperationCanceledException)
        {
            Status = Repository != repository ? "The repository changed. Look it up again." : "The lookup was stopped.";
        }
        catch (Exception error)
        {
            Status = "Could not look up the repository: " + error.Message;
        }
        finally
        {
            if (ReferenceEquals(_lookingUp, lookingUp))
            {
                _lookingUp = null;
            }

            IsBusy = false;
        }
    }

    private bool CanChooseConversion(GgufConversionOption? option)
    {
        return !IsBusy && option is not null;
    }

    /// <summary>
    /// Looks up a conversion found for the repository entered, just as if its name had been entered instead.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanChooseConversion))]
    private async Task ChooseConversionAsync(GgufConversionOption? option)
    {
        if (option is null)
        {
            return;
        }

        Repository = option.RepoId;
        await LookUpAsync();
    }

    private bool CanShowConversions()
    {
        return !IsBusy && _conversionsOf is not null && Conversions.Count > 0;
    }

    /// <summary>
    /// Goes back from a chosen conversion to the conversions found, to choose another.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanShowConversions))]
    private void ShowConversions()
    {
        if (_conversionsOf is not { } source)
        {
            return;
        }

        Repository = source.Repository;
        NeedsToken = false;
        Status = source.Status;
    }

    private bool CanSaveToken()
    {
        return !IsBusy && !string.IsNullOrWhiteSpace(Token);
    }

    /// <summary>
    /// Saves the access token, then looks the repository up again with it.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSaveToken))]
    private async Task SaveTokenAsync()
    {
        try
        {
            await _library.SetHuggingFaceTokenAsync(Token);
            Token = string.Empty;
            HasSavedToken = true;
        }
        catch (Exception error)
        {
            Status = "Could not save the token: " + error.Message;
            return;
        }

        await LookUpAsync();
    }

    [RelayCommand]
    private async Task ForgetTokenAsync()
    {
        try
        {
            await _library.SetHuggingFaceTokenAsync(null);
            HasSavedToken = false;
            Status = "The access token was removed.";
        }
        catch (Exception error)
        {
            Status = "Could not remove the token: " + error.Message;
        }
    }

    private bool CanAdd()
    {
        return !IsBusy && _lookup is not null && SelectedVariant is not null;
    }

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private async Task AddAsync()
    {
        if (_lookup is null || SelectedVariant is null)
        {
            return;
        }

        IsBusy = true;

        try
        {
            LocalModelAddResult result = await _library.AddAsync(_lookup, SelectedVariant.Variant);

            if (!result.IsSuccessful)
            {
                Status = result.Problem ?? "The model could not be added.";
                return;
            }

            AddedModelId = result.ModelId;
            Added?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception error)
        {
            Status = "Could not add the model: " + error.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
