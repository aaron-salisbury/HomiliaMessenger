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
/// Finds GGUF models in Hugging Face repositories, through Hugging Face's public API, and holds the access token used for
/// gated repositories.
/// </summary>
internal sealed partial class HuggingFaceClient
{
    internal const string SITE = "https://huggingface.co/";
    internal const string RECOMMENDED_QUANTIZATION = "Q4_K_M";

    private static readonly CredentialReference TOKEN = new("huggingface-token");

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
        int site = value.IndexOf("huggingface.co/", StringComparison.OrdinalIgnoreCase);

        // An address, with or without its scheme.
        if (site >= 0 && (site == 0 || value[..site].EndsWith("://", StringComparison.Ordinal) || value[..site].EndsWith("://www.", StringComparison.OrdinalIgnoreCase)))
        {
            value = value[(site + "huggingface.co/".Length)..];
        }

        string[] parts = value.Split('/', StringSplitOptions.RemoveEmptyEntries);

        return parts.Length >= 2 && RepositoryPart().IsMatch(parts[0]) && RepositoryPart().IsMatch(parts[1]) ? $"{parts[0]}/{parts[1]}" : null;
    }

    internal async Task<HuggingFaceLookup> LookUpAsync(string repository, CancellationToken cancellationToken)
    {
        string? repoId = ParseRepository(repository);

        if (repoId is null)
        {
            return HuggingFaceLookup.Unsuccessful(HuggingFaceLookupStatus.NotFound, repository, "Enter a repository as owner/name, such as Qwen/Qwen2.5-1.5B-Instruct-GGUF, or paste its address.");
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

            if (gated && token is null)
            {
                return HuggingFaceLookup.Unsuccessful(HuggingFaceLookupStatus.NeedsToken, repoId, "This repository is gated. Accept its terms on Hugging Face, then save an access token here.");
            }

            List<HuggingFaceFile> files = [];
            string? next = $"{SITE}api/models/{repoId}/tree/{revision}?recursive=true";

            // The file list comes in pages, linked from each response.
            while (next is not null)
            {
                using HttpResponseMessage page = await SendAsync(client, next, token, cancellationToken).ConfigureAwait(false);

                if (Unavailable(page, repoId, token) is { } listProblem)
                {
                    return listProblem;
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

            IReadOnlyList<GgufVariant> variants = Variants(files);

            return variants.Count == 0
                ? HuggingFaceLookup.Unsuccessful(HuggingFaceLookupStatus.NoModels, repoId, "This repository has no GGUF model files. Look for a repository whose name ends in -GGUF.")
                : new HuggingFaceLookup(HuggingFaceLookupStatus.Found, repoId, revision, license, variants, null);
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException
            || (error is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return HuggingFaceLookup.Unsuccessful(HuggingFaceLookupStatus.Failed, repoId, "Could not reach Hugging Face: " + error.Message);
        }
    }

    /// <summary>
    /// The GGUF models among <paramref name="files"/>, smallest first. A model split across numbered files is one variant;
    /// vision projector files, which are not models on their own, are left out.
    /// </summary>
    internal static IReadOnlyList<GgufVariant> Variants(IEnumerable<HuggingFaceFile> files)
    {
        List<(string Key, List<HuggingFaceFile> Files)> groups = [];
        Dictionary<string, int> expectedParts = new(StringComparer.Ordinal);

        foreach (HuggingFaceFile file in files.Where(file => file.Path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
            && !Path.GetFileName(file.Path).Contains("mmproj", StringComparison.OrdinalIgnoreCase)))
        {
            Match split = SplitPart().Match(file.Path);
            string key = split.Success ? split.Groups["base"].Value : file.Path;

            if (split.Success)
            {
                expectedParts[key] = int.Parse(split.Groups["count"].Value);
            }

            int index = groups.FindIndex(group => group.Key == key);

            if (index < 0)
            {
                groups.Add((key, [file]));
            }
            else
            {
                groups[index].Files.Add(file);
            }
        }

        List<GgufVariant> variants = [];

        foreach ((string key, List<HuggingFaceFile> parts) in groups)
        {
            // An incomplete split model cannot be loaded.
            if (expectedParts.TryGetValue(key, out int count) && parts.Count != count)
            {
                continue;
            }

            parts.Sort((first, second) => string.CompareOrdinal(first.Path, second.Path));
            variants.Add(new GgufVariant(Quantization(key) ?? Path.GetFileNameWithoutExtension(key), parts, false));
        }

        // Where two variants share a quantization (in different folders, say), tell them apart by name.
        foreach (IGrouping<string, GgufVariant> same in variants.GroupBy(variant => variant.Label, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1).ToList())
        {
            foreach (GgufVariant variant in same.ToList())
            {
                variants[variants.IndexOf(variant)] = variant with { Label = Path.GetFileNameWithoutExtension(SplitPart().Replace(variant.Files[0].Path, "${base}")) };
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

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex RepositoryPart();

    [GeneratedRegex(@"^(?<base>.+)-(?<part>\d{5})-of-(?<count>\d{5})\.gguf$", RegexOptions.IgnoreCase)]
    private static partial Regex SplitPart();

    [GeneratedRegex(@"(?:^|[-_.])(?<quant>I?Q\d+(?:_[A-Z0-9]+)*|BF16|FP16|F16|F32)(?=$|[-_.])", RegexOptions.IgnoreCase)]
    private static partial Regex QuantizationName();

    [GeneratedRegex(@"<(?<url>[^>]+)>\s*;\s*rel=""?next""?")]
    private static partial Regex NextLink();
}
