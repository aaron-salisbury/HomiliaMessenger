using HomiliaMessenger.Business.Modules.ImageGeneration;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace HomiliaMessenger.Tests.ImageGeneration;

[TestClass]
public sealed partial class PortraitPromptComposerTests
{
    private const string WORKSHOP = "An older man with a long white beard and glasses, wearing a plaid shirt in his workshop.";
    private const string FARMER = "A middle-aged Black farmer in denim overalls with a tractor behind him.";

    /// <summary>
    /// Words describing who a person is, which the composer must never add on its own.
    /// </summary>
    private static readonly string[] DEMOGRAPHIC_WORDS =
    [
        "man", "woman", "boy", "girl", "male", "female", "young", "old", "older", "elderly", "middle-aged", "teen", "child",
        "black", "white", "asian", "latina", "latino", "hispanic", "caucasian", "african", "european", "indian", "skin", "hair",
        "beard", "slim", "thin", "fat", "heavy", "muscular", "tall", "short", "beautiful", "handsome", "pretty", "attractive"
    ];

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(WORKSHOP)]
    [DataRow(FARMER)]
    [DataRow("A young woman with curly red hair and freckles, wearing a green sweater, at a coffee shop")]
    [DataRow("Middle-aged Latina nurse in blue scrubs")]
    [DataRow("person with short gray hair; denim jacket; neighborhood library")]
    [DataRow("A cheerful grandmother in her seventies with silver hair in a bun, wearing a red cardigan and pearl earrings, sitting in a sunny kitchen with flowers on the windowsill")]
    public void Compose_ShowsExamples(string description)
    {
        string prompt = PortraitPromptComposer.Compose(description);
        TestContext.WriteLine($"{description}\n=> {prompt}\n   ~{PortraitPromptComposer.EstimateTokens(prompt)} tokens");

        Assert.IsFalse(string.IsNullOrWhiteSpace(prompt));
    }

    [TestMethod]
    public void Compose_TheWorkshopExample_PutsThePersonFirstAndKeepsEveryDetail()
    {
        string prompt = PortraitPromptComposer.Compose(WORKSHOP);

        Assert.StartsWith("Older man with a long white beard and eyeglasses", prompt);
        Assert.Contains("plaid shirt", prompt);
        Assert.Contains("workshop", prompt);
        Assert.Contains(PortraitPromptComposer.CLOSE_FRAMING, prompt);
        Assert.IsLessThan(prompt.IndexOf("polished photographic style", StringComparison.Ordinal), prompt.IndexOf("workshop", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Compose_TheFarmerExample_UsesWaistUpFramingForOverallsAndABackgroundObject()
    {
        string prompt = PortraitPromptComposer.Compose(FARMER);

        Assert.StartsWith("Middle-aged Black farmer", prompt);
        Assert.Contains("denim overalls", prompt);
        Assert.Contains("tractor behind him", prompt);
        Assert.Contains(PortraitPromptComposer.WAIST_UP_FRAMING, prompt);
        Assert.DoesNotContain("head-and-shoulders", prompt);
    }

    [TestMethod]
    [DataRow(WORKSHOP)]
    [DataRow(FARMER)]
    [DataRow("A woman in a red dress at the beach")]
    [DataRow("Young man with a shaved head, wearing a leather jacket, on a city street at night")]
    public void Compose_OrdersPersonThenClothingThenFramingThenSettingThenStyle(string description)
    {
        string prompt = PortraitPromptComposer.Compose(description);
        string normalized = PortraitPromptComposer.Normalize(description);
        int person = prompt.IndexOf(normalized.Split(' ')[0], StringComparison.OrdinalIgnoreCase);
        int framing = prompt.IndexOf("portrait", StringComparison.Ordinal);
        int style = prompt.IndexOf("polished photographic style", StringComparison.Ordinal);

        Assert.AreEqual(0, person, prompt);
        Assert.IsGreaterThan(person, framing);
        Assert.IsGreaterThan(framing, style);
    }

    [TestMethod]
    public void Compose_PlacesClothingBeforeFramingAndSettingAfterIt()
    {
        string prompt = PortraitPromptComposer.Compose("Young man with a shaved head, wearing a leather jacket, on a city street at night");
        int clothing = prompt.IndexOf("leather jacket", StringComparison.Ordinal);
        int framing = prompt.IndexOf("portrait", StringComparison.Ordinal);
        int setting = prompt.IndexOf("city street", StringComparison.Ordinal);

        Assert.IsLessThan(framing, clothing);
        Assert.IsGreaterThan(framing, setting);
    }

    [TestMethod]
    public void Compose_DefaultsToACloseHeadAndShouldersPortrait()
    {
        string prompt = PortraitPromptComposer.Compose("A woman with short gray hair and a warm smile");

        Assert.Contains(PortraitPromptComposer.CLOSE_FRAMING, prompt);
        Assert.Contains(PortraitPromptComposer.STYLE_WITHOUT_SETTING, prompt);
    }

    [TestMethod]
    [DataRow("Full body photo of a dancer in a studio", "Full body")]
    [DataRow("Close-up of a man with a scar on his cheek", "Close-up")]
    public void Compose_KeepsFramingTheUserAskedFor(string description, string framing)
    {
        string prompt = PortraitPromptComposer.Compose(description);

        Assert.Contains(framing, prompt);
        Assert.DoesNotContain(PortraitPromptComposer.CLOSE_FRAMING, prompt);
        Assert.DoesNotContain(PortraitPromptComposer.WAIST_UP_FRAMING, prompt);
    }

    [TestMethod]
    [DataRow("A person with curly hair wearing a green sweater in a park")]
    [DataRow("someone in a lab coat")]
    [DataRow("in a library")]
    [DataRow("Nurse wearing scrubs")]
    [DataRow("A teacher at a chalkboard")]
    public void Compose_NeverAddsWordsAboutWhoThePersonIs(string description)
    {
        string prompt = PortraitPromptComposer.Compose(description);
        string[] given = Words(description);

        foreach (string word in Words(prompt).Where(word => DEMOGRAPHIC_WORDS.Contains(word)))
        {
            Assert.Contains(word, given, $"\"{word}\" was added to: {prompt}");
        }
    }

    [TestMethod]
    [DataRow(WORKSHOP)]
    [DataRow(FARMER)]
    [DataRow("A cheerful grandmother in her seventies with silver hair in a bun, wearing a red cardigan and pearl earrings, sitting in a sunny kitchen with flowers on the windowsill")]
    public void Compose_KeepsEveryWordTheUserWrote(string description)
    {
        string prompt = PortraitPromptComposer.Compose(description).ToLowerInvariant();
        // Articles and joining words may be dropped where phrases are rearranged; "glasses" becomes "eyeglasses".
        string[] skipped = ["a", "an", "the", "and", "with", "glasses"];

        foreach (string word in Words(description).Where(word => !skipped.Contains(word)))
        {
            Assert.Contains(word, prompt, $"\"{word}\" is missing from: {prompt}");
        }
    }

    [TestMethod]
    public void Compose_KeepsAgeWordsAsWritten()
    {
        string prompt = PortraitPromptComposer.Compose("A woman in her sixties with a silver bob, wearing a navy blazer");

        Assert.StartsWith("Woman in her sixties with a silver bob", prompt);
        Assert.Contains("navy blazer", prompt);
    }

    [TestMethod]
    public void Compose_ReadsGlassesAsEyeglassesButLeavesOtherGlassesAlone()
    {
        Assert.Contains("eyeglasses", PortraitPromptComposer.Compose("A man with glasses"));
        Assert.Contains("sunglasses", PortraitPromptComposer.Compose("A woman with sunglasses"));
        Assert.Contains("reading glasses", PortraitPromptComposer.Compose("A man with reading glasses"));
        Assert.DoesNotContain("eyeeyeglasses", PortraitPromptComposer.Compose("A man with eyeglasses"));
    }

    [TestMethod]
    [DataRow(WORKSHOP)]
    [DataRow(FARMER)]
    [DataRow("A young woman with curly red hair and freckles, wearing a green sweater, at a coffee shop")]
    public void Compose_StaysWithinOneClipChunkForOrdinaryDescriptions(string description)
    {
        Assert.IsLessThanOrEqualTo(PortraitPromptComposer.CLIP_CHUNK_TOKENS, PortraitPromptComposer.EstimateTokens(PortraitPromptComposer.Compose(description)));
    }

    [TestMethod]
    public void Compose_ShortensOnlyItsOwnWordsForALongDescription()
    {
        string description = "A cheerful grandmother in her seventies with silver hair in a neat bun, rosy cheeks, laugh lines, bright hazel eyes and "
            + "a gentle smile, wearing a red knitted cardigan, a white blouse with a lace collar, and pearl earrings, sitting in a sunny farmhouse kitchen";
        string prompt = PortraitPromptComposer.Compose(description);

        Assert.Contains(PortraitPromptComposer.SHORT_STYLE, prompt);
        Assert.Contains("laugh lines, bright hazel eyes and a gentle smile", prompt);
        Assert.Contains("pearl earrings", prompt);
    }

    [TestMethod]
    public void Compose_IsDeterministic()
    {
        Assert.AreEqual(PortraitPromptComposer.Compose(FARMER), PortraitPromptComposer.Compose(FARMER));
        Assert.AreEqual(PortraitPromptComposer.Compose(WORKSHOP), PortraitPromptComposer.Compose("  An older man with a long   white beard and glasses,\nwearing a plaid shirt in his workshop  "));
    }

    [TestMethod]
    public void Compose_UsesNeutralWordsWhenNoPersonIsDescribed()
    {
        string prompt = PortraitPromptComposer.Compose("in a library");

        Assert.StartsWith(PortraitPromptComposer.UNNAMED_PERSON, prompt);
        Assert.Contains("library", prompt);
    }

    private static string[] Words(string text)
    {
        return WordPattern().Matches(text.ToLowerInvariant()).Select(match => match.Value).ToArray();
    }

    [GeneratedRegex(@"[a-z0-9'-]+")]
    private static partial Regex WordPattern();
}
