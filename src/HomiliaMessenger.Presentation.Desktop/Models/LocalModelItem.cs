using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HomiliaMessenger.Business.Modules.Agents;
using System;
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
    [NotifyPropertyChangedFor(nameof(CanDownload), nameof(SizeText))]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand), nameof(DeleteCommand))]
    private bool _isDownloaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
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
        _isDownloaded = model.IsDownloaded;
    }

    public string Id { get; }
    public string DisplayName { get; }
    public string Description { get; }
    public string License { get; }
    public long DownloadBytes { get; }

    /// <summary>
    /// How to assign this model to a contact.
    /// </summary>
    public string AssignmentHint => $"Provider \"{_library.Provider}\", model \"{Id}\"";

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

    private bool CanDelete()
    {
        return IsDownloaded && !IsDownloading;
    }

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteAsync()
    {
        if (_confirmAsync is not null && !await _confirmAsync("Remove model", $"Remove {DisplayName} and free {FormatGigabytes(DownloadBytes)}? Contacts using it cannot reply until it is downloaded again."))
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
        foreach (LocalModel model in _library.List())
        {
            if (model.Id == Id)
            {
                IsDownloaded = model.IsDownloaded;
                ProgressPercent = model.IsDownloaded ? 100 : ProgressPercent;
            }
        }
    }

    private static string FormatGigabytes(long bytes)
    {
        return $"{bytes / 1_000_000_000d:0.0} GB";
    }
}
