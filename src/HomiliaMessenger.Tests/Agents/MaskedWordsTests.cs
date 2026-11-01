using HomiliaMessenger.Business.Modules.Agents;
using HomiliaMessenger.Business.Modules.Contacts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HomiliaMessenger.Tests.Agents;

[TestClass]
public sealed class MaskedWordsTests
{
    [TestMethod]
    [DataRow("The f*cking game was so f*cking intense!", "The fucking game was so fucking intense!")]
    [DataRow("What the hell is wrong with this f*cking piece of sh*t?", "What the hell is wrong with this fucking piece of shit?")]
    [DataRow("F*ck the new boss, he's an a**hole.", "Fuck the new boss, he's an asshole.")]
    [DataRow("Well f***.", "Well fuck.")]
    [DataRow("SH*T, I'm late.", "SHIT, I'm late.")]
    [DataRow("You b*tches and your bull$hit", "You bitches and your bull$hit")]
    [DataRow("G*ddamn d*mn truck.", "Goddamn damn truck.")]
    public void Unmask_RestoresMaskedSwearWords(string masked, string expected)
    {
        Assert.AreEqual(expected, MaskedWords.Unmask(masked));
    }

    [TestMethod]
    [DataRow("I *really* mean it.")]
    [DataRow("Rated 5*, would visit again.")]
    [DataRow("2*3 is 6.")]
    [DataRow("The h*rse ran off.")]
    [DataRow("No masking here.")]
    public void Unmask_LeavesEverythingElseAlone(string text)
    {
        Assert.AreEqual(text, MaskedWords.Unmask(text));
    }

    [TestMethod]
    public void CleanReply_WritesMaskedWordsInFull()
    {
        Contact mack = new() { Id = ContactId.New(), GivenName = "Mack" };

        Assert.AreEqual("Damn truck broke the fuck down again.", PersonaFraming.CleanReply("Damn truck broke the f*ck down again.", mack));
    }
}
