using HomiliaMessenger.Business.Modules.ImageGeneration;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace HomiliaMessenger.Integrations.ImageGeneration;

/// <summary>
/// A selfie prompt for a Stable Diffusion 1.x model: what to draw, and what to steer away from.
/// </summary>
internal sealed record SelfiePrompt(string Prompt, string NegativePrompt);

/// <summary>
/// Turns a selfie's subject and scene into a prompt for a Stable Diffusion 1.x model with face matching, following the
/// local selfie experiment, the same way every time.
/// </summary>
/// <remarks>
/// <para>
/// The experiment found, with DreamShaper 8 LCM: the framing must come first ("Close-up smartphone selfie from slightly
/// above, face close to the camera."), the reverse of a contact photo's subject-first prompt, or the image reads as a
/// photo someone else took; emphasis weights such as <c>(soaking wet:1.5)</c> with CFG above 1 and a negative prompt
/// naming the default the model drifts to are what make a state or expression show; strong expressions (laughing,
/// grinning, irritated, sad, sleepy, exhausted, soaked) render reliably, while subtle ones (embarrassed, nervous,
/// shocked) mostly do not, so embarrassment is drawn as a gesture, which worked about half the time; a prop shows only
/// when heavily weighted and placed beside the face; and named clothing overrides what the reference photo wears.
/// </para>
/// <para>
/// Words from the conversation are kept as written, without the characters the runtime reads as emphasis syntax. Only
/// the first subject is drawn: this model draws one person reliably, and mixes up the features of two.
/// </para>
/// </remarks>
internal static partial class SelfiePromptComposer
{
    internal const string FRAMING = "Close-up smartphone selfie from slightly above, face close to the camera.";
    internal const string BASE_NEGATIVE = "deformed, blurry";

    /// <summary>
    /// Expressions as the experiment drew them best: the words that make each show, and what to steer away from. Matched
    /// by any of their names appearing in the expression asked for.
    /// </summary>
    private static readonly (string[] Names, string Words, string Avoid)[] EXPRESSIONS =
    [
        (["irritated", "annoyed", "angry", "frustrated", "mad", "grumpy", "furious", "scowl", "scowling"], "(irritated:1.4), (annoyed scowl:1.4), (furrowed brow:1.3)", "happy, smiling, calm"),
        (["sad", "upset", "crying", "teary", "lonely", "heartbroken", "melancholy", "gloomy"], "(sad:1.4), (teary eyes:1.3), (downturned mouth:1.3)", "happy, smiling"),
        (["exhausted", "tired", "worn out", "weary", "drained", "spent"], "(exhausted:1.5), (tired heavy-lidded eyes:1.3)", "energetic, fresh"),
        (["sleepy", "drowsy", "yawning", "groggy", "just woke"], "(sleepy:1.4), (yawning:1.3), (half-closed eyes:1.3)", "awake, alert"),
        (["laughing", "laugh", "giggling", "cracking up", "amused"], "(laughing:1.3)", "serious, sad"),
        (["grinning", "grin", "proud", "overjoyed", "excited", "thrilled", "joyful", "ecstatic", "beaming", "triumphant"], "(huge grin:1.3)", "sad, serious"),
        (["embarrassed", "sheepish", "shy", "blushing", "awkward", "ashamed", "mortified"], "(covering half the face with one hand:1.4), (peeking through fingers:1.3), (blushing:1.3)", "confident, broad smile"),
        (["surprised", "shocked", "amazed", "astonished", "stunned", "wide-eyed"], "(surprised:1.4), (wide eyes:1.4), (mouth open:1.3)", "calm, closed mouth"),
        (["nervous", "anxious", "worried", "scared", "tense"], "(nervous:1.4), (tense tight-lipped smile:1.3)", "relaxed, confident"),
        (["cold", "freezing", "shivering"], "(cold:1.3), (red cheeks and nose:1.3)", "summer, warm"),
        (["smiling", "smile", "happy", "content", "cheerful", "relaxed", "calm"], "(smiling:1.2)", "sad")
    ];

    /// <summary>
    /// States the weather leaves a person in that the experiment drew: soaked in rain.
    /// </summary>
    private static readonly string[] WET = ["wet", "soak", "drench", "rain", "storm", "downpour", "pouring", "drizzl"];

