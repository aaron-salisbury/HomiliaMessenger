using HomiliaMessenger.Business.Modules.Contacts;
using HomiliaMessenger.Presentation.Desktop.Base.Converters;
using HomiliaMessenger.Presentation.Desktop.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Globalization;

namespace HomiliaMessenger.Tests.Contacts;

[TestClass]
public sealed class ContactDetailViewModelTests
{
    [TestMethod]
    public void OpeningAContact_IsNotDirtyUntilEdited()
    {
        Contact contact = new() { Id = ContactId.New(), GivenName = "Walt", Birthday = new DateOnly(1948, 3, 14) };

        using ContactDetailViewModel detail = new(contact);
        bool dirtyAfterOpening = detail.IsDirty;
        detail.Personality = "Gruff but warm.";

        Assert.IsFalse(dirtyAfterOpening);
        Assert.IsTrue(detail.IsDirty);

        detail.AcceptChanges();
        Assert.IsFalse(detail.IsDirty);
    }

    [TestMethod]
    public void ToContact_TrimsTextAndDropsBlankValues()
    {
        using ContactDetailViewModel detail = new()
        {
            GivenName = "  Walt  ",
            FamilyName = "   ",
            Birthday = new DateOnly(1948, 3, 14),
            Gender = string.Empty,
            AgentProvider = " Ollama ",
            AgentModel = " llama3.1:8b "
        };

        Contact contact = detail.ToContact();

        Assert.AreEqual(detail.Id, contact.Id);
        Assert.AreEqual("Walt", contact.GivenName);
        Assert.IsNull(contact.FamilyName);
        Assert.AreEqual(new DateOnly(1948, 3, 14), contact.Birthday);
        Assert.AreEqual(78, contact.AgeOn(new DateOnly(2026, 10, 31)));
        Assert.IsNull(contact.Gender);
        Assert.AreEqual(new AgentAssignment("Ollama", "llama3.1:8b"), contact.Agent);
    }

    [TestMethod]
    public void ToContact_OmitsAgentOnlyWhenBothAgentFieldsAreBlank()
    {
        using ContactDetailViewModel blank = new() { GivenName = "Rose" };
        using ContactDetailViewModel half = new() { GivenName = "Rose", AgentModel = "llama3.1:8b" };

        Assert.IsNull(blank.ToContact().Agent);
        Assert.AreEqual(new AgentAssignment(string.Empty, "llama3.1:8b"), half.ToContact().Agent);
    }

    [TestMethod]
    public void BirthdayConverter_UsesCalendarDateRegardlessOfCultureOrOffset()
    {
        BirthdayToDateConverter converter = new();
        CultureInfo culture = CultureInfo.GetCultureInfo("de-DE");

        DateTimeOffset? shown = (DateTimeOffset?)converter.Convert(new DateOnly(2000, 1, 1), typeof(DateTimeOffset?), null, culture);
        Assert.AreEqual(new DateTime(2000, 1, 1), shown!.Value.DateTime);
        Assert.IsNull(converter.Convert(null, typeof(DateTimeOffset?), null, culture));

        DateTimeOffset picked = new(1990, 1, 15, 23, 30, 0, TimeSpan.FromHours(-6));
        Assert.AreEqual(new DateOnly(1990, 1, 15), converter.ConvertBack(picked, typeof(DateOnly?), null, culture));
    }
}
