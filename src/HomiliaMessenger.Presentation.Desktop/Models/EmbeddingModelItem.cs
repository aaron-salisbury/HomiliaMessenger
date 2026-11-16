using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HomiliaMessenger.Business.Modules.Agents;
using HomiliaMessenger.Business.Modules.Memories;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Presentation.Desktop.Models;

/// <summary>
/// An embedding model in Settings, which memories can be searched with once it is downloaded and chosen.
/// </summary>
public sealed partial class EmbeddingModelItem : ObservableObject
{
    private readonly IEmbeddingModelLibrary _library;
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
    public EmbeddingModelItem(EmbeddingModel model, IEmbeddingModelLibrary library, Func<string, string, Task<bool>>? confirmAsync, Action changed)
    {
        ArgumentNullException.ThrowIfNull(model);
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _confirmAsync = confirmAsync;
        _changed = changed ?? throw new ArgumentNullException(nameof(changed));
        Id = model.Id;
        DisplayName = model.DisplayName;
        Description = model.Description;
        License = model.License;
        DownloadBytes = model.DownloadBytes;
        _isDownloaded = model.IsDownloaded;
        _isSelected = model.IsSelected;
    }

    public string Id { get; }
    public string DisplayName { get; }
    public string Description { get; }
    public string License { get; }
    public long DownloadBytes { get; }

    public string SizeText => $"{DownloadBytes / 1_000_000d:0} MB · {License} license"
        + (IsDownloaded ? IsSelected ? " · In use" : " · Downloaded" : string.Empty);

    public bool CanDownload => !IsDownloaded && !IsDownloading;

    public bool CanRemove => IsDownloaded && !IsDownloading;

    public bool CanChoose => IsDownloaded && !IsDownloading && !IsSelected;

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
                : $"{update.BytesDownloaded / 1_000_000d:0} of {update.TotalBytes / 1_000_000d:0} MB";
        });

        try
        {
            LocalModelDownloadResult result = await _library.DownloadAsync(Id, progress, download.Token);

            Status = result.Status switch
            {
                LocalModelDownloadStatus.Success => "Ready.",
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
                Status = "Memories are being prepared for searching with this model. Your memories themselves are unchanged.";
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
        string question = IsSelected
            ? $"Remove {DisplayName}? Memories are kept, but are found by their words rather than their meaning until an embedding model is downloaded again."
            : $"Remove {DisplayName}? Memories are kept.";

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
            return;
        }

        IsDownloaded = model.IsDownloaded;
        IsSelected = model.IsSelected;
        ProgressPercent = model.IsDownloaded ? 100 : ProgressPercent;
    }
}
