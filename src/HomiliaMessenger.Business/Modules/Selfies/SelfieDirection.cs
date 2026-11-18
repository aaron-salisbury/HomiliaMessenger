using HomiliaMessenger.Business.Modules.Agents;
using HomiliaMessenger.Business.Modules.Contacts;
using HomiliaMessenger.Business.Modules.Conversations;
using HomiliaMessenger.Business.Modules.ImageGeneration;
using HomiliaMessenger.Business.Modules.Profile;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace HomiliaMessenger.Business.Modules.Selfies;

/// <summary>
/// What a selfie shows, as the contact in it decided from the conversation: how they look, what they wear, the scene, and
/// what they say with it. Any part may be unknown.
/// </summary>
/// <param name="Caption">The message the contact sends with the selfie, in their own words; <see langword="null"/> for none.</param>
internal sealed record SelfiePlan(string? Expression, string? Clothing, SelfieScene Scene, string? Caption)
{
    internal static SelfiePlan Unknown { get; } = new(null, null, SelfieScene.Unknown, null);
}

/// <summary>
/// Plans a selfie from a conversation, with the model of the contact in it, and describes it for those who cannot see it.
/// </summary>
/// <remarks>
/// The plan is asked for as a few labelled lines rather than JSON or a tool call, since small local models cannot be relied
/// on for structured output; each line is read on its own, and a missing or unclear line leaves that part unknown. Where the
/// conversation says nothing, the model is asked for something ordinary and fitting, so a selfie asked for out of the blue
/// still shows the contact somewhere.
/// </remarks>
internal static partial class SelfieDirection
{
    /// <summary>
    /// The latest messages are enough to tell where a contact is and what is going on.
    /// </summary>
    internal const int RECENT_MESSAGES = 10;

    internal const int MAX_PART_LENGTH = 80;
    internal const int MAX_CAPTION_LENGTH = 300;

    /// <summary>
    /// When nothing says how the contact looks, a selfie still shows them pleased to be sending it.
    /// </summary>
    internal const string DEFAULT_EXPRESSION = "smiling";

    private static readonly string[] LABELS = ["Expression", "Clothing", "Location", "Activity", "Props", "Weather", "Light", "Caption"];

    /// <param name="others">The other contacts in a group, who may be mentioned in the conversation but are not in the selfie.</param>
    /// <param name="now">The user's local time, which sets the light when the conversation does not.</param>
    internal static AgentRequest PlanRequest(Contact subject, UserProfile user, IReadOnlyList<Contact> others, Conversation conversation, IReadOnlyList<ChatMessage> messages, string model, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(others);
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(messages);

        string name = subject.DisplayName;
        string userName = string.IsNullOrWhiteSpace(user.Name) ? "Me" : user.Name.Trim();
        StringBuilder instructions = new();

        instructions.AppendLine($"You plan the selfie {name} is about to take and send in a chat with {userName}{(others.Count > 0 ? " and " + string.Join(" and ", others.Select(other => other.DisplayName)) : string.Empty)}, right now.");
        instructions.AppendLine($"{name} is {Appearance(subject, DateOnly.FromDateTime(now.Date))}.");

        if (!string.IsNullOrWhiteSpace(subject.Personality))
        {
            instructions.AppendLine($"{name}'s personality: {Shorten(subject.Personality, 300)}");
        }

        if (!string.IsNullOrWhiteSpace(subject.Background))
        {
            instructions.AppendLine($"{name}'s background: {Shorten(subject.Background, 300)}");
        }

        instructions.AppendLine($"It is {now.ToString("dddd", CultureInfo.InvariantCulture)} {TimeOfDay(now)}.");
        instructions.AppendLine("Decide from the conversation where they are, what they are doing and wearing, and how they feel, including why they are sending a selfie now. Where it says nothing, choose something ordinary that fits them.");
        instructions.AppendLine("Answer with exactly these lines, a few words each, and write none when something does not apply:");
        instructions.AppendLine($"Expression: {name}'s face, such as smiling, laughing, grinning, irritated, sad, exhausted, sleepy, or surprised");
        instructions.AppendLine($"Clothing: what {name} is wearing");
        instructions.AppendLine($"Location: where {name} is");
        instructions.AppendLine($"Activity: what {name} is doing");
        instructions.AppendLine($"Props: one thing {name} shows beside their face, or none");
        instructions.AppendLine($"Weather: the weather, or a state it leaves {name} in, such as soaking wet, or none");
        instructions.AppendLine("Light: the time of day or the light");
        instructions.Append($"Caption: the short message {name} sends with the selfie, in their own voice");

        Dictionary<ContactId, string> names = others.Append(subject).ToDictionary(contact => contact.Id, contact => contact.DisplayName);
        StringBuilder chat = new();

        foreach (ChatMessage message in messages.TakeLast(RECENT_MESSAGES))
        {
            string author = conversation.SenderOf(message) is { } id ? names.GetValueOrDefault(id, "Someone") : userName;
            chat.AppendLine($"{author}: {message.AgentText.ReplaceLineEndings(" ")}");
        }

        chat.Append($"Plan {name}'s selfie now.");

        return new AgentRequest(model, instructions.ToString(), [new AgentTurn(MessageSender.User, chat.ToString())], ["<|", "\n\n\n"], Precise: true);
    }

