using Microsoft.Data.Sqlite;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Data.Database;

internal interface IApplicationDatabase
{
    Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default);
}
