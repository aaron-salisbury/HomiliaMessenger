using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace HomiliaMessenger.Business.Modules.ImageGeneration;

/// <summary>
/// A description of a person's photo, split into who they are, what they wear, and where they are.
/// </summary>
/// <param name="Person">The person and their attributes, such as "older man with a long white beard and eyeglasses".</param>
public sealed record PortraitParts(string? Person, string? Clothing, string? Setting);

/// <summary>
/// Turns a user's description of a person into a short, subject-first portrait prompt: the person and their attributes,
/// then clothing, then framing, then the setting, then a brief style phrase. The user's own words are kept as written;
/// nothing about who the person is (age, gender, ethnicity, build) is ever added.
/// </summary>
/// <remarks>
/// The order and wording follow the local portrait experiment, in which concise subject-first prompts rendered requested
/// characters far more faithfully than a long photographic preamble placed before the subject, and a close
/// head-and-shoulders crop kept unrequested hands and props out of frame. Stable Diffusion 1.x reads prompts in chunks of
/// 75 content tokens; staying within one chunk is a quality preference, not a limit, so the user's words are never cut.
/// </remarks>
public static partial class PortraitPromptComposer
{
    /// <summary>
    /// The CLIP tokens in one chunk of a Stable Diffusion 1.x prompt, excluding its start and end tokens.
    /// </summary>
    public const int CLIP_CHUNK_TOKENS = 75;

    internal const string CLOSE_FRAMING = "Close head-and-shoulders portrait, face filling the frame, looking at the camera.";
    internal const string WAIST_UP_FRAMING = "Waist-up portrait, facing the camera.";
    internal const string SHORT_CLOSE_FRAMING = "Head-and-shoulders portrait.";
    internal const string SHORT_WAIST_UP_FRAMING = "Waist-up portrait.";
    internal const string STYLE = "Soft natural light, polished photographic style.";
    internal const string STYLE_WITHOUT_SETTING = "Soft natural light, simple background, polished photographic style.";
    internal const string SHORT_STYLE = "Polished photographic style.";

    /// <summary>
    /// Used only when the description names no person at all, such as "in a park"; it says nothing about who they are.
    /// </summary>
    internal const string UNNAMED_PERSON = "Portrait of a person";

    private static readonly HashSet<string> GARMENTS = new(StringComparer.OrdinalIgnoreCase)
    {
        "shirt", "shirts", "t-shirt", "tee", "blouse", "sweater", "cardigan", "hoodie", "sweatshirt", "jacket", "coat", "parka", "raincoat",
        "windbreaker", "blazer", "suit", "tuxedo", "tie", "bowtie", "vest", "dress", "gown", "skirt", "jeans", "pants", "trousers", "shorts",
        "overalls", "uniform", "scrubs", "apron", "robe", "kimono", "sari", "hijab", "turban", "headscarf", "scarf", "hat", "cap", "beanie",
        "helmet", "turtleneck", "polo", "top", "flannel", "jersey", "necklace", "earrings", "jewelry", "outfit", "clothes", "clothing", "attire",
        "costume", "cassock", "habit", "gi", "lab coat", "kilt", "leotard", "swimsuit", "bikini"
    };

    /// <summary>
    /// Clothing seen in full only below the chest, so asking for it needs a waist-up crop rather than a close one.
    /// </summary>
    private static readonly HashSet<string> WAIST_UP_GARMENTS = new(StringComparer.OrdinalIgnoreCase)
    {
        "overalls", "dress", "gown", "skirt", "jeans", "pants", "trousers", "shorts", "apron", "kilt", "belt", "uniform", "costume", "outfit"
    };

