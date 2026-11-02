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
/// <param name="IsUserAdded">
/// Whether the user added it from Hugging Face. Removing such a model forgets it as well as deleting its files.
/// </param>
public sealed record LocalModel(string Id, string DisplayName, string Description, string License, long DownloadBytes, bool IsDownloaded, bool IsUserAdded = false);

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
/// A file in a Hugging Face repository, with the SHA-256 hash Hugging Face publishes for it.
/// </summary>
/// <param name="Path">The file's path within the repository.</param>
public sealed record HuggingFaceFile(string Path, long Bytes, string Sha256);

/// <summary>
/// One way to download a model from a repository: a GGUF file, or the parts of one split across files.
/// </summary>
/// <param name="Label">The quantization, such as <c>Q4_K_M</c>, or the file name when it has none.</param>
/// <param name="IsRecommended">A good balance of quality, size, and speed on a CPU.</param>
public sealed record GgufVariant(string Label, IReadOnlyList<HuggingFaceFile> Files, bool IsRecommended)
{
    public long DownloadBytes
    {
        get
        {
            long bytes = 0;

            foreach (HuggingFaceFile file in Files)
            {
                bytes += file.Bytes;
            }

            return bytes;
        }
    }
}

public enum HuggingFaceLookupStatus
{
    Found,

    /// <summary>
    /// The repository does not exist, or is private.
    /// </summary>
    NotFound,

    /// <summary>
    /// The repository is gated: its terms must be accepted on Hugging Face, and an access token saved here.
    /// </summary>
    NeedsToken,

    /// <summary>
    /// The repository exists but has no GGUF model files.
    /// </summary>
    NoModels,
    Failed
}

/// <summary>
/// What a Hugging Face repository offers, at the revision it was looked up at.
/// </summary>
/// <param name="Revision">The commit the files belong to; downloads are pinned to it so the hashes stay valid.</param>
public sealed record HuggingFaceLookup(HuggingFaceLookupStatus Status, string RepoId, string? Revision, string? License, IReadOnlyList<GgufVariant> Variants, string? Problem)
{
    public static HuggingFaceLookup Unsuccessful(HuggingFaceLookupStatus status, string repoId, string problem)
    {
        return new(status, repoId, null, null, [], problem);
    }
}

/// <summary>
/// The outcome of adding a model. <see cref="ModelId"/> is the added model on success.
/// </summary>
public sealed record LocalModelAddResult(string? ModelId, string? Problem)
{
    public bool IsSuccessful => ModelId is not null;
}

/// <summary>
/// Capability to manage the models that run on this device: which are offered, adding more from Hugging Face, and
/// downloading or removing them.
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
    /// Removes a downloaded model, freeing its disk space; a model the user added is forgotten too. Returns
    /// <see langword="false"/> when there was nothing to remove.
    /// </summary>
    Task<bool> DeleteAsync(string modelId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the GGUF models in a Hugging Face repository, given as <c>owner/name</c> or its URL.
    /// </summary>
    Task<HuggingFaceLookup> LookUpAsync(string repository, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a variant from a lookup to the models offered, ready to download.
    /// </summary>
    Task<LocalModelAddResult> AddAsync(HuggingFaceLookup lookup, GgufVariant variant, CancellationToken cancellationToken = default);

    Task<bool> HasHuggingFaceTokenAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves the Hugging Face access token used for gated repositories, through the operating system's credential store,
    /// or forgets it when <paramref name="token"/> is blank.
    /// </summary>
    Task SetHuggingFaceTokenAsync(string? token, CancellationToken cancellationToken = default);
}
