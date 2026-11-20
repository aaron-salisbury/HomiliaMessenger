using HomiliaMessenger.Business.Modules.ImageGeneration;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace HomiliaMessenger.Integrations.ImageGeneration;

/// <summary>
/// Turns a selfie's subject and scene into plain sentences for a model that reads prompts with a language model and keeps
/// the face from the contact's photo as a reference image, as FLUX.2 [klein] does, the same way every time.
/// </summary>
/// <remarks>
/// <para>
/// Follows the prompts that kept the face and showed the scene best in the GPU image experiment: the framing first, then
/// "the exact same person as in the reference image, with the same face, hair and age" and the person in words (which
/// keeps their age and hair when the photo is small), then what is happening, in the order a person would describe it.
/// Unlike CLIP, the language model reads sentences, consequences ("soaking wet, hair plastered down") and several objects
/// at once, so there are no emphasis weights, no gesture stand-ins for subtle expressions, and no negative prompt: the
/// model runs at CFG 1, where a negative prompt has no effect.
/// </para>
/// <para>
/// Words from the conversation are kept as written, without the characters the runtime reads as emphasis syntax. Only the
/// first subject is drawn, with one reference image.
/// </para>
/// </remarks>
internal static class PlainSelfiePromptComposer
{
    internal const string FRAMING = "Close-up smartphone selfie from slightly above of the exact same person as in the reference image, with the same face, hair and age";

    /// <summary>
    /// What a soaking leaves a person looking like, added when the weather or scene says they are wet.
    /// </summary>
    internal const string SOAKED = "Soaking wet, hair plastered down, water streaming down the face and clothes";

    private static readonly string[] WET = ["wet", "soak", "drench", "rain", "storm", "downpour", "pouring", "drizzl"];

    internal static SelfiePrompt Compose(SelfieSubject subject, SelfieScene scene)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(scene);

        string appearance = SelfiePromptComposer.Clean(subject.Appearance) ?? "a person";
        string? expression = SelfiePromptComposer.Clean(subject.Expression);
        string? clothing = SelfiePromptComposer.Clean(subject.Clothing);
        string? activity = SelfiePromptComposer.Clean(scene.Activity);
        string? props = SelfiePromptComposer.Clean(scene.Props);
        string? location = SelfiePromptComposer.Clean(scene.Location);
        string? weather = SelfiePromptComposer.Clean(scene.Weather);
        string? lighting = SelfiePromptComposer.Clean(scene.Lighting);

        List<string> sentences = [$"{FRAMING}: {appearance}"];
        sentences.Add(expression is null ? "Smiling, looking into the camera" : $"{Capitalize(expression)}, looking into the camera");

        if (clothing is not null)
        {
            sentences.Add($"Wearing {clothing}");
        }

        if (activity is not null)
        {
            sentences.Add(Capitalize(activity));
        }

        if (props is not null)
        {
            sentences.Add($"In the picture with them: {props}");
        }

        bool wet = new[] { weather, activity }.Any(text => text is not null && WET.Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase)));

        if (wet)
        {
            sentences.Add(SOAKED);
        }

        foreach (string? setting in new[] { location, weather, lighting })
        {
            if (setting is not null)
            {
                sentences.Add(Capitalize(setting));
            }
        }

        return new SelfiePrompt(string.Join(" ", sentences.Select(sentence => sentence + ".")), string.Empty, ImagePromptStyle.Plain, false);
    }

    private static string Capitalize(string text)
    {
        return text.Length > 0 && char.IsLower(text[0]) ? char.ToUpper(text[0], CultureInfo.InvariantCulture) + text[1..] : text;
    }
}

/// <summary>
/// Adds the trigger words of the LoRAs in use to a prompt, first, where they count most.
/// </summary>
internal static class LoraTriggers
{
    internal static string Prepend(string prompt, IEnumerable<ImageLoraUse> loras, ImagePromptStyle style)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        string[] words = [.. loras
            .Select(lora => lora.TriggerWords.Trim().TrimEnd('.', ',', ';').Trim())
            .Where(words => words.Length > 0 && !prompt.Contains(words, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)];

        if (words.Length == 0)
        {
            return prompt;
        }

        return style == ImagePromptStyle.Plain
            ? string.Join(". ", words) + ". " + prompt
            : string.Join(", ", words) + ", " + prompt;
    }
}
