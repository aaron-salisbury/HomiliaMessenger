using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace HomiliaMessenger.Business.Modules.Agents;

/// <summary>
/// Restores swear words a model masked with asterisks, such as <c>f*cking</c> or <c>sh*t</c>. Some models mask them
/// whatever they are asked, from their training; contacts should speak as written, uncensored.
/// </summary>
/// <remarks>
/// A word is restored only when asterisks stand in for letters inside it (or at its end) and a common swear word fits
/// its length and every visible letter. Asterisks used for emphasis, such as <c>*really*</c>, are left alone.
/// </remarks>
internal static partial class MaskedWords
{
    // Most common first, so an ambiguous mask such as "s***" takes the likelier word.
    private static readonly string[] WORDS =
    [
        "fucking", "fuck", "fucked", "fucker", "fuckers", "fuckin", "fucks", "motherfucker", "motherfucking",
        "shit", "shitty", "shits", "bullshit", "horseshit", "bitch", "bitches", "bitching", "asshole", "assholes", "ass",
        "damn", "goddamn", "goddamnit", "dammit", "damned", "hell", "crap", "crappy", "dick", "dicks", "dickhead",
        "piss", "pissed", "bastard", "bastards", "cunt", "cock", "prick", "twat", "whore", "slut", "bloody", "bollocks",
        "wanker", "jackass", "dumbass", "badass", "douchebag", "douche", "arse", "arsehole"
    ];

    internal static string Unmask(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return text.Contains('*') ? MaskedWord().Replace(text, match => Restore(match.Value) ?? match.Value) : text;
    }

    private static string? Restore(string masked)
    {
        // "*really*" is emphasis: asterisks only around the word, none inside it.
        if (masked.StartsWith('*') && !masked.Trim('*').Contains('*'))
        {
            return null;
        }

        string? word = WORDS.FirstOrDefault(candidate => candidate.Length == masked.Length
            && candidate.Zip(masked).All(pair => pair.Second == '*' || char.ToLowerInvariant(pair.Second) == pair.First));

        if (word is null)
        {
            return null;
        }

        string letters = new([.. masked.Where(char.IsLetter)]);

        if (letters.Length > 1 && letters.All(char.IsUpper))
        {
            return word.ToUpperInvariant();
        }

        return char.IsUpper(masked.First(char.IsLetter)) && masked.IndexOf(masked.First(char.IsLetter)) == 0
            ? char.ToUpperInvariant(word[0]) + word[1..]
            : word;
    }

    // A run of letters and asterisks with at least one of each, not part of a longer word.
    [GeneratedRegex(@"(?<![A-Za-z*])(?=[A-Za-z*]*\*)(?=[A-Za-z*]*[A-Za-z])[A-Za-z*]+(?![A-Za-z*])")]
    private static partial Regex MaskedWord();
}
