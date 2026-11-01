using FluentValidation.Results;
using HomiliaMessenger.Business.Modules.Contacts;
using HomiliaMessenger.Data.Database;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace HomiliaMessenger.Tests.Contacts;

[TestClass]
public sealed class ContactServiceTests
{
    private static readonly DateOnly ADULT_BIRTHDAY = new(1980, 6, 15);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task SaveAsync_PersistsPersonaFieldsAndAgentAndReadsThemBack()
    {
        using TestDatabase database = await TestDatabase.CreateAsync(TestContext);
        IContactService service = database.Services.GetRequiredService<IContactService>();

        Contact vet = new()
        {
            Id = ContactId.New(),
            GivenName = "Walt",
            FamilyName = "Kowalski",
            Nickname = "Grandpa Walt",
            Birthday = new DateOnly(1948, 3, 14),
            Gender = Gender.Male,
            Relationship = "Grandfather",
            Personality = "Gruff but warm. Slow to open up about the war.",
            Background = "Drafted in 1968, Army medic. Grandfather of four.",
            Agent = new AgentAssignment("Ollama", "llama3.1:8b"),
            Photo = [137, 80, 78, 71, 13, 10, 26, 10],
            IsFavorite = true
        };

        ValidationResult result = await service.SaveAsync(vet, TestContext.CancellationToken);
        Contact? stored = await service.GetAsync(vet.Id, TestContext.CancellationToken);

        Assert.IsTrue(result.IsValid);
        Assert.IsNotNull(stored);
        Assert.AreEqual(vet with { Photo = null }, stored with { Photo = null });
        Assert.AreEqual(Convert.ToHexString(vet.Photo), Convert.ToHexString(stored.Photo!));
        Assert.AreEqual("Grandpa Walt", stored.DisplayName);
    }

    [TestMethod]
    public async Task SaveAsync_ChangingAgentKeepsTheSameContact()
    {
        using TestDatabase database = await TestDatabase.CreateAsync(TestContext);
        IContactService service = database.Services.GetRequiredService<IContactService>();
        Contact contact = new() { Id = ContactId.New(), GivenName = "Rose", Birthday = ADULT_BIRTHDAY, Agent = new AgentAssignment("Ollama", "llama3.1:8b") };

        await service.SaveAsync(contact, TestContext.CancellationToken);
        await service.SaveAsync(contact with { Agent = new AgentAssignment("Anthropic", "claude-sonnet-5-5") }, TestContext.CancellationToken);
        await service.SaveAsync(contact with { Agent = null }, TestContext.CancellationToken);

        IReadOnlyList<Contact> contacts = await service.ListAsync(TestContext.CancellationToken);

        Assert.HasCount(1, contacts);
        Assert.IsNull(contacts[0].Agent);
    }

    [TestMethod]
    public async Task SaveAsync_RejectsInvalidContactsWithoutSavingThem()
    {
        using TestDatabase database = await TestDatabase.CreateAsync(TestContext);
        IContactService service = database.Services.GetRequiredService<IContactService>();

        Contact[] invalid =
        [
            new() { Id = ContactId.New(), Birthday = ADULT_BIRTHDAY },
            new() { Id = new ContactId(Guid.Empty), GivenName = "No Id", Birthday = ADULT_BIRTHDAY },
            new() { Id = ContactId.New(), GivenName = "No Birthday" },
            new() { Id = ContactId.New(), GivenName = "Minor", Birthday = UtcToday().AddYears(-17) },
            new() { Id = ContactId.New(), GivenName = "Too Old", Birthday = UtcToday().AddYears(-151) },
            new() { Id = ContactId.New(), GivenName = "Unborn", Birthday = UtcToday().AddDays(1) },
            new() { Id = ContactId.New(), GivenName = "Half Agent", Birthday = ADULT_BIRTHDAY, Agent = new AgentAssignment("Ollama", string.Empty) },
            new() { Id = ContactId.New(), GivenName = "Half Agent", Birthday = ADULT_BIRTHDAY, Agent = new AgentAssignment(" ", "llama3.1:8b") },
            new() { Id = ContactId.New(), GivenName = "Huge Photo", Birthday = ADULT_BIRTHDAY, Photo = new byte[(8 * 1024 * 1024) + 1] }
        ];

        foreach (Contact contact in invalid)
        {
            ValidationResult result = await service.SaveAsync(contact, TestContext.CancellationToken);
            Assert.IsFalse(result.IsValid, contact.GivenName ?? "nameless contact");
        }

        IReadOnlyList<Contact> stored = await service.ListAsync(TestContext.CancellationToken);
        Assert.IsEmpty(stored);
    }

