using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HomiliaMessenger.Business.Modules.Agents;
using HomiliaMessenger.Business.Modules.ImageGeneration;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Presentation.Desktop.ViewModels;

/// <summary>
/// A GGUF file offered in the add-image-model dialog, with what its header showed.
/// </summary>
public sealed record ImageModelFileOption(ImageModelFile File)
{
    public string Name => Path.GetFileName(File.File.Path);

    public string Details
    {
        get
        {
            List<string> parts = [FormatSize(File.File.Bytes), Family(File.Family)];

            if (File.Quantization is { } quantization)
            {
                parts.Add(quantization);
            }

            if (File.Family == ImageModelFamily.StableDiffusion1)
            {
                parts.Add(File.HasTextEncoder && File.HasVae ? "text encoder and VAE included" : "incomplete");
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>
    /// Why it cannot be added, or that it is already built in; empty when it can be added.
    /// </summary>
    public string Note => File.KnownModelId is not null
        ? "This is the built-in DreamShaper 8 LCM; download it from the Image Generation list instead."
        : File.Problem ?? string.Empty;

    public bool HasNote => Note.Length > 0;

    public bool IsSupported => File.IsSupported;

    internal static string FormatSize(long bytes)
    {
        return bytes >= 1_000_000_000 ? $"{bytes / 1_000_000_000d:0.0} GB" : $"{bytes / 1_000_000d:0} MB";
    }

    private static string Family(ImageModelFamily family)
    {
        return family switch
        {
            ImageModelFamily.StableDiffusion1 => "Stable Diffusion 1.x",
            ImageModelFamily.StableDiffusion2 => "Stable Diffusion 2.x",
            ImageModelFamily.StableDiffusionXL => "SDXL",
            ImageModelFamily.OtherImageModel => "other image model",
            ImageModelFamily.LanguageModel => "language model",
            _ => "not recognized"
        };
    }
}

/// <summary>
/// Adding a Stable Diffusion 1.x model from a Hugging Face repository: look the repository up, choose a self-contained
/// GGUF file its header shows this app can run, give the settings its model card recommends, and add it. Its
/// compatibility is not verified, and the dialog says so; nothing is guessed about how it should be run.
/// </summary>
public partial class AddImageModelViewModel : ObservableObject
{
    /// <summary>
    /// The runtime's documented example for Stable Diffusion 1.5 (Euler ancestral, 20 steps) with its default CFG of 7:
    /// a starting point for an ordinary checkpoint, which the user is asked to check against the model card.
    /// </summary>
    internal const string DEFAULT_SAMPLER = "euler_a";
    internal const int DEFAULT_STEPS = 20;
    internal const float DEFAULT_GUIDANCE = 7f;

    private readonly IImageModelLibrary _library;
    private ImageModelLookup? _lookup;
    private CancellationTokenSource? _lookingUp;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LookUpCommand))]
    private string _repository = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LookUpCommand), nameof(AddCommand))]
    private bool _isBusy;

    [ObservableProperty] private string _status = "Enter a Hugging Face repository with Stable Diffusion 1.x GGUF files.";
    [ObservableProperty] private IReadOnlyList<ImageModelFileOption> _files = [];
    [ObservableProperty] private string? _repositoryDetails;
    [ObservableProperty] private Uri? _repositoryUrl;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSupportedSelection), nameof(SuggestionHint))]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private ImageModelFileOption? _selectedFile;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private string _sampler = DEFAULT_SAMPLER;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private decimal? _steps = DEFAULT_STEPS;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private decimal? _guidanceScale = (decimal)DEFAULT_GUIDANCE;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private bool _acceptsUnverified;

    /// <param name="purpose">What the model is added for: contact photos, or selfies.</param>
    public AddImageModelViewModel(IImageModelLibrary library, ImageModelPurpose purpose = ImageModelPurpose.Portraits)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
        Purpose = purpose;
    }

    public ImageModelPurpose Purpose { get; }

    public string Title => Purpose == ImageModelPurpose.Selfies ? "Add a selfie model from Hugging Face" : "Add an image model from Hugging Face";

    /// <summary>
    /// What the settings below apply to, and the size images are drawn at.
    /// </summary>
    public string SettingsHint => Purpose == ImageModelPurpose.Selfies
        ? $"This file looks like a complete Stable Diffusion 1.x model, but it has not been tested with this app or with face matching. Enter the settings its model card recommends; the defaults suit an ordinary checkpoint. Selfies are {ImageGenerationSettings.SELFIE_WIDTH}×{ImageGenerationSettings.SELFIE_HEIGHT}, and keep the contact's face with face matching, which is downloaded with the model if needed."
        : "This file looks like a complete Stable Diffusion 1.x model, but it has not been tested with this app. Enter the settings its model card recommends; the defaults suit an ordinary checkpoint. Photos are 512×512.";

    /// <summary>
    /// Raised once a model has been added, with <see cref="AddedModelId"/> set.
    /// </summary>
    public event EventHandler? Added;

    public string? AddedModelId { get; private set; }

    public IReadOnlyList<string> Samplers { get; } = ImageGenerationSettings.SAMPLERS;

    public bool HasFiles => Files.Count > 0;

    public bool HasSupportedSelection => SelectedFile?.IsSupported == true;

    /// <summary>
    /// A pointer to the model card when the file name suggests a few-step model, whose settings differ from the defaults.
    /// It is only advice: the settings are not changed.
    /// </summary>
    public string? SuggestionHint
    {
        get
        {
            string name = SelectedFile?.Name.ToLowerInvariant() ?? string.Empty;
            string? kind = new[] { "lcm", "turbo", "lightning", "hyper" }.FirstOrDefault(word => name.Contains(word, StringComparison.Ordinal));

            return kind is null
                ? null
                : $"The file name mentions \"{kind}\". Few-step models like this usually need their own sampler, very few steps, and a low CFG; the model card says which.";
        }
    }

    /// <summary>
    /// The settings as entered, or <see langword="null"/> when a value is missing.
    /// </summary>
    internal ImageGenerationSettings? Settings => Steps is { } steps && GuidanceScale is { } guidance
        ? Purpose == ImageModelPurpose.Selfies
            ? new ImageGenerationSettings(ImageGenerationSettings.SELFIE_WIDTH, ImageGenerationSettings.SELFIE_HEIGHT, Sampler, null, (int)steps, (float)guidance)
            : new ImageGenerationSettings(512, 512, Sampler, null, (int)steps, (float)guidance)
        : null;

    partial void OnFilesChanged(IReadOnlyList<ImageModelFileOption> value)
    {
        OnPropertyChanged(nameof(HasFiles));
    }

    private bool CanLookUp()
    {
        return !IsBusy && !string.IsNullOrWhiteSpace(Repository);
    }

    [RelayCommand(CanExecute = nameof(CanLookUp))]
    private async Task LookUpAsync()
    {
        _lookingUp?.Cancel();
        using CancellationTokenSource lookingUp = new();
        _lookingUp = lookingUp;
        IsBusy = true;
        Status = "Reading the repository and its files' headers…";
        Files = [];
        SelectedFile = null;
        RepositoryDetails = null;
        RepositoryUrl = null;
        _lookup = null;

        try
        {
            ImageModelLookup lookup = await _library.LookUpAsync(Repository, lookingUp.Token);

            if (!ReferenceEquals(lookingUp, _lookingUp))
            {
                return;
            }

            _lookup = lookup;
            ShowLookup(lookup);
        }
        catch (OperationCanceledException)
        {
            // The dialog was closed, or another lookup started.
        }
        catch (Exception error)
        {
            Status = "Could not look up the repository: " + error.Message;
        }
        finally
        {
            if (ReferenceEquals(lookingUp, _lookingUp))
            {
                _lookingUp = null;
                IsBusy = false;
            }
        }
    }

    private void ShowLookup(ImageModelLookup lookup)
    {
        if (lookup.Status != HuggingFaceLookupStatus.Found)
        {
            Status = lookup.Problem ?? "Nothing could be added from this repository.";
            return;
        }

        RepositoryUrl = new Uri(lookup.Url);
        RepositoryDetails = $"License: {lookup.License ?? "not stated; see the repository"}"
            + (lookup.PipelineTag is { } tag ? $" · Tagged {tag}" : string.Empty);
        Files = [.. lookup.Files.Select(file => new ImageModelFileOption(file))];
        SelectedFile = Files.FirstOrDefault(file => file.IsSupported);

        int supported = Files.Count(file => file.IsSupported);
        Status = (supported switch
        {
            0 => "None of these files can be added. Each one's reason is shown below.",
            1 => "One file can be added.",
            _ => $"{supported} files can be added; choose one."
        }) + (lookup.Problem is { } note ? " " + note : string.Empty);
    }

    private bool CanAdd()
    {
        return !IsBusy && HasSupportedSelection && AcceptsUnverified && Settings is { } settings && settings.Problem() is null;
    }

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private async Task AddAsync()
    {
        if (_lookup is null || SelectedFile is null || Settings is not { } settings)
        {
            return;
        }

        IsBusy = true;

        try
        {
            LocalModelAddResult result = await _library.AddAsync(_lookup, SelectedFile.File, settings, Purpose);


            if (result.IsSuccessful)
            {
                AddedModelId = result.ModelId;
                Added?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                Status = result.Problem ?? "The model could not be added.";
            }
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

    /// <summary>
    /// Stops a lookup in progress, when the dialog closes.
    /// </summary>
    public void CancelLookUp()
    {
        _lookingUp?.Cancel();
    }
}
