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

            if (File.Family is ImageModelFamily.StableDiffusion1 or ImageModelFamily.StableDiffusionXL)
            {
                parts.Add(File.HasTextEncoder && File.HasVae ? "text encoder and VAE included" : "incomplete");
            }
            else if (File.Family == ImageModelFamily.Flux2Klein4B)
            {
                parts.Add("diffusion model; drawn with the shared Qwen3-4B text encoder and FLUX.2 VAE");
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>
    /// Why it cannot be added, or that it is already built in; empty when it can be added.
    /// </summary>
    public string Note => File.KnownModelId is not null
        ? "This file is one of the built-in models; download it from the Image Generation lists instead."
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
            ImageModelFamily.Flux2Klein4B => "FLUX.2 [klein] 4B",
            ImageModelFamily.OtherImageModel => "other image model",
            ImageModelFamily.LanguageModel => "language model",
            _ => "not recognized"
        };
    }
}

/// <summary>
/// Adding an image model from a Hugging Face repository: look the repository up, choose a GGUF file its header shows this
/// app can run on the processor or graphics card it is added for (Stable Diffusion 1.x on either; SDXL and FLUX.2
/// [klein] 4B on a graphics card), give the settings its model card recommends, and add it. Starting settings and the
/// prompt style follow the file's family; whether a negative prompt is used follows from the CFG. Its compatibility is
/// not verified, and the dialog says so.
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

    /// <summary>
    /// An ordinary SDXL checkpoint's usual settings: Euler ancestral, 25 steps, CFG 7.
    /// </summary>
    internal const int DEFAULT_SDXL_STEPS = 25;

    /// <summary>
    /// The prompt styles offered, in the order shown.
    /// </summary>
    public IReadOnlyList<ImagePromptStyle> PromptStyles { get; } = [ImagePromptStyle.Weighted, ImagePromptStyle.Plain];

    private readonly IImageModelLibrary _library;
    private ImageModelLookup? _lookup;
    private CancellationTokenSource? _lookingUp;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LookUpCommand))]
    private string _repository = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LookUpCommand), nameof(AddCommand))]
    private bool _isBusy;

    [ObservableProperty] private string _status;
    [ObservableProperty] private IReadOnlyList<ImageModelFileOption> _files = [];
    [ObservableProperty] private string? _repositoryDetails;
    [ObservableProperty] private Uri? _repositoryUrl;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSupportedSelection), nameof(SuggestionHint), nameof(SettingsHint), nameof(SizeText))]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private ImageModelFileOption? _selectedFile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PromptStyleHint))]
    private ImagePromptStyle _promptStyle = ImagePromptStyle.Weighted;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private string _sampler = DEFAULT_SAMPLER;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private decimal? _steps = DEFAULT_STEPS;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    [NotifyPropertyChangedFor(nameof(NegativePromptHint))]
    private decimal? _guidanceScale = (decimal)DEFAULT_GUIDANCE;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private bool _acceptsUnverified;

    /// <param name="purpose">What the model is added for: contact photos, or selfies.</param>
    /// <param name="processor">What it runs on, which decides the files offered.</param>
    public AddImageModelViewModel(IImageModelLibrary library, ImageModelPurpose purpose = ImageModelPurpose.Portraits, ImageProcessor processor = ImageProcessor.Cpu)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
        Purpose = purpose;
        Processor = processor;
        _status = processor == ImageProcessor.Gpu
            ? "Enter a Hugging Face repository with GGUF files of a Stable Diffusion 1.x, SDXL, or FLUX.2 [klein] 4B model."
            : "Enter a Hugging Face repository with Stable Diffusion 1.x GGUF files.";
    }

    public ImageModelPurpose Purpose { get; }

    public ImageProcessor Processor { get; }

    public string Title => (Purpose == ImageModelPurpose.Selfies ? "Add a selfie model from Hugging Face" : "Add an image model from Hugging Face")
        + (Processor == ImageProcessor.Gpu ? " (graphics card)" : string.Empty);

    /// <summary>
    /// What can be added here.
    /// </summary>
    public string Intro => Processor == ImageProcessor.Gpu
        ? "For experimenting. On a graphics card, this app runs GGUF files of Stable Diffusion 1.x and SDXL models with the text encoder and VAE in the same file, and FLUX.2 [klein] 4B diffusion models, which are drawn with the shared Qwen3-4B text encoder and FLUX.2 VAE. Each file's header is read to check what it holds; nothing else is downloaded until you add one."
        : "For experimenting. Only Stable Diffusion 1.x models in GGUF format, with the text encoder and VAE in the same file, can run on the processor; add SDXL and FLUX.2 models under Graphics card. Each file's header is read to check what it holds; nothing else is downloaded until you add one.";

    private ImageModelFamily Family => SelectedFile?.File.Family ?? ImageModelFamily.StableDiffusion1;

    /// <summary>
    /// The size images are drawn at: the size each family was trained at.
    /// </summary>
    private (int Width, int Height) Size => (Family, Purpose) switch
    {
        (ImageModelFamily.StableDiffusion1, ImageModelPurpose.Selfies) => (ImageGenerationSettings.SELFIE_WIDTH, ImageGenerationSettings.SELFIE_HEIGHT),
        (ImageModelFamily.StableDiffusion1, _) => (512, 512),
        (_, ImageModelPurpose.Selfies) => (ImageGenerationSettings.LARGE_SELFIE_WIDTH, ImageGenerationSettings.LARGE_SELFIE_HEIGHT),
        _ => (ImageGenerationSettings.LARGE_PORTRAIT_SIZE, ImageGenerationSettings.LARGE_PORTRAIT_SIZE)
    };

    public string SizeText => $"{(Purpose == ImageModelPurpose.Selfies ? "Selfies" : "Photos")} are {Size.Width}×{Size.Height}.";

    /// <summary>
    /// What the settings below apply to.
    /// </summary>
    public string SettingsHint
    {
        get
        {
            string what = Family switch
            {
                ImageModelFamily.StableDiffusionXL => "a complete SDXL model",
                ImageModelFamily.Flux2Klein4B => "a FLUX.2 [klein] 4B diffusion model",
                _ => "a complete Stable Diffusion 1.x model"
            };
            string defaults = Family == ImageModelFamily.Flux2Klein4B
                ? "The defaults suit the distilled [klein] 4B (4 steps, CFG 1); a base, undistilled one needs about 50 steps and CFG 4."
                : "The defaults suit an ordinary checkpoint.";
            string face = Purpose != ImageModelPurpose.Selfies
                ? string.Empty
                : Family == ImageModelFamily.Flux2Klein4B
                    ? " Selfies keep the contact's face by drawing from their photo itself."
                    : " Selfies keep the contact's face with face matching, which is downloaded with the model if needed.";

            return $"This file looks like {what}, but it has not been tested with this app. Enter the settings its model card recommends. {defaults}{face}";
        }
    }

    public string PromptStyleHint => PromptStyle == ImagePromptStyle.Plain
        ? "Plain sentences, for models that read prompts with a language model, as FLUX.2 does. No emphasis weights."
        : "Short phrases with emphasis weights such as (laughing:1.3), for models that read prompts with CLIP, as Stable Diffusion does.";

    /// <summary>
    /// Whether the model will use negative prompts, which follows from the CFG: the runtime reads them only above 1.
    /// </summary>
    public string NegativePromptHint => GuidanceScale is > 1
        ? "Negative prompts are used, since CFG is above 1."
        : "Negative prompts are not used at CFG 1, the setting distilled models need.";

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
        ? new ImageGenerationSettings(Size.Width, Size.Height, Sampler, null, (int)steps, (float)guidance)
        : null;

    /// <summary>
    /// Starts the settings and prompt style at what the chosen file's family usually needs.
    /// </summary>
    partial void OnSelectedFileChanged(ImageModelFileOption? value)
    {
        if (value?.File is not { } file)
        {
            return;
        }

        PromptStyle = IImageModelLibrary.PromptStyleOf(file.Family);
        (Sampler, Steps, GuidanceScale) = file.Family switch
        {
            ImageModelFamily.Flux2Klein4B => ("euler", 4m, 1m),
            ImageModelFamily.StableDiffusionXL => (DEFAULT_SAMPLER, (decimal)DEFAULT_SDXL_STEPS, (decimal)DEFAULT_GUIDANCE),
            _ => (DEFAULT_SAMPLER, (decimal)DEFAULT_STEPS, (decimal)DEFAULT_GUIDANCE)
        };
    }

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
            ImageModelLookup lookup = await _library.LookUpAsync(Repository, Processor, lookingUp.Token);

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
            LocalModelAddResult result = await _library.AddAsync(_lookup, SelectedFile.File, settings, Purpose, PromptStyle);


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
