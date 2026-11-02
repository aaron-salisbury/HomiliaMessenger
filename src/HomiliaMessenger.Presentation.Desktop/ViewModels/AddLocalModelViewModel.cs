using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HomiliaMessenger.Business.Modules.Agents;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace HomiliaMessenger.Presentation.Desktop.ViewModels;

/// <summary>
/// A GGUF variant offered in the add-model dialog.
/// </summary>
public sealed record GgufVariantOption(GgufVariant Variant)
{
    public string Label => Variant.Label;

    public string Details => FormatSize(Variant.DownloadBytes) + (Variant.Files.Count > 1 ? $" in {Variant.Files.Count} files" : string.Empty)
        + (Variant.IsRecommended ? " · recommended" : string.Empty);

    internal static string FormatSize(long bytes)
    {
        return bytes >= 1_000_000_000 ? $"{bytes / 1_000_000_000d:0.0} GB" : $"{bytes / 1_000_000d:0} MB";
    }
}

/// <summary>
/// Adding a GGUF model from a Hugging Face repository: look the repository up, choose a variant, and add it.
/// </summary>
public partial class AddLocalModelViewModel : ObservableObject
{
    private readonly ILocalModelLibrary _library;
    private HuggingFaceLookup? _lookup;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LookUpCommand))]
    private string _repository = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LookUpCommand), nameof(AddCommand), nameof(SaveTokenCommand))]
    private bool _isBusy;

    [ObservableProperty] private string _status = "Enter a repository that offers GGUF files, such as Qwen/Qwen2.5-1.5B-Instruct-GGUF, then look it up.";
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
    [NotifyPropertyChangedFor(nameof(HasVariants))]
    private IReadOnlyList<GgufVariantOption> _variants = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private GgufVariantOption? _selectedVariant;

    public AddLocalModelViewModel(ILocalModelLibrary library)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
    }

    public bool HasVariants => Variants.Count > 0;

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

    partial void OnRepositoryChanged(string value)
    {
        // A different repository needs its own lookup before anything can be added.
        _lookup = null;
        Variants = [];
        SelectedVariant = null;
        License = null;
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

        try
        {
            HuggingFaceLookup lookup = await _library.LookUpAsync(repository);

            // The user typed a different repository meanwhile; these results are not for it.
            if (Repository != repository)
            {
                Status = "The repository changed. Look it up again.";
                return;
            }

            NeedsToken = lookup.Status == HuggingFaceLookupStatus.NeedsToken;

            if (lookup.Status != HuggingFaceLookupStatus.Found)
            {
                Status = lookup.Problem ?? "The repository could not be looked up.";
                return;
            }

            _lookup = lookup;
            License = lookup.License is null ? "License: see the repository." : $"License: {lookup.License}. Check that it allows how you plan to use the model.";
            Variants = [.. lookup.Variants.Select(variant => new GgufVariantOption(variant))];
            SelectedVariant = Variants.FirstOrDefault(option => option.Variant.IsRecommended) ?? Variants[0];
            Status = $"Found {Variants.Count} {(Variants.Count == 1 ? "variant" : "variants")} in {lookup.RepoId}. Smaller files are faster and use less memory; larger ones are usually better.";
        }
        catch (Exception error)
        {
            Status = "Could not look up the repository: " + error.Message;
        }
        finally
        {
            IsBusy = false;
        }
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
