using FluentValidation;
using HomiliaMessenger.Business.Modules.Contacts;
using System;

namespace HomiliaMessenger.Business.Modules.Profile;

internal sealed class UserProfileValidator : AbstractValidator<UserProfile>
{
    private readonly TimeProvider _timeProvider;

    public UserProfileValidator() : this(TimeProvider.System)
    {
    }

    internal UserProfileValidator(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

        RuleFor(profile => profile.Name).MaximumLength(ContactValidator.MAX_NAME_LENGTH);
        RuleFor(profile => profile.Gender).IsInEnum();
        RuleFor(profile => profile.About).MaximumLength(UserProfile.MAX_ABOUT_LENGTH);

        // Judged against the UTC calendar date, as contact birthdays are.
        RuleFor(profile => profile.Birthday)
            .Must(birthday => birthday!.Value <= UtcToday())
            .When(profile => profile.Birthday is not null)
            .WithMessage("Your birthday cannot be in the future.");

        RuleFor(profile => profile.Birthday)
            .Must(birthday => ContactBirthday.AgeOn(birthday!.Value, UtcToday()) <= ContactValidator.MAX_AGE)
            .When(profile => profile.Birthday is not null)
            .WithMessage($"Your age must be {ContactValidator.MAX_AGE} or younger.");

        RuleFor(profile => profile.Photo)
            .Must(photo => photo is null || photo.Length <= ContactValidator.MAX_PHOTO_BYTES)
            .WithMessage("Profile photo exceeds the 8 MiB limit.");
    }

    private DateOnly UtcToday()
    {
        return DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime);
    }
}
