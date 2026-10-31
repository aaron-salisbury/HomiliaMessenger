using FluentValidation;
using FluentValidation.Results;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Business.Modules.Contacts;

internal sealed class ContactService : IContactService
{
    private readonly IContactStore _store;
    private readonly IValidator<Contact> _validator;

    public ContactService(IContactStore store, IValidator<Contact> validator)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
    }

    public Task<IReadOnlyList<Contact>> ListAsync(CancellationToken cancellationToken = default)
    {
        return _store.ListAsync(cancellationToken);
    }

    public Task<Contact?> GetAsync(ContactId id, CancellationToken cancellationToken = default)
    {
        return _store.GetAsync(id, cancellationToken);
    }

    public async Task<ValidationResult> SaveAsync(Contact contact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contact);

        ValidationResult result = await _validator.ValidateAsync(contact, cancellationToken);

        if (result.IsValid)
        {
            await _store.UpsertAsync(contact, cancellationToken);
        }

        return result;
    }

    public Task<bool> DeleteAsync(ContactId id, CancellationToken cancellationToken = default)
    {
        if (id.Value == Guid.Empty)
        {
            throw new ArgumentException("A contact ID is required.", nameof(id));
        }

        return _store.DeleteAsync(id, cancellationToken);
    }
}
