using FluentValidation;
using System;

namespace HomiliaMessenger.Business.Modules.Contacts;

internal sealed class ContactValidator : AbstractValidator<Contact>
{
    internal const int MAX_PHOTO_BYTES = 8 * 1024 * 1024;
    internal const int MIN_AGE = 18;
    internal const int MAX_AGE = 150;
    internal const int MAX_NAME_LENGTH = 100;
    internal const int MAX_PERSONA_TEXT_LENGTH = 8000;

    private readonly TimeProvider _timeProvider;

    public ContactValidator() : this(TimeProvider.System)
    {
    }

    internal ContactValidator(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

        RuleFor(contact => contact.Id.Value)
            .NotEqual(Guid.Empty)
            .WithMessage("A contact ID is required.");

        RuleFor(contact => contact)
            .Must(contact => !string.IsNullOrWhiteSpace(contact.GivenName) || !string.IsNullOrWhiteSpace(contact.FamilyName) || !string.IsNullOrWhiteSpace(contact.Nickname))
            .WithName("Name")
            .WithMessage("A given name, family name or nickname is required.");

        RuleFor(contact => contact.GivenName).MaximumLength(MAX_NAME_LENGTH);
        RuleFor(contact => contact.FamilyName).MaximumLength(MAX_NAME_LENGTH);
        RuleFor(contact => contact.Nickname).MaximumLength(MAX_NAME_LENGTH);
        RuleFor(contact => contact.Gender).MaximumLength(MAX_NAME_LENGTH);

        // Personas must be adults. Age is judged against the UTC calendar date so the rule does not depend on the machine's time zone.
        RuleFor(contact => contact.Birthday)
            .NotNull()
            .WithMessage("A birthday is required.")
            .DependentRules(() =>
            {
                RuleFor(contact => contact.Birthday)
                    .Must(birthday => ContactBirthday.AgeOn(birthday!.Value, UtcToday()) >= MIN_AGE)
                    .WithMessage($"Contacts must be at least {MIN_AGE} years old.");

                RuleFor(contact => contact.Birthday)
                    .Must(birthday => ContactBirthday.AgeOn(birthday!.Value, UtcToday()) <= MAX_AGE)
                    .WithMessage($"Contacts must be {MAX_AGE} years old or younger.");
            });

        RuleFor(contact => contact.Personality).MaximumLength(MAX_PERSONA_TEXT_LENGTH);
        RuleFor(contact => contact.Background).MaximumLength(MAX_PERSONA_TEXT_LENGTH);

        RuleFor(contact => contact.Agent!.Provider)
            .NotEmpty()
            .When(contact => contact.Agent is not null)
            .WithName("Agent provider")
            .WithMessage("An agent provider is required when an agent model is set.");

        RuleFor(contact => contact.Agent!.Model)
            .NotEmpty()
            .When(contact => contact.Agent is not null)
            .WithName("Agent model")
            .WithMessage("An agent model is required when an agent provider is set.");

        RuleFor(contact => contact.Photo)
            .Must(photo => photo is null || photo.Length <= MAX_PHOTO_BYTES)
            .WithMessage("Contact photo exceeds the 8 MiB limit.");
    }

    private DateOnly UtcToday()
    {
        return DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime);
    }
}
