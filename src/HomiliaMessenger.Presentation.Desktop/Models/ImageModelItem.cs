using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HomiliaMessenger.Business.Modules.Agents;
using HomiliaMessenger.Business.Modules.ImageGeneration;
using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Presentation.Desktop.Models;

/// <summary>
/// An image model in Settings, listed for one purpose: contact photos, or selfies (which also need face matching).
/// </summary>
public sealed partial class ImageModelItem : ObservableObject
{
    /// <summary>
    /// What downloading a model for selfies agrees to besides the model's own license: face matching's.
    /// </summary>
    internal const string FACE_MATCHING_NOTICE =
        " For selfies, face matching (IP-Adapter Plus-Face and its image encoder) is downloaded too, under the Apache 2.0 license.";

    /// <summary>
    /// The use restrictions every user of a CreativeML Open RAIL-M model must agree to (its paragraph 5 and Attachment A),
    /// in brief; the full license is linked and saved with the model.
    /// </summary>
    internal const string OPENRAIL_NOTICE =
        "{0} is licensed under the CreativeML Open RAIL-M license. By downloading and using it, you agree to its terms, including the use restrictions "
        + "in its Attachment A: for example, not to use it to break the law, harm or exploit minors, defame or harass people, spread false information "
        + "to harm others, generate personal information that could harm someone, or discriminate against people. Read the full license from the link "
        + "under the model in Settings. You are responsible for the images you make.";

    private readonly IImageModelLibrary _library;
    private readonly ImageModelPurpose _purpose;

    // Whether the model, listed for contact photos, also takes selfies, which removing it here affects.
    private readonly bool _takesSelfiesToo;

