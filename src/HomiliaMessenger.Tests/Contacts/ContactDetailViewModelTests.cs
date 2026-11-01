using HomiliaMessenger.Business.Modules.Contacts;
using HomiliaMessenger.Business.Modules.Speech;
using HomiliaMessenger.Tests.Speech;
using HomiliaMessenger.Presentation.Desktop.Base.Converters;
using HomiliaMessenger.Presentation.Desktop.Models;
using HomiliaMessenger.Presentation.Desktop.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

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
            Gender = GenderOption.For(Gender.Male)
        };

        Contact contact = detail.ToContact();

        Assert.AreEqual(detail.Id, contact.Id);
        Assert.AreEqual("Walt", contact.GivenName);
        Assert.IsNull(contact.FamilyName);
        Assert.AreEqual(new DateOnly(1948, 3, 14), contact.Birthday);
        Assert.AreEqual(78, contact.AgeOn(new DateOnly(2026, 10, 31)));
        Assert.AreEqual(Gender.Male, contact.Gender);
        Assert.IsNull(contact.Agent);
    }

    [TestMethod]
    public void Gender_OffersOnlyMaleFemaleOrNotSpecified()
    {
        using ContactDetailViewModel detail = new(new Contact { Id = ContactId.New(), GivenName = "Rose", Gender = Gender.Female });

        Assert.AreSequenceEqual(new Gender?[] { null, Gender.Male, Gender.Female }, detail.GenderOptions.Select(option => option.Value).ToArray());
        Assert.AreEqual("Female", detail.Gender.Label);
        Assert.IsFalse(detail.IsDirty);

        detail.Gender = GenderOption.NotSpecified;

        Assert.IsNull(detail.ToContact().Gender);
        Assert.IsTrue(detail.IsDirty);
    }

    [TestMethod]
    public void Agent_IsChosenFromTheOfferedModels()
    {
        AgentOption phi = new("Phi-3.5 mini", new AgentAssignment("Local", "phi-3.5-mini"));
        Contact rose = new() { Id = ContactId.New(), GivenName = "Rose", Agent = new AgentAssignment("Local", "phi-3.5-mini") };

        using ContactDetailViewModel blank = new(null, [AgentOption.None, phi]);
        using ContactDetailViewModel assigned = new(rose, [AgentOption.None, phi]);

        Assert.AreSame(AgentOption.None, blank.Agent);
        Assert.IsNull(blank.ToContact().Agent);
        Assert.AreSame(phi, assigned.Agent);
        Assert.HasCount(2, assigned.AgentOptions);
        Assert.AreEqual(new AgentAssignment("Local", "phi-3.5-mini"), assigned.ToContact().Agent);
    }

    [TestMethod]
    public void Voice_FiltersChoicesByGenderWithoutClearingTheSavedVoice()
    {
        FakeSpeech speech = new();
        Contact rose = new() { Id = ContactId.New(), GivenName = "Rose", Gender = Gender.Female, VoiceId = "af_heart" };
        using ContactDetailViewModel detail = new(rose, voices: speech.Voices, isSpeechAvailable: true);

        Assert.IsFalse(detail.IsDirty);
        Assert.AreEqual("af_heart", detail.Voice!.Id);
        Assert.AreSequenceEqual(new string?[] { null, "af_heart" }, detail.VoiceOptions.Select(option => option.Id).ToArray());

        detail.Gender = GenderOption.For(Gender.Male);
        Assert.AreSequenceEqual(new string?[] { null, "am_adam" }, detail.VoiceOptions.Select(option => option.Id).ToArray());
        Assert.AreEqual("af_heart", detail.ToContact().VoiceId);
        Assert.Contains("Heart", detail.VoiceDescription);
        detail.Voice = null; // What a ComboBox does when the selected item is removed from its ItemsSource.
        Assert.AreEqual("af_heart", detail.ToContact().VoiceId);

        detail.AcceptChanges();
        detail.RefreshVoices(speech.Voices, false);
        Assert.IsFalse(detail.IsDirty);
        Assert.IsFalse(detail.IsSpeechAvailable);
        Assert.AreEqual("af_heart", detail.ToContact().VoiceId);

        detail.RefreshVoices(speech.Voices, true);
        detail.Voice = detail.VoiceOptions.Single(option => option.Id == "am_adam");
        Assert.IsTrue(detail.IsDirty);
        Assert.AreEqual("am_adam", detail.ToContact().VoiceId);
        detail.Voice = VoiceOption.None;
        Assert.IsNull(detail.ToContact().VoiceId);
    }

    [TestMethod]
    public void Voice_WithoutAGenderOffersBothAndPreservesAnUnavailableVoice()
    {
        FakeSpeech speech = new();
        Contact contact = new() { Id = ContactId.New(), VoiceId = "future_voice" };
        using ContactDetailViewModel detail = new(contact, voices: speech.Voices);

        Assert.HasCount(3, detail.VoiceOptions);
        Assert.AreEqual("future_voice", detail.ToContact().VoiceId);
        Assert.IsFalse(detail.IsDirty);
    }

    [TestMethod]
    public void RefreshAgentOptions_OffersNewModelsKeepingTheAgentAndEdits()
    {
        AgentOption phi = new("Phi-3.5 mini", new AgentAssignment("Local", "phi-3.5-mini"));
        Contact walt = new() { Id = ContactId.New(), GivenName = "Walt", Agent = phi.Assignment };
        using ContactDetailViewModel detail = new(walt, [AgentOption.None, phi]);
        bool dirtyBefore = detail.IsDirty;

        AgentOption qwen = new("Qwen2.5 · Q4_K_M", new AgentAssignment("Local", "qwen"));
        detail.RefreshAgentOptions([AgentOption.None, phi with { Label = "Phi-3.5 mini" }, qwen]);

        Assert.IsFalse(dirtyBefore);
        Assert.IsFalse(detail.IsDirty);
        Assert.HasCount(3, detail.AgentOptions);
        Assert.AreEqual(phi.Assignment, detail.Agent.Assignment);
        Assert.AreEqual(detail.AgentOptions[1], detail.Agent);

        detail.GivenName = "Walter";
        detail.Agent = detail.AgentOptions[2];
        detail.RefreshAgentOptions([AgentOption.None, qwen]);

        Assert.IsTrue(detail.IsDirty);
        Assert.AreEqual("Walter", detail.GivenName);
        Assert.AreEqual(qwen.Assignment, detail.ToContact().Agent);
    }

    [TestMethod]
    public void RefreshAgentOptions_KeepsAnAgentWhoseModelWasRemoved()
    {
        AgentOption qwen = new("Qwen2.5 · Q4_K_M", new AgentAssignment("Local", "qwen"));
        Contact walt = new() { Id = ContactId.New(), GivenName = "Walt", Agent = qwen.Assignment };
        using ContactDetailViewModel detail = new(walt, [AgentOption.None, qwen]);

        detail.RefreshAgentOptions([AgentOption.None]);

        Assert.AreEqual("Qwen2.5 · Q4_K_M (removed)", detail.Agent.Label);
        Assert.AreEqual(qwen.Assignment, detail.ToContact().Agent);
        Assert.IsFalse(detail.IsDirty);

        detail.RefreshAgentOptions([AgentOption.None, qwen]);

        Assert.AreEqual(qwen, detail.Agent);
        Assert.HasCount(2, detail.AgentOptions);
    }

    [TestMethod]
    public void Voice_ChoosingAVoiceKeepsTheOptionsAndTheSelection()
    {
        IReadOnlyList<SpeechVoice> voices = [new("af_heart", "Heart", Gender.Female), new("af_bella", "Bella", Gender.Female)];
        using ContactDetailViewModel detail = new(new Contact { Id = ContactId.New(), GivenName = "Rose", Gender = Gender.Female }, null, voices, true);
        IReadOnlyList<VoiceOption> options = detail.VoiceOptions;

        detail.Voice = options.Single(option => option.Id == "af_bella");

        Assert.AreSame(options, detail.VoiceOptions);
        Assert.AreEqual("af_bella", detail.Voice?.Id);
        Assert.AreEqual("af_bella", detail.ToContact().VoiceId);
        Assert.Contains("Bella", detail.VoiceDescription);
        Assert.IsTrue(detail.IsDirty);
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