    private static readonly HashSet<string> PLACES = new(StringComparer.OrdinalIgnoreCase)
    {
        "workshop", "garage", "garden", "park", "kitchen", "office", "library", "farm", "field", "fields", "barn", "beach", "forest", "woods",
        "trail", "mountain", "mountains", "lake", "river", "city", "street", "cafe", "café", "coffee", "restaurant", "bar", "pub", "classroom",
        "school", "hospital", "church", "studio", "home", "house", "room", "porch", "backyard", "yard", "store", "shop", "market", "bakery",
        "gym", "stage", "boat", "dock", "harbor", "harbour", "desert", "snow", "meadow", "vineyard", "orchard", "ranch", "campus", "lab",
        "laboratory", "station", "airport", "bookstore", "window", "fireplace", "background", "countryside", "village", "town",
        "neighborhood", "neighbourhood", "deck", "patio", "pool", "sea", "ocean", "shore", "hills", "valley", "plaza", "square", "alley",
        "bridge", "train", "bus", "car", "truck", "tractor", "sunset", "sunrise", "night", "rain", "concert", "festival", "wedding",
        "party", "living", "bedroom", "den", "study", "attic", "cabin", "lodge", "hotel", "lobby", "museum", "gallery", "theater", "theatre",
        "arena", "stadium", "court", "courtyard", "greenhouse", "nursery", "clinic", "pharmacy", "factory", "warehouse", "construction",
        "dining", "diner", "pier", "marina", "jungle", "savanna", "tundra", "canyon", "cliff", "waterfall", "outdoors", "indoors", "outside"
    };

    /// <summary>
    /// Words by which a description chooses its own framing; then none is added.
    /// </summary>
    private static readonly string[] FRAMING_WORDS =
    [
        "close-up", "closeup", "close up", "headshot", "head shot", "head-and-shoulders", "head and shoulders", "waist-up", "waist up",
        "half-body", "half body", "full body", "full-body", "full-length", "full length", "upper body", "bust portrait"
    ];

    /// <summary>
    /// Composes the prompt for <paramref name="description"/>. The same description always gives the same prompt.
    /// </summary>
    public static string Compose(string description)
    {
        ArgumentNullException.ThrowIfNull(description);

        List<string> person = [];
        List<string> clothing = [];
        List<string> setting = [];

        foreach (string sentence in ClauseSeparator().Split(Normalize(description)))
        {
            ClassifyClauses(sentence.Trim(), person, clothing, setting);
        }

        string normalized = Normalize(description);
        bool hasOwnFraming = FRAMING_WORDS.Any(word => normalized.Contains(word, StringComparison.OrdinalIgnoreCase));
        bool waistUp = Words(normalized).Any(WAIST_UP_GARMENTS.Contains) || BehindSubject().IsMatch(normalized);

        string subject = person.Count == 0 ? UNNAMED_PERSON : Capitalize(string.Join(", ", person));
        // "Farmer in denim overalls" reads as one phrase; "Older man, wearing a plaid shirt" as two.
        string firstSentence = subject + string.Concat(clothing.Select(clause => (clause.StartsWith("in ", StringComparison.OrdinalIgnoreCase) ? " " : ", ") + clause)) + ".";
        string? settingSentence = setting.Count == 0 ? null : Capitalize(string.Join(", ", setting)) + ".";

        string[] framings = hasOwnFraming ? [string.Empty] : waistUp ? [WAIST_UP_FRAMING, SHORT_WAIST_UP_FRAMING] : [CLOSE_FRAMING, SHORT_CLOSE_FRAMING];
        string[] styles = settingSentence is null ? [STYLE_WITHOUT_SETTING, SHORT_STYLE] : [STYLE, SHORT_STYLE];

        // Prefer the fuller framing and style, shortening the added words (never the user's) until it fits one chunk.
        string prompt = Join(firstSentence, framings[0], settingSentence, styles[0]);

        foreach ((string framing, string style) in new[] { (framings[0], styles[^1]), (framings[^1], styles[^1]) })
        {
            if (EstimateTokens(prompt) <= CLIP_CHUNK_TOKENS)
            {
                break;
            }

            prompt = Join(firstSentence, framing, settingSentence, style);
        }

        return prompt;
    }

