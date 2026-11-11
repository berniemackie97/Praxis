using Microsoft.Data.Sqlite;

namespace Praxis.Infrastructure.Catalog;

/// <summary>
/// Owns the single catalog SQLite connection. Every statement runs while holding the
/// catalog gate, so stores cannot reach the connection outside serialized access.
/// The gate is reentrant: a store may call another store from inside an operation.
/// </summary>
internal sealed class CatalogDatabase(SqliteConnection connection) : IDisposable
{
    private readonly Lock gate = new();

    internal T Execute<T>(Func<SqliteConnection, T> operation)
    {
        lock (gate)
        {
            return operation(connection);
        }
    }

    internal void Execute(Action<SqliteConnection> operation)
    {
        lock (gate)
        {
            operation(connection);
        }
    }

    public void Dispose() => connection.Dispose();
}
