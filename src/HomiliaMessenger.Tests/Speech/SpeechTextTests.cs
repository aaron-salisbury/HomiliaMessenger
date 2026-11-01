using HomiliaMessenger.Integrations.Speech;
using KokoroSharp.Processing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text;

namespace HomiliaMessenger.Tests.Speech;

[TestClass]
public sealed class SpeechTextTests
{
    [TestMethod]
    public void Prepare_ReplacesMalformedUtf16AndNoncharactersWithSeparators()
    {
        // Construct malformed strings here rather than asking the test runner to serialize invalid attribute data.
        foreach (string invalid in new[] { "\uD800", "\uDC00", "\uFFFE", "\uFFFF", "\uFDD0", "\uFFFD", "\0", "\u0001" })
        {
            string cleaned = SpeechText.Prepare("Hello" + invalid + "world.");
            Assert.AreEqual("Hello world.", cleaned);
            Assert.AreEqual(cleaned, cleaned.Normalize(NormalizationForm.FormC));
            Assert.IsNotEmpty(Tokenizer.Tokenize(cleaned));
        }
    }

    [TestMethod]
    public void Prepare_ReplacesSupplementaryCharactersBeforeThePhonemizerCanSplitThem()
    {
        foreach (string symbol in new[] { "\U0001F642", "\U0001F44D", "\U00010400", "\U0001FFFE" })
        {
            string cleaned = SpeechText.Prepare("Hello" + symbol + "world.");
            Assert.AreEqual("Hello world.", cleaned);
            Assert.IsNotEmpty(Tokenizer.Tokenize(cleaned, "en-us"));
            Assert.IsNotEmpty(Tokenizer.Tokenize(cleaned, "en-gb"));
        }
    }

    [TestMethod]
    public void Prepare_PreservesAccentsPunctuationAndLineBreaks()
    {
        const string text = "Café, naïve, José — don’t worry!\r\nA cafe\u0301 costs £5.\tReally?";
        Assert.AreEqual(text, SpeechText.Prepare(text));
        Assert.IsNotEmpty(Tokenizer.Tokenize(SpeechText.Prepare(text)));
        Assert.AreEqual("Hello world.", SpeechText.Prepare("Hello\u200Dworld."));
        Assert.IsTrue(string.IsNullOrWhiteSpace(SpeechText.Prepare("\U0001F642\uD800\0\uFFFE")));
        Assert.AreEqual(string.Empty, SpeechText.Prepare(string.Empty));
    }
}