    private readonly Func<string, string, Task<bool>>? _confirmAsync;
    private readonly Action _changed;
    private CancellationTokenSource? _download;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload), nameof(CanRemove), nameof(CanChoose), nameof(SizeText))]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand), nameof(DeleteCommand), nameof(ChooseCommand))]
    private bool _isDownloaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload), nameof(CanRemove), nameof(CanChoose))]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand), nameof(DeleteCommand), nameof(CancelCommand), nameof(ChooseCommand))]
    private bool _isDownloading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChoose), nameof(SizeText))]
    [NotifyCanExecuteChangedFor(nameof(ChooseCommand))]
    private bool _isSelected;

    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string _status = string.Empty;

    /// <param name="purpose">What the model is listed for: contact photos or selfies.</param>
    /// <param name="changed">Called after the model is downloaded, chosen, or removed, which may change every item.</param>
    public ImageModelItem(ImageModel model, IImageModelLibrary library, Func<string, string, Task<bool>>? confirmAsync, Action changed, ImageModelPurpose purpose = ImageModelPurpose.Portraits)
    {
        ArgumentNullException.ThrowIfNull(model);
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _purpose = purpose;
        IsSharedWithPhotos = purpose == ImageModelPurpose.Selfies && model.Purposes.HasFlag(ImageModelPurpose.Portraits);
        _takesSelfiesToo = purpose == ImageModelPurpose.Portraits && model.Purposes.HasFlag(ImageModelPurpose.Selfies);
        _confirmAsync = confirmAsync;
        _changed = changed ?? throw new ArgumentNullException(nameof(changed));
        Id = model.Id;
        DisplayName = model.DisplayName;
        Description = model.Description;
        License = model.License;
        LicenseUrl = Uri.TryCreate(model.LicenseUrl, UriKind.Absolute, out Uri? url) ? url : null;
        DownloadBytes = model.DownloadBytes;
        IsUserAdded = model.IsUserAdded;
        IsVerified = model.IsVerified;
        SettingsText = Describe(model.Settings);
        _isDownloaded = model.IsDownloaded;
        _isSelected = model.IsSelected;
    }

    /// <summary>
    /// Raised when the model is no longer offered, after the user removed a model they added.
    /// </summary>
    public event EventHandler? Removed;

    public string Id { get; }
    public string DisplayName { get; }
    public string Description { get; }
    public string License { get; }
    public Uri? LicenseUrl { get; }
    public bool HasLicenseUrl => LicenseUrl is not null;
    public long DownloadBytes { get; }
    public bool IsUserAdded { get; }
    public bool IsVerified { get; }

    /// <summary>
    /// Whether this is a selfie model that also makes contact photos, so removing it here removes only face matching.
    /// </summary>
    public bool IsSharedWithPhotos { get; }

    public string RemoveHint => IsSharedWithPhotos ? "Remove face matching (the model stays for contact photos)" : "Remove model";

    private bool IsForSelfies => _purpose == ImageModelPurpose.Selfies;

    /// <summary>
    /// The model's drawing settings, shown for models the user added, whose settings they chose.
    /// </summary>
    public string SettingsText { get; }

    public string SizeText => $"{FormatSize(DownloadBytes)} · {License} · CPU" + (IsVerified ? string.Empty : " · Unverified")
        + (IsDownloaded ? IsSelected ? " · In use" : " · Downloaded" : string.Empty);

    public bool CanDownload => !IsDownloaded && !IsDownloading;

    public bool CanRemove => (IsDownloaded || IsUserAdded) && !IsDownloading;

    public bool CanChoose => IsDownloaded && !IsDownloading && !IsSelected;

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task DownloadAsync()
    {
        if (License.Contains("RAIL", StringComparison.OrdinalIgnoreCase) && _confirmAsync is not null
            && !await _confirmAsync("Model license", string.Format(CultureInfo.CurrentCulture, OPENRAIL_NOTICE, DisplayName) + (IsForSelfies ? FACE_MATCHING_NOTICE : string.Empty)))
        {
            return;
        }

        using CancellationTokenSource download = new();
        _download = download;
        IsDownloading = true;
        Status = "Starting download…";

        Progress<LocalModelDownloadProgress> progress = new(update =>
        {
            ProgressPercent = update.TotalBytes == 0 ? 0 : 100d * update.BytesDownloaded / update.TotalBytes;
            Status = update.BytesDownloaded >= update.TotalBytes
                ? "Verifying…"
                : $"{FormatSize(update.BytesDownloaded)} of {FormatSize(update.TotalBytes)}";
        });

        try
        {
            LocalModelDownloadResult result = await _library.DownloadAsync(Id, _purpose, progress, download.Token);

            Status = result.Status switch
            {
                LocalModelDownloadStatus.Success when IsForSelfies => "Ready. Ask for a selfie with the camera button in a chat with a contact who has a profile photo.",
                LocalModelDownloadStatus.Success => "Ready. Generate a photo from a contact's photo on the Contacts page.",
                LocalModelDownloadStatus.Canceled => "Download paused. Download again to resume.",
                _ => result.Problem ?? "The download failed."
            };
        }
        catch (Exception error)
        {
            Status = "The download failed: " + error.Message;
        }
        finally
        {
            _download = null;
            IsDownloading = false;
            _changed();
        }
    }

    private bool CanCancel()
    {
        return IsDownloading;
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _download?.Cancel();
    }

    [RelayCommand(CanExecute = nameof(CanChoose))]
    private void Choose()
    {
        try
        {
            if (_library.Select(Id, _purpose))
            {
                Status = IsForSelfies ? "New selfies are taken with this model." : "New photos are generated with this model.";
            }
        }
        catch (Exception error)
        {
            Status = "Could not choose the model: " + error.Message;
        }

        _changed();
    }

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private async Task DeleteAsync()
    {
        string kept = IsForSelfies ? "Selfies already in chats are kept." : "Photos already saved to contacts are kept.";
        string question = IsSharedWithPhotos
            ? $"Remove face matching and free {FormatSize(_library.GetFaceMatching().DiskBytes)}? Every selfie model needs it, so no selfies can be taken until it is downloaded again. {DisplayName} itself stays for contact photos; remove it under Profile photos. {kept}"
            : _takesSelfiesToo && IsUserAdded
                ? $"Take {DisplayName} off this list? It stays downloaded for selfies. {kept}"
                : _takesSelfiesToo
                    ? $"Remove {DisplayName} and free {FormatSize(DownloadBytes)}? It also takes selfies, so no selfies can be taken with it until it is downloaded again. {kept}"
                    : IsUserAdded
                        ? $"Remove {DisplayName}? Its files are deleted and it is taken off this list. {kept}"
                        : $"Remove {DisplayName} and free {FormatSize(DownloadBytes)}? {kept}";

        if (_confirmAsync is not null && !await _confirmAsync(IsSharedWithPhotos ? "Remove face matching" : "Remove model", question))
        {
            return;
        }

        try
        {
            await _library.DeleteAsync(Id, _purpose);
            Status = "Removed.";
        }
        catch (Exception error)
        {
            Status = "Could not remove the model: " + error.Message;
        }

        _changed();
    }

    internal void Refresh()
    {
        if (_library.List(_purpose).FirstOrDefault(model => model.Id == Id) is not { } model)

        {
            Removed?.Invoke(this, EventArgs.Empty);
            return;
        }

        IsDownloaded = model.IsDownloaded;
        IsSelected = model.IsSelected;
        ProgressPercent = model.IsDownloaded ? 100 : ProgressPercent;
    }

    internal static string Describe(ImageGenerationSettings settings)
    {
        string guidance = settings.GuidanceScale.ToString("0.##", CultureInfo.InvariantCulture);

        return $"{settings.Width}×{settings.Height} · {settings.Sampler} sampler · {settings.Steps} steps · CFG {guidance}";
    }

    private static string FormatSize(long bytes)
    {
        return bytes >= 1_000_000_000 ? $"{bytes / 1_000_000_000d:0.0} GB" : $"{bytes / 1_000_000d:0} MB";
    }
}