    internal static SelfiePrompt Compose(SelfieSubject subject, SelfieScene scene)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(scene);

        string appearance = Clean(subject.Appearance) ?? "person";
        string? expression = Clean(subject.Expression);
        string? clothing = Clean(subject.Clothing);
        string? weather = Clean(scene.Weather);
        string? props = Clean(scene.Props);
        List<string> avoid = [];

        // The person, with how they look: "(irritated:1.4), (annoyed scowl:1.4), (furrowed brow:1.3) older man with a white beard".
        string face = Expression(expression, avoid);
        List<string> person = [face.Length == 0 ? appearance : $"{face} {appearance}"];

        if (weather is not null && WET.Any(word => weather.Contains(word, StringComparison.OrdinalIgnoreCase)))
        {
            person.Add("(soaking wet:1.5), (drenched:1.3), (water streaming down the face:1.3)");
            avoid.Add("dry hair, dry clothes, sunny");
        }

        if (clothing is not null)
        {
            person.Add($"wearing ({clothing}:1.4)");
        }

        if (props is not null)
        {
            person.Add($"({props} held up beside the face:1.5)");
        }

        // The scene: what is going on, where, and the light and weather.
        List<string> place = [];
        string? activity = Clean(scene.Activity);
        string? location = Clean(scene.Location);
        string? lighting = Clean(scene.Lighting);

        if (location is not null)
        {
            place.Add(Capitalize(location));
        }

        if (weather is not null)
        {
            place.Add(WET.Any(word => weather.Contains(word, StringComparison.OrdinalIgnoreCase)) ? $"({weather}:1.4), rain streaks" : $"({weather}:1.3)");
        }

        if (lighting is not null)
        {
            place.Add(lighting);
        }

        string sceneSentence = place.Count == 0 ? string.Empty : string.Join(", ", place) + ".";
        string activitySentence = activity is null ? string.Empty : Capitalize(activity) + ".";

        // The framing and the person matter most; what they are doing is dropped first when the prompt runs long, since
        // the location and light already set the scene.
        string prompt = Join(FRAMING, string.Join(", ", person) + ".", activitySentence, sceneSentence);

        if (PortraitPromptComposer.EstimateTokens(prompt) > PortraitPromptComposer.CLIP_CHUNK_TOKENS && activitySentence.Length > 0)
        {
            prompt = Join(FRAMING, string.Join(", ", person) + ".", sceneSentence);
        }

        string negative = string.Join(", ", avoid.Append(BASE_NEGATIVE).Distinct(StringComparer.OrdinalIgnoreCase));

        return new SelfiePrompt(prompt, negative);
    }

    /// <summary>
    /// The words that make <paramref name="expression"/> show, adding what to avoid; an expression the experiment did not
    /// cover is weighted as written.
    /// </summary>
    private static string Expression(string? expression, List<string> avoid)
    {
        if (expression is null)
        {
            avoid.Add("sad");
            return "(smiling:1.2)";
        }

        foreach ((string[] names, string words, string against) in EXPRESSIONS)
        {
            if (names.Any(name => Regex.IsMatch(expression, $@"\b{Regex.Escape(name)}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
            {
                avoid.Add(against);
                return words;
            }
        }

        return $"({expression}:1.3)";
    }

    /// <summary>
    /// Text from a conversation as prompt words: one line, without the brackets and colons the runtime reads as emphasis,
    /// and without a closing full stop; <see langword="null"/> when nothing is left.
    /// </summary>
    internal static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string cleaned = Whitespace().Replace(Syntax().Replace(text, " "), " ").Trim().TrimEnd('.', ',', ';').Trim();

        return cleaned.Length == 0 ? null : cleaned;
    }

    private static string Join(params string[] sentences)
    {
        return string.Join(" ", sentences.Where(sentence => sentence.Length > 0));
    }

    private static string Capitalize(string text)
    {
        return text.Length > 0 && char.IsLower(text[0]) ? char.ToUpper(text[0], CultureInfo.InvariantCulture) + text[1..] : text;
    }

    [GeneratedRegex(@"[()\[\]{}:<>|\\]")]
    private static partial Regex Syntax();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
