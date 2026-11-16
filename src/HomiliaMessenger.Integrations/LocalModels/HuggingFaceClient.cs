using HomiliaMessenger.Business.Modules.Agents;
using HomiliaMessenger.Business.Modules.Credentials;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Integrations.LocalModels;

/// <summary>
/// A Hugging Face repository at the revision it was read at, with its large files.
/// </summary>
/// <param name="PipelineTag">What the repository says its model does, such as <c>text-to-image</c>.</param>
internal sealed record HuggingFaceRepository(string RepoId, string Revision, string? License, string? PipelineTag, bool IsGated, IReadOnlyList<HuggingFaceFile> Files);

/// <summary>
/// Finds GGUF models in Hugging Face repositories, through Hugging Face's public API, and holds the access token used for
/// gated repositories.
/// </summary>
internal sealed partial class HuggingFaceClient
{
    internal const string SITE = "https://huggingface.co/";
    internal const string RECOMMENDED_QUANTIZATION = "Q4_K_M";

    /// <summary>
    /// How many conversions of a model to offer, most downloaded first; popular models have hundreds.
    /// </summary>
    internal const int CONVERSIONS_SHOWN = 50;
    private const int NAME_MATCHES_SHOWN = 20;
    private const string SEARCH_FIELDS = "&expand%5B%5D=siblings&expand%5B%5D=gguf&expand%5B%5D=downloads&expand%5B%5D=gated&expand%5B%5D=tags";

    private static readonly CredentialReference TOKEN = new("huggingface-token");

    /// <summary>
    /// Hugging Face pages whose addresses look like <c>owner/name</c>, but are not models.
    /// </summary>
    private static readonly HashSet<string> NOT_MODELS = new(["datasets", "spaces", "collections", "papers", "organizations", "docs", "settings", "models"], StringComparer.OrdinalIgnoreCase);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ICredentialStore? _credentials;

