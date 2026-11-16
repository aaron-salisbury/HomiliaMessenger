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
/// An image model in Settings, which contact photos can be generated with once it is downloaded.
/// </summary>
public sealed partial class ImageModelItem : ObservableObject
{
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

    /// <param name="changed">Called after the model is downloaded, chosen, or removed, which may change every item.</param>
    public ImageModelItem(ImageModel model, IImageModelLibrary library, Func<string, string, Task<bool>>? confirmAsync, Action changed)
    {
        ArgumentNullException.ThrowIfNull(model);
        _library = library ?? throw new ArgumentNullException(nameof(library));
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
            && !await _confirmAsync("Model license", string.Format(CultureInfo.CurrentCulture, OPENRAIL_NOTICE, DisplayName)))
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
            LocalModelDownloadResult result = await _library.DownloadAsync(Id, progress, download.Token);

            Status = result.Status switch
            {
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
            if (_library.Select(Id))
            {
                Status = "New photos are generated with this model.";
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
        string question = IsUserAdded
            ? $"Remove {DisplayName}? Its files are deleted and it is taken off this list. Photos already saved to contacts are kept."
            : $"Remove {DisplayName} and free {FormatSize(DownloadBytes)}? Photos already saved to contacts are kept.";

        if (_confirmAsync is not null && !await _confirmAsync("Remove model", question))
        {
            return;
        }

        try
        {
            await _library.DeleteAsync(Id);
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
        if (_library.List().FirstOrDefault(model => model.Id == Id) is not { } model)
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
