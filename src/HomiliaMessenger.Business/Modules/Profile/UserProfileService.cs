using FluentValidation;
using FluentValidation.Results;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Business.Modules.Profile;

internal sealed class UserProfileService : IUserProfileService
{
    private readonly IUserProfileStore _store;
    private readonly IValidator<UserProfile> _validator;

    public UserProfileService(IUserProfileStore store, IValidator<UserProfile> validator)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
    }

    public async Task<UserProfile> GetAsync(CancellationToken cancellationToken = default)
    {
        return await _store.GetAsync(cancellationToken) ?? new UserProfile();
    }

    public async Task<ValidationResult> SaveAsync(UserProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        ValidationResult result = await _validator.ValidateAsync(profile, cancellationToken);

        if (result.IsValid)
        {
            await _store.SaveAsync(profile, cancellationToken);
        }

        return result;
    }
}
