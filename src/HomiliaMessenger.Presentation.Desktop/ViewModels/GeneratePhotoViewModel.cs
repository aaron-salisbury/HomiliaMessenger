using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HomiliaMessenger.Business.Base.Extensions;
using HomiliaMessenger.Business.Modules.ImageGeneration;
using HomiliaMessenger.Presentation.Desktop.Base;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Presentation.Desktop.ViewModels;

/// <summary>
/// Generating a contact photo from a description of the person: describe them, generate, look at the result, and
/// generate again or use it. Nothing changes on the contact until the user chooses to use a photo.
/// </summary>
public partial class GeneratePhotoViewModel : ObservableObject, IDisposable
{
    private readonly IPortraitService _portraits;
    private CancellationTokenSource? _generating;
    private byte[]? _photo;
    private bool _disposed;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
    private string _description;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GenerateLabel), nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand), nameof(UsePhotoCommand), nameof(StopCommand))]
    private bool _isGenerating;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview), nameof(GenerateLabel))]
    [NotifyCanExecuteChangedFor(nameof(UsePhotoCommand))]
    private Bitmap? _preview;

    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private bool _isProgressIndeterminate = true;
    [ObservableProperty] private string _status = string.Empty;

    /// <summary>
    /// Whether a model must be downloaded in Settings before photos can be generated.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
    private bool _needsModel;

    /// <summary>
    /// Whether image generation is missing from this build or system, so Settings cannot help.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
    private bool _isUnsupported;

    /// <param name="description">The description used last time, kept so it can be adjusted rather than retyped.</param>
    public GeneratePhotoViewModel(IPortraitService portraits, string? description = null)
    {
        _portraits = portraits ?? throw new ArgumentNullException(nameof(portraits));
        _description = description ?? string.Empty;
        RefreshAvailability();
    }

    /// <summary>
    /// Raised when the dialog should close: after a photo is chosen, or to go to Settings.
    /// </summary>
    public event EventHandler? CloseRequested;

    public int MaxDescriptionLength => PortraitRequest.MAX_DESCRIPTION_LENGTH;

    public string DescriptionHint => "Describe the person: age, appearance, and any clothing or setting. For example: \"An older man with a long white beard and glasses, wearing a plaid shirt, in his workshop.\"";

    public bool HasPreview => Preview is not null;

    public bool IsIdle => !IsGenerating;

    public string GenerateLabel => HasPreview ? "Generate another" : "Generate";

    /// <summary>
    /// The PNG photo chosen with "Use photo", or <see langword="null"/> when none was.
    /// </summary>
    public byte[]? ChosenPhoto { get; private set; }

    /// <summary>
    /// Whether the user asked to go to Settings to download an image model.
    /// </summary>
    public bool WantsSettings { get; private set; }

    private void RefreshAvailability()
    {
        ImageGeneratorAvailability availability = _portraits.GetAvailability();
        IsUnsupported = !availability.IsRuntimeIncluded;
        NeedsModel = availability.IsRuntimeIncluded && !availability.IsReady;

        if (!availability.IsReady)
        {
            Status = NeedsModel
                ? "To generate photos, download an image model in Settings, under Image Generation. It runs on this computer."
                : availability.Problem ?? "Image generation is not available.";
        }
    }

    private bool CanGenerate()
    {
        return !IsGenerating && !NeedsModel && !IsUnsupported && !string.IsNullOrWhiteSpace(Description);
    }

    /// <summary>
    /// Generates a photo from the description, or another one with a fresh seed. The photo shown is replaced only once
    /// the new one is ready.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private async Task GenerateAsync()
    {
        using CancellationTokenSource generating = new();
        _generating = generating;
        IsGenerating = true;
        IsProgressIndeterminate = true;
        ProgressPercent = 0;
        Status = "Getting ready…";

        Progress<ImageGenerationProgress> progress = new(ShowProgress);

        try
        {
            PortraitGeneration generation = await _portraits.GenerateAsync(new PortraitRequest(Description), progress, generating.Token);

            if (!generation.Validation.IsValid)
            {
                Status = generation.Validation.ToStringPretty("Photo");
                return;
            }

            ImageGenerationResult result = generation.Result!;

            switch (result.Status)
            {
                case ImageGenerationStatus.Success:
                    ShowPhoto(result.Png!);
                    break;
                case ImageGenerationStatus.Canceled:
                    Status = HasPreview ? "Stopped. The photo shown is unchanged." : "Stopped.";
                    break;
                case ImageGenerationStatus.Unavailable:
                    RefreshAvailability();
                    Status = result.Problem ?? Status;
                    break;
                default:
                    Status = result.Problem ?? "The photo could not be generated.";
                    break;
            }
        }
        catch (Exception error)
        {
            Status = "Could not generate the photo: " + error.Message;
        }
        finally
        {
            _generating = null;
            IsGenerating = false;
            IsProgressIndeterminate = false;
            ProgressPercent = 0;
        }
    }

    private void ShowProgress(ImageGenerationProgress update)
    {
        // Progress still arriving after "Stop" must not hide that it is stopping.
        if (!IsGenerating || _generating?.IsCancellationRequested == true)
        {
            return;
        }

        switch (update.Stage)
        {
            case ImageGenerationStage.Loading:
                IsProgressIndeterminate = true;
                Status = "Getting ready…";
                break;
            case ImageGenerationStage.Preparing:
                IsProgressIndeterminate = true;
                Status = "Reading the description…";
                break;
            case ImageGenerationStage.Sampling when update.Total > 0:
                IsProgressIndeterminate = false;
                ProgressPercent = 10 + (75d * Math.Clamp(update.Completed, 0, update.Total) / update.Total);
                Status = "Drawing…";
                break;
            case ImageGenerationStage.Decoding:
                IsProgressIndeterminate = false;
                ProgressPercent = 90;
                Status = "Finishing…";
                break;
        }
    }

    private void ShowPhoto(byte[] png)
    {
        PhotoFile.EnsureSupported(png);
        Bitmap? previous = Preview;
        _photo = png;
        Preview = PhotoFile.TryPreview(png);
        previous?.Dispose();
        Status = Preview is null
            ? "A photo was generated, but it cannot be shown here. You can still use it."
            : "Use this photo, or generate another from the same description.";
    }

    private bool CanStop()
    {
        return IsGenerating;
    }

    /// <summary>
    /// Asks the generation to stop. It stops between drawing steps; the last stage finishes first.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        if (_generating is { IsCancellationRequested: false } generating)
        {
            generating.Cancel();
            Status = "Stopping…";
        }
    }

    private bool CanUsePhoto()
    {
        return !IsGenerating && _photo is not null;
    }

    [RelayCommand(CanExecute = nameof(CanUsePhoto))]
    private void UsePhoto()
    {
        ChosenPhoto = _photo is null ? null : [.. _photo];
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void OpenSettings()
    {
        WantsSettings = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Stops any generation in progress, as when the dialog is closed.
    /// </summary>
    public void CancelGeneration()
    {
        _generating?.Cancel();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelGeneration();
        Preview?.Dispose();
        GC.SuppressFinalize(this);
    }
}
