using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Tests.ImageGeneration;

/// <summary>
/// A stand-in for huggingface.co: JSON answers for API addresses and file contents (with range requests) for downloads.
/// </summary>
internal sealed class FakeHub : HttpMessageHandler, IHttpClientFactory
{
    internal const string SITE = "https://huggingface.co/";

    private readonly Lock _lock = new();
    private readonly List<string> _requested = [];

    public Dictionary<string, string> Json { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Addresses, after <see cref="SITE"/>, that fail as if the network were down.
    /// </summary>
    public HashSet<string> Unreachable { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// When set, a download is cut off after this many bytes, as an interrupted connection would be.
    /// </summary>
    public int? CutOffAfter { get; set; }

    public List<string> Requested
    {
        get
        {
            lock (_lock)
            {
                return [.. _requested];
            }
        }
    }

    public List<string> Ranges { get; } = [];

    public HttpClient CreateClient(string name)
    {
        return new HttpClient(this, disposeHandler: false);
    }

    /// <summary>
    /// Serves a repository with the given files at a revision, as Hugging Face's model API and file tree do.
    /// </summary>
    public void AddRepository(string repoId, string revision, string? license, string? pipelineTag, params (string Path, byte[] Content)[] files)
    {
        string card = license is null ? string.Empty : $$""","cardData":{"license":"{{license}}"}""";
        string pipeline = pipelineTag is null ? string.Empty : $$""","pipeline_tag":"{{pipelineTag}}" """;
        Json[$"api/models/{repoId}"] = $$"""{"id":"{{repoId}}","sha":"{{revision}}","gated":false{{card}}{{pipeline}}}""";
        Json[$"api/models/{repoId}/tree/{revision}?recursive=true"] = "[" + string.Join(",", files.Select(file =>
            $$$"""{"type":"file","path":"{{{file.Path}}}","size":{{{file.Content.Length}}},"lfs":{"oid":"{{{Sha256(file.Content)}}}"}}""")) + "]";

        foreach ((string path, byte[] content) in files)
        {
            Files[$"{repoId}/resolve/{revision}/{path}"] = content;
        }
    }

    internal static string Sha256(byte[] content)
    {
        return Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string address = request.RequestUri!.AbsoluteUri;
        string path = address.StartsWith(SITE, StringComparison.Ordinal) ? Uri.UnescapeDataString(address[SITE.Length..]) : address;

        lock (_lock)
        {
            _requested.Add(path);

            if (request.Headers.Range is { } range)
            {
                Ranges.Add($"{path} {range}");
            }
        }

        if (Unreachable.Contains(path))
        {
            throw new HttpRequestException("No connection could be made.");
        }

        if (Json.TryGetValue(path, out string? json))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }

        if (Files.TryGetValue(path, out byte[]? content))
        {
            long start = 0;
            long end = content.Length - 1;
            HttpStatusCode status = HttpStatusCode.OK;

            if (request.Headers.Range?.Ranges.FirstOrDefault() is { } requested)
            {
                start = requested.From ?? 0;
                end = Math.Min(requested.To ?? end, end);
                status = HttpStatusCode.PartialContent;
            }

            byte[] slice = content[(int)start..(int)(end + 1)];
            HttpContent body = CutOffAfter is { } limit && slice.Length > limit
                ? new StreamContent(new FailingStream(slice[..limit]))
                : new ByteArrayContent(slice);

            if (status == HttpStatusCode.PartialContent)
            {
                body.Headers.ContentRange = new ContentRangeHeaderValue(start, end, content.Length);
            }

            return Task.FromResult(new HttpResponseMessage(status) { Content = body });
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    /// <summary>
    /// Yields some bytes, then fails as a dropped connection does.
    /// </summary>
    private sealed class FailingStream : MemoryStream
    {
        public FailingStream(byte[] bytes) : base(bytes)
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = base.Read(buffer, offset, count);
            return read > 0 ? read : throw new IOException("The connection was reset.");
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await base.ReadAsync(buffer, cancellationToken);
            return read > 0 ? read : throw new IOException("The connection was reset.");
        }
    }
}

/// <summary>
/// Writes GGUF headers with the given metadata and tensors (names, shapes, and types only), as image and language models
/// have them.
/// </summary>
internal static class GgufTestFile
{
    internal const uint F16 = 1;
    internal const uint Q8_0 = 8;

    internal static byte[] Write(IReadOnlyList<(string Key, string Value)> metadata, IReadOnlyList<(string Name, long[] Shape, uint Type)> tensors, int padding = 64)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write(Encoding.ASCII.GetBytes("GGUF"));
        writer.Write(3u);
        writer.Write((long)tensors.Count);
        writer.Write((long)metadata.Count);

        foreach ((string key, string value) in metadata)
        {
            WriteString(writer, key);
            writer.Write(8u);
            WriteString(writer, value);
        }

        long offset = 0;

        foreach ((string name, long[] shape, uint type) in tensors)
        {
            WriteString(writer, name);
            writer.Write((uint)shape.Length);

            foreach (long dimension in shape)
            {
                writer.Write(dimension);
            }

            writer.Write(type);
            writer.Write(offset);
            offset += 32;
        }

        // Stands in for the weights after the header.
        writer.Write(new byte[padding]);

        return stream.ToArray();
    }

    /// <summary>
    /// The tensors of a Stable Diffusion checkpoint, in the layout stable-diffusion.cpp reads, as DreamShaper 8 has them.
    /// </summary>
    internal static List<(string Name, long[] Shape, uint Type)> StableDiffusion(long contextWidth = 768, bool textEncoder = true, bool vae = true, long inputChannels = 4, bool classEmbedding = false)
    {
        List<(string, long[], uint)> tensors =
        [
            ("model.diffusion_model.input_blocks.0.0.weight", [3, 3, inputChannels, 320], F16),
            ("model.diffusion_model.input_blocks.1.1.transformer_blocks.0.attn2.to_k.weight", [contextWidth, 320], Q8_0),
            ("model.diffusion_model.middle_block.1.transformer_blocks.0.attn2.to_k.weight", [contextWidth, 1280], Q8_0),
            ("model.diffusion_model.out.2.weight", [3, 3, 320, 4], F16)
        ];

        if (classEmbedding)
        {
            tensors.Add(("model.diffusion_model.label_emb.0.0.weight", [2816, 1280], F16));
        }

        if (textEncoder)
        {
            tensors.Add(("cond_stage_model.transformer.text_model.embeddings.token_embedding.weight", [768, 49408], F16));
        }

        if (vae)
        {
            tensors.Add(("first_stage_model.decoder.conv_in.weight", [3, 3, 4, 512], F16));
        }

        return tensors;
    }

    internal static byte[] StableDiffusion1()
    {
        return Write([], StableDiffusion());
    }

    internal static byte[] LanguageModel()
    {
        return Write([("general.architecture", "llama"), ("general.name", "Tiny")], [("token_embd.weight", [2048, 32000], Q8_0), ("blk.0.attn_q.weight", [2048, 2048], Q8_0)]);
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        writer.Write((long)bytes.Length);
        writer.Write(bytes);
    }
}
