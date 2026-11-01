using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Business.Modules.Agents;

/// <summary>
/// A model that can run on this device, and whether it has been downloaded.
/// </summary>
/// <param name="Id">The model name contacts are assigned, such as <c>phi-3.5-mini</c>.</param>
/// <param name="DownloadBytes">The total size of the model's files.</param>
public sealed record LocalModel(string Id, string DisplayName, string Description, string License, long DownloadBytes, bool IsDownloaded);

public sealed record LocalModelDownloadProgress(long BytesDownloaded, long TotalBytes);

public enum LocalModelDownloadStatus
{
    Success,
    Canceled,
    Failed
}

public sealed record LocalModelDownloadResult(LocalModelDownloadStatus Status, string? Problem)
{
    public static LocalModelDownloadResult Success { get; } = new(LocalModelDownloadStatus.Success, null);
    public static LocalModelDownloadResult Canceled { get; } = new(LocalModelDownloadStatus.Canceled, null);

    public static LocalModelDownloadResult Failed(string problem)
    {
        return new(LocalModelDownloadStatus.Failed, problem);
    }
}

/// <summary>
/// Capability to manage the models that run on this device: which are offered, and downloading or removing them.
/// </summary>
public interface ILocalModelLibrary
{
    /// <summary>
    /// The provider name to assign contacts that should use a local model.
    /// </summary>
    string Provider { get; }

    IReadOnlyList<LocalModel> List();

    /// <summary>
    /// Downloads and verifies a model. An interrupted download resumes where it stopped the next time.
    /// </summary>
    Task<LocalModelDownloadResult> DownloadAsync(string modelId, IProgress<LocalModelDownloadProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a downloaded model, freeing its disk space. Returns <see langword="false"/> when it was not downloaded.
    /// </summary>
    Task<bool> DeleteAsync(string modelId, CancellationToken cancellationToken = default);
}
