using FluentValidation.Results;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Business.Modules.Contacts;

public interface IContactService
{
    Task<IReadOnlyList<Contact>> ListAsync(CancellationToken cancellationToken = default);
    Task<Contact?> GetAsync(ContactId id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates and saves a contact. An invalid contact is not saved; the returned result describes why.
    /// </summary>
    Task<ValidationResult> SaveAsync(Contact contact, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(ContactId id, CancellationToken cancellationToken = default);
}

/// <summary>
/// Persistence capability required by the contacts module.
/// </summary>
public interface IContactStore
{
    Task<IReadOnlyList<Contact>> ListAsync(CancellationToken cancellationToken = default);
    Task<Contact?> GetAsync(ContactId id, CancellationToken cancellationToken = default);
    Task UpsertAsync(Contact contact, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(ContactId id, CancellationToken cancellationToken = default);
}