    [TestMethod]
    public async Task DeleteAsync_RemovesOnlyTheSelectedContact()
    {
        using TestDatabase database = await TestDatabase.CreateAsync(TestContext);
        IContactService service = database.Services.GetRequiredService<IContactService>();
        Contact keep = new() { Id = ContactId.New(), GivenName = "Keep", Birthday = ADULT_BIRTHDAY };
        Contact remove = new() { Id = ContactId.New(), GivenName = "Remove", Birthday = ADULT_BIRTHDAY };

        await service.SaveAsync(keep, TestContext.CancellationToken);
        await service.SaveAsync(remove, TestContext.CancellationToken);

        bool deleted = await service.DeleteAsync(remove.Id, TestContext.CancellationToken);
        bool deletedAgain = await service.DeleteAsync(remove.Id, TestContext.CancellationToken);
        IReadOnlyList<Contact> contacts = await service.ListAsync(TestContext.CancellationToken);

        Assert.IsTrue(deleted);
        Assert.IsFalse(deletedAgain);
        Assert.HasCount(1, contacts);
        Assert.AreEqual(keep.Id, contacts[0].Id);
    }

    [TestMethod]
    public async Task ListAsync_OrdersFavoritesFirstThenByDisplayName()
    {
        using TestDatabase database = await TestDatabase.CreateAsync(TestContext);
        IContactService service = database.Services.GetRequiredService<IContactService>();

        await service.SaveAsync(new Contact { Id = ContactId.New(), GivenName = "charlie", Birthday = ADULT_BIRTHDAY }, TestContext.CancellationToken);
        await service.SaveAsync(new Contact { Id = ContactId.New(), Nickname = "Bravo", Birthday = ADULT_BIRTHDAY }, TestContext.CancellationToken);
        await service.SaveAsync(new Contact { Id = ContactId.New(), GivenName = "Zulu", Birthday = ADULT_BIRTHDAY, IsFavorite = true }, TestContext.CancellationToken);
        await service.SaveAsync(new Contact { Id = ContactId.New(), GivenName = "Alpha", Birthday = ADULT_BIRTHDAY }, TestContext.CancellationToken);

        IReadOnlyList<Contact> contacts = await service.ListAsync(TestContext.CancellationToken);

        Assert.AreSequenceEqual(new[] { "Zulu", "Alpha", "Bravo", "charlie" }, new[] { contacts[0].DisplayName, contacts[1].DisplayName, contacts[2].DisplayName, contacts[3].DisplayName });
    }

    [TestMethod]
    public async Task InitializeApplicationDataAsync_CanRunRepeatedly()
    {
        using TestDatabase database = await TestDatabase.CreateAsync(TestContext);

        await database.Services.InitializeApplicationDataAsync(TestContext.CancellationToken);

        IContactService service = database.Services.GetRequiredService<IContactService>();
        IReadOnlyList<Contact> contacts = await service.ListAsync(TestContext.CancellationToken);
        Assert.IsEmpty(contacts);
    }

    private static DateOnly UtcToday()
    {
        return DateOnly.FromDateTime(DateTime.UtcNow);
    }
}
