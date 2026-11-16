using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace HomiliaMessenger.Business.Modules.WebSearch;

/// <summary>
/// Turns search results into the few short lines a contact is given, within a fixed budget, so a small local model is
/// not flooded and retrieved text cannot pass for instructions.
/// </summary>
/// <remarks>
/// Each result becomes a single line in a list the instructions introduce as information found online, with line breaks,
/// chat-template tokens, Markdown headings, and speaker labels removed, so a page cannot open a section of its own,
/// start a new turn, or pose as the system. The instructions also tell the model to ignore any instructions in it.
/// </remarks>
internal static partial class WebContext
{
    internal const int MAX_RESULTS = 3;
    internal const int MAX_TITLE_CHARACTERS = 100;
    internal const int MAX_SNIPPET_CHARACTERS = 280;

    /// <summary>
    /// One line per result, best first, at most <see cref="MAX_RESULTS"/>; results without usable text are skipped.
    /// </summary>
    internal static IReadOnlyList<string> Lines(IReadOnlyList<WebSearchResult>? results)
    {
        List<string> lines = [];

        foreach (WebSearchResult result in results ?? [])
        {
            if (lines.Count == MAX_RESULTS)
            {
                break;
            }

            string title = Clean(result.Title, MAX_TITLE_CHARACTERS);
            string snippet = Clean(result.Snippet, MAX_SNIPPET_CHARACTERS);

            if (snippet.Length == 0 && title.Length == 0)
            {
                continue;
            }

            StringBuilder line = new();
            line.Append(title.Length > 0 ? $"\"{title}\"" : "A page");

            List<string> details = [];

            if (Site(result.Url) is { } site)
            {
                details.Add(site);
            }

            if (result.PublishedAt is { } published)
            {
                details.Add("published " + published.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture));
            }

            if (details.Count > 0)
            {
                line.Append($" ({string.Join(", ", details)})");
            }

            if (snippet.Length > 0)
            {
                line.Append(": ").Append(snippet);
            }

            lines.Add(line.ToString());
        }

        return lines;
    }

    /// <summary>
    /// The host name of an <c>http</c> or <c>https</c> address, without "www.".
    /// </summary>
    internal static string? Site(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return null;
        }

        return uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
    }

    /// <summary>
    /// Retrieved text as one plain line of at most <paramref name="maxLength"/> characters.
    /// </summary>
    internal static string Clean(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string cleaned = new([.. text.Select(character => char.IsControl(character) ? ' ' : character)]);
        cleaned = TemplateToken().Replace(cleaned, " ");
        cleaned = Heading().Replace(cleaned, " ");
        cleaned = SpeakerLabel().Replace(cleaned, " ");
        cleaned = cleaned.Replace('"', '\'');
        cleaned = Whitespace().Replace(cleaned, " ").Trim();

        if (cleaned.Length > maxLength)
        {
            int end = cleaned.LastIndexOf(' ', maxLength - 1);
            cleaned = cleaned[..(end > maxLength / 2 ? end : maxLength - 1)].TrimEnd(',', ';', ':', ' ') + "…";
        }

        return cleaned;
    }

    // Chat-template markers such as "<|im_start|>", "[INST]", or "<s>".
    [GeneratedRegex(@"<\|[^|>]{0,40}\|>|\[/?INST\]|</?s>|<<\s*/?SYS\s*>>|</?(?:system|user|assistant)>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TemplateToken();

    [GeneratedRegex(@"#{1,6}\s|-{3,}|\*{3,}|_{3,}|={3,}")]
    private static partial Regex Heading();

    // "System:", "User:", "Assistant:", or "Instructions:" labels that could pose as a new turn or section.
    [GeneratedRegex(@"\b(?:system|user|assistant|instructions?|developer)\s*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SpeakerLabel();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
