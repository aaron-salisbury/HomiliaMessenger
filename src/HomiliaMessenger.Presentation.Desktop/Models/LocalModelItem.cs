using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HomiliaMessenger.Business.Modules.Agents;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Presentation.Desktop.Models;

/// <summary>
/// A downloadable local model in Settings, with its download progress and the actions available for it.
/// </summary>
public sealed partial class LocalModelItem : ObservableObject
{
    private readonly ILocalModelLibrary _library;
    private readonly Func<string, string, Task<bool>>? _confirmAsync;
    private CancellationTokenSource? _download;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload), nameof(CanRemove), nameof(SizeText))]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand), nameof(DeleteCommand))]
    private bool _isDownloaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload), nameof(CanRemove))]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand), nameof(DeleteCommand), nameof(CancelCommand))]
    private bool _isDownloading;

    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string _status = string.Empty;

    public LocalModelItem(LocalModel model, ILocalModelLibrary library, Func<string, string, Task<bool>>? confirmAsync)
    {
        ArgumentNullException.ThrowIfNull(model);
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _confirmAsync = confirmAsync;
        Id = model.Id;
        DisplayName = model.DisplayName;
        Description = model.Description;
        License = model.License;
        DownloadBytes = model.DownloadBytes;
        IsUserAdded = model.IsUserAdded;
        _isDownloaded = model.IsDownloaded;
    }

    /// <summary>
    /// Raised when the model is no longer offered, after the user removed a model they added.
    /// </summary>
    public event EventHandler? Removed;

    public string Id { get; }
    public string DisplayName { get; }
    public string Description { get; }
    public string License { get; }
    public long DownloadBytes { get; }

    /// <summary>
    /// Whether the user added it from Hugging Face; removing it also takes it off the list.
    /// </summary>
    public bool IsUserAdded { get; }

    public string SizeText => $"{FormatGigabytes(DownloadBytes)} · {License} license" + (IsDownloaded ? " · Downloaded" : string.Empty);

    public bool CanDownload => !IsDownloaded && !IsDownloading;

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task DownloadAsync()
    {
        using CancellationTokenSource download = new();
        _download = download;
        IsDownloading = true;
        Status = "Starting download…";

        Progress<LocalModelDownloadProgress> progress = new(update =>
        {
            ProgressPercent = update.TotalBytes == 0 ? 0 : 100d * update.BytesDownloaded / update.TotalBytes;
            Status = update.BytesDownloaded >= update.TotalBytes
                ? "Verifying…"
                : $"{FormatGigabytes(update.BytesDownloaded)} of {FormatGigabytes(update.TotalBytes)}";
        });

        try
        {
            LocalModelDownloadResult result = await _library.DownloadAsync(Id, progress, download.Token);

            Status = result.Status switch
            {
                LocalModelDownloadStatus.Success => "Ready to chat.",
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
            Refresh();
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

    /// <summary>
    /// A downloaded model can be removed to free its space; one the user added can be removed from the list at any time.
    /// </summary>
    public bool CanRemove => (IsDownloaded || IsUserAdded) && !IsDownloading;

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private async Task DeleteAsync()
    {
        string question = IsUserAdded
            ? $"Remove {DisplayName}? Its files are deleted and it is taken off this list; add it again from Hugging Face to use it. Contacts using it cannot reply until then."
            : $"Remove {DisplayName} and free {FormatGigabytes(DownloadBytes)}? Contacts using it cannot reply until it is downloaded again.";

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

        Refresh();
    }

    internal void Refresh()
    {
        LocalModel? model = _library.List().FirstOrDefault(model => model.Id == Id);

        if (model is null)
        {
            Removed?.Invoke(this, EventArgs.Empty);
            return;
        }

        IsDownloaded = model.IsDownloaded;
        ProgressPercent = model.IsDownloaded ? 100 : ProgressPercent;
    }

    private static string FormatGigabytes(long bytes)
    {
        return $"{bytes / 1_000_000_000d:0.0} GB";
    }
}
