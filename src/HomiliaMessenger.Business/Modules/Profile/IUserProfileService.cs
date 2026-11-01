using FluentValidation.Results;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Business.Modules.Profile;

public interface IUserProfileService
{
    /// <summary>
    /// The user's profile, empty until it is first saved.
    /// </summary>
    Task<UserProfile> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates and saves the profile. An invalid profile is not saved; the returned result describes why.
    /// </summary>
    Task<ValidationResult> SaveAsync(UserProfile profile, CancellationToken cancellationToken = default);
}

/// <summary>
/// Persistence capability required by the profile module.
/// </summary>
public interface IUserProfileStore
{
    Task<UserProfile?> GetAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(UserProfile profile, CancellationToken cancellationToken = default);
}