    /// <summary>
    /// Splits <paramref name="description"/> as <see cref="Compose"/> reads it: the person and their attributes, their
    /// clothing, and the setting, each <see langword="null"/> when the description names none. Used to draw the same
    /// person somewhere else, as in a selfie, without the photo's setting.
    /// </summary>
    public static PortraitParts Split(string description)
    {
        ArgumentNullException.ThrowIfNull(description);

        List<string> person = [];
        List<string> clothing = [];
        List<string> setting = [];

        foreach (string sentence in ClauseSeparator().Split(Normalize(description)))
        {
            ClassifyClauses(sentence.Trim(), person, clothing, setting);
        }

        static string? Join(List<string> clauses)
        {
            return clauses.Count == 0 ? null : string.Join(", ", clauses);
        }

        return new PortraitParts(Join(person), Join(clothing)?.Replace("wearing ", string.Empty, StringComparison.OrdinalIgnoreCase), Join(setting));
    }

    /// <summary>
    /// A conservative estimate of the CLIP tokens in <paramref name="text"/>: one per punctuation mark, and one per word
    /// with another for every eight letters after the first eight, since long or rare words split into several tokens.
    /// </summary>
    public static int EstimateTokens(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        int tokens = 0;

        foreach (Match match in TokenPiece().Matches(text))
        {
            tokens += char.IsLetterOrDigit(match.Value[0]) ? 1 + ((match.Value.Length - 1) / 8) : 1;
        }

        return tokens;
    }

    private static string Join(params string?[] sentences)
    {
        return string.Join(" ", sentences.Where(sentence => !string.IsNullOrEmpty(sentence)));
    }

    /// <summary>
    /// One whitespace-separated line, without a leading "a photo of" or article and without closing punctuation, with
    /// plain "glasses" made "eyeglasses" so they are not read as drinking glasses.
    /// </summary>
    internal static string Normalize(string description)
    {
        string text = Whitespace().Replace(description, " ").Trim().TrimEnd('.', '!', '?', ',', ';', ':', ' ');
        text = LeadingPhotoOf().Replace(text, string.Empty);
        text = LeadingArticle().Replace(text, string.Empty);
        text = BareGlasses().Replace(text, "eyeglasses");

        return text.Trim();
    }

    /// <summary>
    /// Sorts the phrases of one comma-separated clause or sentence. The description's first phrase names the person,
    /// even when it mentions clothing ("a nurse") or a place ("a farmer"), unless it starts as a place or clothing does
    /// ("in a library"). A phrase split off mid-clause that is neither clothing nor a place stays with the phrase before
    /// it, as "in his sixties" stays with "a man" and "on the windowsill" with "in a kitchen with flowers"; one that starts
    /// a clause, such as "sitting", goes with the phrase after it.
    /// </summary>
    private static void ClassifyClauses(string sentence, List<string> person, List<string> clothing, List<string> setting)
    {
        List<string> parts = [.. Marker().Split(sentence).SelectMany(piece => IN_GARMENT.Split(piece)).Select(part => part.Trim()).Where(part => part.Length > 0)];
        List<string>? previous = null;
        string? lead = null;

        for (int index = 0; index < parts.Count; index++)
        {
            string part = parts[index];
            string clause = lead is null ? part : lead + " " + part;
            lead = null;

            bool first = person.Count == 0 && clothing.Count == 0 && setting.Count == 0;
            string[] words = Words(part);
            bool startsAsPlace = PlaceStart().IsMatch(part);
            bool hasGarment = words.Any(GARMENTS.Contains) || part.Contains("lab coat", StringComparison.OrdinalIgnoreCase);
            bool hasPlace = words.Any(PLACES.Contains) || BehindSubject().IsMatch(part);
            List<string> target;

            if (first && !ClothingStart().IsMatch(part) && !startsAsPlace)
            {
                target = person;
            }
            else if (ClothingStart().IsMatch(part) || (hasGarment && !hasPlace))
            {
                target = clothing;
                clause = LeadingAnd().Replace(clause, string.Empty);
            }
            else if (hasPlace || (first && startsAsPlace))
            {
                target = setting;
                clause = LeadingWith().Replace(LeadingAnd().Replace(clause, string.Empty), string.Empty);
            }
            else if (previous is not null)
            {
                previous[^1] += " " + clause;
                continue;
            }
            else if (index < parts.Count - 1)
            {
                lead = clause;
                continue;
            }
            else
            {
                target = person;
            }

            target.Add(clause);
            previous = target;
        }
    }