    /// <summary>
    /// Reads a plan from a model's answer. Each labelled line is read on its own; anything else is ignored.
    /// </summary>
    internal static SelfiePlan ReadPlan(string? answer)
    {
        Dictionary<string, string> parts = new(StringComparer.OrdinalIgnoreCase);

        foreach (string line in (answer ?? string.Empty).ReplaceLineEndings("\n").Split('\n'))
        {
            Match match = LabelledLine().Match(line);

            if (!match.Success)
            {
                continue;
            }

            string label = LABELS.FirstOrDefault(known => string.Equals(known, match.Groups["label"].Value, StringComparison.OrdinalIgnoreCase)) ?? string.Empty;

            if (label.Length > 0 && !parts.ContainsKey(label) && Value(match.Groups["value"].Value, label == "Caption" ? MAX_CAPTION_LENGTH : MAX_PART_LENGTH) is { } value)
            {
                parts[label] = value;
            }
        }

        return new SelfiePlan(
            parts.GetValueOrDefault("Expression"),
            parts.GetValueOrDefault("Clothing"),
            new SelfieScene(parts.GetValueOrDefault("Location"), parts.GetValueOrDefault("Activity"), parts.GetValueOrDefault("Props"), parts.GetValueOrDefault("Weather"), parts.GetValueOrDefault("Light")),
            parts.GetValueOrDefault("Caption"));
    }

    /// <summary>
    /// The contact in words, for drawing them: the person in the description their photo was generated from, without its
    /// clothing and setting; otherwise their gender and age, such as "woman in her thirties".
    /// </summary>
    internal static string Appearance(Contact contact, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(contact);

        if (!string.IsNullOrWhiteSpace(contact.Appearance) && PortraitPromptComposer.Split(contact.Appearance).Person is { Length: > 0 } person)
        {
            return person;
        }

        (string noun, string pronoun) = contact.Gender switch
        {
            Gender.Male => ("man", "his"),
            Gender.Female => ("woman", "her"),
            _ => ("person", "their")
        };

        return contact.AgeOn(today) is { } age ? $"{noun} in {pronoun} {Decade(age)}" : $"adult {noun}";
    }

    /// <summary>
    /// What the contact wears in the selfie: what the plan says, otherwise what their photo's description says.
    /// </summary>
    internal static string? Clothing(Contact contact, SelfiePlan plan)
    {
        ArgumentNullException.ThrowIfNull(contact);
        ArgumentNullException.ThrowIfNull(plan);

        return plan.Clothing ?? (string.IsNullOrWhiteSpace(contact.Appearance) ? null : PortraitPromptComposer.Split(contact.Appearance).Clothing);
    }

    /// <summary>
    /// What a selfie shows, for agents and the chat list, which cannot see it: "Sent a selfie: laughing, waiting for the
    /// bus at a bus stop, pouring rain".
    /// </summary>
    internal static string Describe(SelfiePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        IEnumerable<string?> parts =
        [
            plan.Expression ?? DEFAULT_EXPRESSION,
            plan.Scene.Activity,
            plan.Scene.Location is { } location ? (StartsWithPreposition().IsMatch(location) ? location : "at " + location) : null,
            plan.Scene.Weather,
            plan.Clothing is { } clothing ? "wearing " + clothing : null,
            plan.Scene.Props is { } props ? "with " + props : null
        ];

        return "Sent a selfie: " + string.Join(", ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string? Value(string text, int maxLength)
    {
        string value = text.Trim().Trim('"', '“', '”', '*', '\'').Trim();

        if (value.Length == 0 || NonePattern().IsMatch(value))
        {
            return null;
        }

        value = value.TrimEnd('.');

        return value.Length <= maxLength ? value : value[..maxLength].TrimEnd();
    }

    private static string Decade(int age)
    {
        return age switch
        {
            < 20 => "late teens",
            < 30 => "twenties",
            < 40 => "thirties",
            < 50 => "forties",
            < 60 => "fifties",
            < 70 => "sixties",
            < 80 => "seventies",
            _ => "eighties"
        };
    }

    private static string TimeOfDay(DateTimeOffset now)
    {
        return now.Hour switch
        {
            < 5 => "night",
            < 12 => "morning",
            < 17 => "afternoon",
            < 21 => "evening",
            _ => "night"
        };
    }

    private static string Shorten(string text, int maxLength)
    {
        string flat = text.ReplaceLineEndings(" ").Trim();
        return flat.Length <= maxLength ? flat : flat[..maxLength].TrimEnd() + "…";
    }

    [GeneratedRegex(@"^\s*[-*•]?\s*\**(?<label>[A-Za-z]+)\**\s*:\s*(?<value>.*)$")]
    private static partial Regex LabelledLine();

    [GeneratedRegex(@"^(none|n/?a|nothing|no|-+|unknown|not applicable)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex NonePattern();

    [GeneratedRegex(@"^(at|in|on|inside|outside|near|by|beside|under|on top of)\b", RegexOptions.IgnoreCase)]
    private static partial Regex StartsWithPreposition();
}
