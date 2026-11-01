using System;
using System.Globalization;
using System.Text;

namespace HomiliaMessenger.Integrations.Speech;

/// <summary>Prepares a speech-only copy of a message for Kokoro's English phonemizer.</summary>
internal static class SpeechText
{
    internal static string Prepare(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        StringBuilder cleaned = new(text.Length);

        // EnumerateRunes consumes a valid surrogate pair together and substitutes U+FFFD for malformed UTF-16.
        // Misaki's regex tokenization can split even valid supplementary characters into isolated surrogates,
        // which then fail Unicode normalization. Keep them out of this phonemizer as well.
        foreach (Rune rune in text.EnumerateRunes())
        {
            UnicodeCategory category = Rune.GetUnicodeCategory(rune);
            bool noncharacter = rune.Value is >= 0xFDD0 and <= 0xFDEF || (rune.Value & 0xFFFE) == 0xFFFE;
            if (!rune.IsBmp || rune == Rune.ReplacementChar || noncharacter
                || category == UnicodeCategory.Format
                || (category == UnicodeCategory.Control && !Rune.IsWhiteSpace(rune)))
            {
                // A separator avoids joining the words on either side of an omitted symbol.
                cleaned.Append(' ');
            }
            else
            {
                cleaned.Append(rune.ToString());
            }
        }

        return cleaned.ToString();
    }
}
