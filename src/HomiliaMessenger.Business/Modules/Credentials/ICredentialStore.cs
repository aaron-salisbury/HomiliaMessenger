using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Business.Modules.Credentials;

public interface ICredentialStore
{
    Task DeleteAsync(CredentialReference reference, CancellationToken cancellationToken = default);

    Task<CredentialMaterial?> GetAsync(CredentialReference reference, CancellationToken cancellationToken = default);

    Task StoreAsync(CredentialReference reference, CredentialMaterial material, CancellationToken cancellationToken = default);
}