    public HuggingFaceClient(IHttpClientFactory httpClientFactory, ICredentialStore? credentials)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _credentials = credentials;
    }

    /// <summary>
    /// The repository named by <paramref name="text"/>, given as <c>owner/name</c> or a Hugging Face URL, or
    /// <see langword="null"/> when it names none.
    /// </summary>
    internal static string? ParseRepository(string? text)
    {
        string value = (text ?? string.Empty).Trim();

        foreach (string host in (string[])["huggingface.co/", "hf.co/"])
        {
            int site = value.IndexOf(host, StringComparison.OrdinalIgnoreCase);

            // An address, with or without its scheme.
            if (site >= 0 && (site == 0 || value[..site].EndsWith("://", StringComparison.Ordinal) || value[..site].EndsWith("://www.", StringComparison.OrdinalIgnoreCase)))
            {
                value = value[(site + host.Length)..];
                break;
            }
        }

        // An address may end with a query, or a section of its page.
        int end = value.IndexOfAny(['?', '#']);
        string[] parts = (end >= 0 ? value[..end] : value).Split('/', StringSplitOptions.RemoveEmptyEntries);

        return parts.Length >= 2 && !NOT_MODELS.Contains(parts[0]) && IsRepositoryPart(parts[0]) && IsRepositoryPart(parts[1]) ? $"{parts[0]}/{parts[1]}" : null;
    }

    internal async Task<HuggingFaceLookup> LookUpAsync(string repository, CancellationToken cancellationToken)
    {
        string? repoId = ParseRepository(repository);

        if (repoId is null)
        {
            return HuggingFaceLookup.Unsuccessful(HuggingFaceLookupStatus.NotFound, repository, "Enter a model's repository as owner/name, such as Qwen/Qwen2.5-1.5B-Instruct, or paste its address from huggingface.co.");
        }

        string? token = await GetTokenAsync(cancellationToken).ConfigureAwait(false);
        HttpClient client = _httpClientFactory.CreateClient();

        try
        {
            using HttpResponseMessage info = await SendAsync(client, $"{SITE}api/models/{repoId}", token, cancellationToken).ConfigureAwait(false);

            if (Unavailable(info, repoId, token) is { } problem)
            {
                return problem;
            }

            using JsonDocument model = JsonDocument.Parse(await info.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            string revision = model.RootElement.GetProperty("sha").GetString()!;
            bool gated = model.RootElement.TryGetProperty("gated", out JsonElement gate) && gate.ValueKind != JsonValueKind.False;
            string? license = model.RootElement.TryGetProperty("cardData", out JsonElement card) && card.ValueKind == JsonValueKind.Object
                && card.TryGetProperty("license", out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

            // The model's information names its files even when it is gated, so a repository without GGUF files needs no
            // token to find conversions of it.
            if (model.RootElement.TryGetProperty("siblings", out JsonElement siblings) && siblings.ValueKind == JsonValueKind.Array
                && Variants(FileNames(siblings)).Count == 0)
            {
                return await FindConversionsAsync(client, repoId, token, cancellationToken).ConfigureAwait(false);
            }

            if (gated && token is null)
            {
                return HuggingFaceLookup.Unsuccessful(HuggingFaceLookupStatus.NeedsToken, repoId, "This repository is gated. Accept its terms on Hugging Face, then save an access token here.");
            }

            (List<HuggingFaceFile> files, HuggingFaceLookup? listProblem) = await ListFilesAsync(client, repoId, revision, token, cancellationToken).ConfigureAwait(false);

            if (listProblem is not null)
            {
                return listProblem;
            }

            IReadOnlyList<GgufVariant> variants = Variants(files);

            return variants.Count == 0
                ? await FindConversionsAsync(client, repoId, token, cancellationToken).ConfigureAwait(false)
                : new HuggingFaceLookup(HuggingFaceLookupStatus.Found, repoId, revision, license, variants, null);
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException
            || (error is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return HuggingFaceLookup.Unsuccessful(HuggingFaceLookupStatus.Failed, repoId, "Could not reach Hugging Face: " + error.Message);
        }
    }

    /// <summary>
    /// The large (LFS) files of a repository at <paramref name="revision"/>, with their sizes and SHA-256 hashes, or the
    /// problem that prevented listing them.
    /// </summary>
    private static async Task<(List<HuggingFaceFile> Files, HuggingFaceLookup? Problem)> ListFilesAsync(HttpClient client, string repoId, string revision, string? token, CancellationToken cancellationToken)
    {
        List<HuggingFaceFile> files = [];
        string? next = $"{SITE}api/models/{repoId}/tree/{revision}?recursive=true";

        // The file list comes in pages, linked from each response.
        while (next is not null)
        {
            using HttpResponseMessage page = await SendAsync(client, next, token, cancellationToken).ConfigureAwait(false);

            if (Unavailable(page, repoId, token) is { } problem)
            {
                return (files, problem);
            }

            using JsonDocument entries = JsonDocument.Parse(await page.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));

            foreach (JsonElement entry in entries.RootElement.EnumerateArray())
            {
                if (entry.GetProperty("type").GetString() == "file"
                    && entry.TryGetProperty("lfs", out JsonElement lfs) && lfs.ValueKind == JsonValueKind.Object
                    && lfs.TryGetProperty("oid", out JsonElement oid))
                {
                    files.Add(new HuggingFaceFile(entry.GetProperty("path").GetString()!, entry.GetProperty("size").GetInt64(), oid.GetString()!));
                }
            }

            next = NextPage(page);
        }

        return (files, null);
    }

    /// <summary>
    /// A repository's information and large files at its current revision, for models other than conversational ones,
    /// or the problem that prevented reading them (as an unsuccessful lookup).
    /// </summary>
    internal async Task<(HuggingFaceRepository? Repository, HuggingFaceLookup? Problem)> GetRepositoryAsync(string repository, CancellationToken cancellationToken)
    {
        string? repoId = ParseRepository(repository);

        if (repoId is null)
        {
            return (null, HuggingFaceLookup.Unsuccessful(HuggingFaceLookupStatus.NotFound, repository, "Enter a model's repository as owner/name, such as NishQ1/vora-create-sd15, or paste its address from huggingface.co."));
        }

        string? token = await GetTokenAsync(cancellationToken).ConfigureAwait(false);
        HttpClient client = _httpClientFactory.CreateClient();

        try
        {
            using HttpResponseMessage info = await SendAsync(client, $"{SITE}api/models/{repoId}", token, cancellationToken).ConfigureAwait(false);

            if (Unavailable(info, repoId, token) is { } problem)
            {
                return (null, problem);
            }

            using JsonDocument model = JsonDocument.Parse(await info.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            JsonElement root = model.RootElement;
            string revision = root.GetProperty("sha").GetString()!;
            bool gated = root.TryGetProperty("gated", out JsonElement gate) && gate.ValueKind is not (JsonValueKind.False or JsonValueKind.Null);
            string? license = root.TryGetProperty("cardData", out JsonElement card) && card.ValueKind == JsonValueKind.Object
                && card.TryGetProperty("license", out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            string? pipelineTag = root.TryGetProperty("pipeline_tag", out JsonElement pipeline) && pipeline.ValueKind == JsonValueKind.String ? pipeline.GetString() : null;

            if (gated && token is null)
            {
                return (null, HuggingFaceLookup.Unsuccessful(HuggingFaceLookupStatus.NeedsToken, repoId,
                    "This repository is gated. Accept its terms on Hugging Face, then save an access token under Local Models > Add from Hugging Face."));
            }

            (List<HuggingFaceFile> files, HuggingFaceLookup? listProblem) = await ListFilesAsync(client, repoId, revision, token, cancellationToken).ConfigureAwait(false);

            return listProblem is not null ? (null, listProblem) : (new HuggingFaceRepository(repoId, revision, license, pipelineTag, gated, files), null);
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException
            || (error is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return (null, HuggingFaceLookup.Unsuccessful(HuggingFaceLookupStatus.Failed, repoId, "Could not reach Hugging Face: " + error.Message));
        }
    }

    /// <summary>
    /// Reads the first <paramref name="bytes"/> bytes of a file in a repository with a range request, such as a model's
    /// header, without downloading the rest. A server that ignores the range is cut off after that many bytes.
    /// </summary>
    internal async Task<byte[]> ReadStartAsync(string repoId, string revision, string path, int bytes, CancellationToken cancellationToken)
    {
        string? token = await GetTokenAsync(cancellationToken).ConfigureAwait(false);
        string url = $"{SITE}{repoId}/resolve/{revision}/{string.Join('/', path.Split('/').Select(Uri.EscapeDataString))}";
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(0, bytes - 1);

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using HttpResponseMessage response = await _httpClientFactory.CreateClient().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        byte[] buffer = new byte[bytes];
        int total = 0;
        int read;

        while (total < bytes && (read = await stream.ReadAsync(buffer.AsMemory(total, bytes - total), cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
        }

        return total == bytes ? buffer : buffer[..total];
    }

    /// <summary>
    /// The address, after <see cref="SITE"/>, listing the GGUF repositories that Hugging Face's model tree records as
    /// quantizations of <paramref name="repoId"/>.
    /// </summary>
    internal static string ConversionSearch(string repoId)
    {
        return $"api/models?filter={Uri.EscapeDataString("base_model:quantized:" + repoId)}&filter=gguf&sort=downloads&direction=-1&limit={CONVERSIONS_SHOWN}{SEARCH_FIELDS}";
    }

    /// <summary>
    /// The address, after <see cref="SITE"/>, listing GGUF repositories whose names contain <paramref name="repoId"/>'s.
    /// </summary>
    internal static string NameSearch(string repoId)
    {
        return $"api/models?search={Uri.EscapeDataString(Name(repoId))}&filter=gguf&sort=downloads&direction=-1&limit={NAME_MATCHES_SHOWN}{SEARCH_FIELDS}";
    }

    /// <summary>
    /// The GGUF conversions of <paramref name="repoId"/>, a repository with no GGUF files of its own. Hugging Face's model
    /// tree records which repositories are quantizations of a model, and those with GGUF files are offered. When it records
    /// none, GGUF repositories with a similar name are offered instead, marked as unconfirmed.
    /// </summary>
    private static async Task<HuggingFaceLookup> FindConversionsAsync(HttpClient client, string repoId, string? token, CancellationToken cancellationToken)
    {
        try
        {
            (List<HuggingFaceConversion> conversions, bool more) = await SearchAsync(client, ConversionSearch(repoId), repoId, true, token, cancellationToken).ConfigureAwait(false);

            if (conversions.Count > 0)
            {
                return HuggingFaceLookup.Unsuccessful(HuggingFaceLookupStatus.NoModels, repoId,
                    $"{repoId} contains the original model but no GGUF downloads. Compatible GGUF conversions were found below; choose one to see its downloads.")
                    with
                { Conversions = conversions, HasMoreConversions = more };
            }

            (conversions, _) = await SearchAsync(client, NameSearch(repoId), repoId, false, token, cancellationToken).ConfigureAwait(false);

            return conversions.Count > 0
                ? HuggingFaceLookup.Unsuccessful(HuggingFaceLookupStatus.NoModels, repoId,
                    $"{repoId} contains the original model but no GGUF downloads, and Hugging Face lists no GGUF conversions of it. The repositories below have "
                    + "similar names but are not confirmed to be the same model; check a repository's page before choosing it.") with
                { Conversions = conversions }
                : HuggingFaceLookup.Unsuccessful(HuggingFaceLookupStatus.NoModels, repoId,
                    $"{repoId} has no GGUF downloads, and no GGUF conversions of it were found on Hugging Face. Conversions of new models often appear within "
                    + "days. You can also search huggingface.co for the model's name with \"GGUF\", then enter a repository you find here.");
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or InvalidOperationException
            || (error is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return HuggingFaceLookup.Unsuccessful(HuggingFaceLookupStatus.NoModels, repoId,
                $"{repoId} has no GGUF downloads, and searching Hugging Face for conversions of it failed: {error.Message} Try again in a moment.");
        }
    }

    private static async Task<(List<HuggingFaceConversion> Conversions, bool More)> SearchAsync(
        HttpClient client, string search, string repoId, bool verified, string? token, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendAsync(client, SITE + search, token, cancellationToken).ConfigureAwait(false);

        if (Unavailable(response, repoId, token) is { } problem)
        {
            throw new HttpRequestException(response.StatusCode == HttpStatusCode.TooManyRequests ? problem.Problem : $"Hugging Face answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        using JsonDocument results = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        List<HuggingFaceConversion> conversions = [];

        foreach (JsonElement entry in results.RootElement.EnumerateArray())
        {
            if (Conversion(entry, repoId, verified) is { } conversion
                && !conversions.Exists(existing => existing.RepoId.Equals(conversion.RepoId, StringComparison.OrdinalIgnoreCase)))
            {
                conversions.Add(conversion);
            }
        }

        return (conversions, NextPage(response) is not null);
    }

    /// <summary>
    /// The conversion a search result describes, or <see langword="null"/> when it offers no complete GGUF model or, for a
    /// match by name, declares that it derives from some model.
    /// </summary>
    private static HuggingFaceConversion? Conversion(JsonElement entry, string repoId, bool verified)
    {
        if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("id", out JsonElement id) || id.ValueKind != JsonValueKind.String
            || id.GetString() is not { } candidate || ParseRepository(candidate) != candidate || candidate.Equals(repoId, StringComparison.OrdinalIgnoreCase)
            || !entry.TryGetProperty("siblings", out JsonElement siblings) || siblings.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        IReadOnlyList<GgufVariant> variants = Variants(FileNames(siblings));

        if (variants.Count == 0)
        {
            return null;
        }

        // A name match that names its base model is not a quantization of this one, or the model tree would have listed it.
        if (!verified && (!Name(candidate).Contains(Name(repoId), StringComparison.OrdinalIgnoreCase)
            || (entry.TryGetProperty("tags", out JsonElement tags) && tags.ValueKind == JsonValueKind.Array && tags.EnumerateArray()
                .Any(tag => tag.ValueKind == JsonValueKind.String && tag.GetString()!.StartsWith("base_model:", StringComparison.OrdinalIgnoreCase)))))
        {
            return null;
        }

        long? parameters = entry.TryGetProperty("gguf", out JsonElement gguf) && gguf.ValueKind == JsonValueKind.Object && gguf.TryGetProperty("total", out JsonElement total)
            && total.ValueKind == JsonValueKind.Number && total.TryGetInt64(out long count) && count > 0 ? count : null;
        long downloads = entry.TryGetProperty("downloads", out JsonElement downloaded) && downloaded.ValueKind == JsonValueKind.Number
            && downloaded.TryGetInt64(out long times) ? Math.Max(times, 0) : 0;
        bool gated = entry.TryGetProperty("gated", out JsonElement gate) && gate.ValueKind is not (JsonValueKind.False or JsonValueKind.Null);

        return new HuggingFaceConversion(candidate, SITE + candidate, verified, [.. variants.Select(variant => variant.Label)], parameters, downloads, gated);
    }

    /// <summary>
    /// The files named in a repository's <c>siblings</c>, which give no sizes or hashes.
    /// </summary>
    private static IEnumerable<HuggingFaceFile> FileNames(JsonElement siblings)
    {
        foreach (JsonElement sibling in siblings.EnumerateArray())
        {
            if (sibling.ValueKind == JsonValueKind.Object && sibling.TryGetProperty("rfilename", out JsonElement name)
                && name.ValueKind == JsonValueKind.String && name.GetString() is { Length: > 0 } path)
            {
                yield return new HuggingFaceFile(path, 0, string.Empty);
            }
        }
    }

    private static string Name(string repoId)
    {
        return repoId[(repoId.IndexOf('/') + 1)..];
    }

    /// <summary>
    /// The GGUF models among <paramref name="files"/>, smallest first. A model split across numbered files is one variant;
    /// vision projector files, which are not models on their own, are left out.
    /// </summary>
    internal static IReadOnlyList<GgufVariant> Variants(IEnumerable<HuggingFaceFile> files)
    {
        List<(string Name, bool IsSplit, List<HuggingFaceFile> Files)> groups = [];
        Dictionary<string, int> expectedParts = new(StringComparer.Ordinal);

        foreach (HuggingFaceFile file in files.Where(file => file.Path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
            && !Path.GetFileName(file.Path).Contains("mmproj", StringComparison.OrdinalIgnoreCase)))
        {
            Match split = SplitPart().Match(file.Path);

            // Named as the whole model's file, so a dotted name such as model-v3.2 keeps its quantization. A split model is
            // grouped apart from a single file of the same name.
            string name = split.Success ? split.Groups["base"].Value + ".gguf" : file.Path;

            if (split.Success)
            {
                expectedParts[name] = int.Parse(split.Groups["count"].Value);
            }

            int index = groups.FindIndex(group => group.Name == name && group.IsSplit == split.Success);

            if (index < 0)
            {
                groups.Add((name, split.Success, [file]));
            }
            else
            {
                groups[index].Files.Add(file);
            }
        }

        List<GgufVariant> variants = [];

        foreach ((string name, bool isSplit, List<HuggingFaceFile> parts) in groups)
        {
            // An incomplete split model cannot be loaded.
            if (isSplit && parts.Count != expectedParts[name])
            {
                continue;
            }

            parts.Sort((first, second) => string.CompareOrdinal(first.Path, second.Path));
            variants.Add(new GgufVariant(Quantization(name) ?? Path.GetFileNameWithoutExtension(name), parts, false));
        }

        // Where two variants share a quantization (in different folders, say), tell them apart by name.
        foreach (IGrouping<string, GgufVariant> same in variants.GroupBy(variant => variant.Label, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1).ToList())
        {
            foreach (GgufVariant variant in same.ToList())
            {
                variants[variants.IndexOf(variant)] = variant with { Label = Path.GetFileNameWithoutExtension(SplitPart().Replace(variant.Files[0].Path, "${base}.gguf")) };
            }
        }

        GgufVariant? recommended = variants.FirstOrDefault(variant => variant.Label.Equals(RECOMMENDED_QUANTIZATION, StringComparison.OrdinalIgnoreCase))
            ?? variants.Where(variant => variant.Label.StartsWith("Q4_K", StringComparison.OrdinalIgnoreCase)).MinBy(variant => variant.DownloadBytes);

        return [.. variants
            .Select(variant => ReferenceEquals(variant, recommended) ? variant with { IsRecommended = true } : variant)
            .OrderBy(variant => variant.DownloadBytes)];
    }

    /// <summary>
    /// The quantization in a GGUF file name, such as <c>Q4_K_M</c> or <c>IQ4_XS</c>, or <see langword="null"/>.
    /// </summary>
    internal static string? Quantization(string path)
    {
        Match match = QuantizationName().Match(Path.GetFileNameWithoutExtension(path));

        return match.Success ? match.Groups["quant"].Value.ToUpperInvariant() : null;
    }

    internal async Task<AuthenticationHeaderValue?> AuthorizationAsync(CancellationToken cancellationToken)
    {
        string? token = await GetTokenAsync(cancellationToken).ConfigureAwait(false);

        return token is null ? null : new AuthenticationHeaderValue("Bearer", token);
    }

    internal async Task<bool> HasTokenAsync(CancellationToken cancellationToken)
    {
        return await GetTokenAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    internal async Task SetTokenAsync(string? token, CancellationToken cancellationToken)
    {
        if (_credentials is null)
        {
            throw new NotSupportedException("Saving an access token is not supported on this system.");
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            await _credentials.DeleteAsync(TOKEN, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _credentials.StoreAsync(TOKEN, new CredentialMaterial(token.Trim()), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<string?> GetTokenAsync(CancellationToken cancellationToken)
    {
        if (_credentials is null)
        {
            return null;
        }

        try
        {
            return (await _credentials.GetAsync(TOKEN, cancellationToken).ConfigureAwait(false))?.Value;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // An unavailable credential store only means gated repositories cannot be reached.
            return null;
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string url, string? token, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, url);

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static HuggingFaceLookup? Unavailable(HttpResponseMessage response, string repoId, string? token)
    {
        return response.StatusCode switch
        {
            HttpStatusCode.OK => null,
            HttpStatusCode.NotFound => HuggingFaceLookup.Unsuccessful(HuggingFaceLookupStatus.NotFound, repoId, $"There is no repository named {repoId}."),
            HttpStatusCode.TooManyRequests => HuggingFaceLookup.Unsuccessful(HuggingFaceLookupStatus.Failed, repoId, "Hugging Face is limiting requests right now. Wait a minute, then try again."),

            // Hugging Face answers this way both for gated repositories and for ones that do not exist or are private.
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => HuggingFaceLookup.Unsuccessful(HuggingFaceLookupStatus.NeedsToken, repoId, token is null
                ? $"{repoId} was not found, or is gated or private. If it is gated, accept its terms on Hugging Face, then save an access token here."
                : $"{repoId} was not found, or your access token cannot reach it. If it is gated, make sure you accepted its terms on Hugging Face."),
            _ => HuggingFaceLookup.Unsuccessful(HuggingFaceLookupStatus.Failed, repoId, $"Hugging Face answered {(int)response.StatusCode} {response.ReasonPhrase}.")
        };
    }

    private static string? NextPage(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out IEnumerable<string>? links))
        {
            return null;
        }

        Match next = NextLink().Match(string.Join(",", links));

        return next.Success ? next.Groups["url"].Value : null;
    }

    /// <summary>
    /// Whether <paramref name="part"/> can be an owner or model name, under Hugging Face's naming rules.
    /// </summary>
    private static bool IsRepositoryPart(string part)
    {
        return part.Length <= 96 && RepositoryPart().IsMatch(part) && !part.Contains("..", StringComparison.Ordinal) && !part.Contains("--", StringComparison.Ordinal);
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex RepositoryPart();

    [GeneratedRegex(@"^(?<base>.+)-(?<part>\d{5})-of-(?<count>\d{5})\.gguf$", RegexOptions.IgnoreCase)]
    private static partial Regex SplitPart();

    [GeneratedRegex(@"(?:^|[-_.])(?<quant>I?Q\d+(?:_[A-Z0-9]+)*|BF16|FP16|F16|F32)(?=$|[-_.])", RegexOptions.IgnoreCase)]
    private static partial Regex QuantizationName();

    [GeneratedRegex(@"<(?<url>[^>]+)>\s*;\s*rel=""?next""?")]
    private static partial Regex NextLink();
}