    /// <summary>
    /// Before "in" followed within a few words by a garment, as in "farmer in denim overalls".
    /// </summary>
    private static readonly Regex IN_GARMENT = new(
        @"\s+(?=in\s+(?:[\w'-]+\s+){0,3}?(?:" + string.Join("|", GARMENTS.OrderByDescending(garment => garment.Length).Select(Regex.Escape)) + @")\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string[] Words(string clause)
    {
        return WordSplit().Split(clause.ToLowerInvariant()).Where(word => word.Length > 0).ToArray();
    }

    private static string Capitalize(string text)
    {
        if (text.Length == 0 || !char.IsLower(text[0]))
        {
            return text;
        }

        StringBuilder builder = new(text);
        builder[0] = char.ToUpperInvariant(builder[0]);
        return builder.ToString();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"^(?:(?:a|an)\s+)?(?:photo|photograph|picture|image|portrait|headshot)\s+of\s+", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingPhotoOf();

    [GeneratedRegex(@"^(?:a|an|the)\s+(?=\S)", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingArticle();

    [GeneratedRegex(@"(?<!\b(?:sun|wine|drinking|safety|swim|swimming|ski|beer|shot|reading|eye)[\s-]?)\bglasses\b", RegexOptions.IgnoreCase)]
    private static partial Regex BareGlasses();

    /// <summary>
    /// Between clauses written as separate phrases or sentences.
    /// </summary>
    [GeneratedRegex(@"\s*[,;.!?]\s*")]
    private static partial Regex ClauseSeparator();

    /// <summary>
    /// Before a phrase that may name clothing or a setting without a comma, such as "wearing a plaid shirt", "in his
    /// workshop", or "with a tractor behind him".
    /// </summary>
    [GeneratedRegex(@"\s+(?=(?:and\s+)?(?:wearing|dressed\s+in|in\s+front\s+of|(?:with\s+(?:a|an|the|some)\s+[^,;.]*?\b(?:behind|in\s+the\s+background)\b)|(?:in|at|on|inside|outside|near|by|beside|behind|under|against)\s+(?:a|an|the|his|her|their|my|our|its|some)\b|outdoors\b|indoors\b))", RegexOptions.IgnoreCase)]
    private static partial Regex Marker();

    [GeneratedRegex(@"^(?:and\s+)?(?:wearing|dressed\s+in)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ClothingStart();

    [GeneratedRegex(@"^(?:and\s+)?(?:in|at|on|inside|outside|near|by|beside|behind|under|against|outdoors|indoors)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PlaceStart();

    [GeneratedRegex(@"\b(?:behind\s+(?:him|her|them|me)|in\s+the\s+background)\b", RegexOptions.IgnoreCase)]
    private static partial Regex BehindSubject();

    [GeneratedRegex(@"^and\s+", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingAnd();

    [GeneratedRegex(@"^with\s+(?=(?:a|an|the|some)\s)", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingWith();

    [GeneratedRegex(@"[^a-z0-9'-]+")]
    private static partial Regex WordSplit();

    [GeneratedRegex(@"[\p{L}\p{N}']+|[^\s\p{L}\p{N}]")]
    private static partial Regex TokenPiece();
}
