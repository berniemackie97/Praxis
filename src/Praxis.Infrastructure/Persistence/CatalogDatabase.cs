using Microsoft.Data.Sqlite;

namespace Praxis.Infrastructure.Persistence;

/// <summary>
/// Owns the catalog's SQLite connections. Every write runs on the single writer connection while
/// holding the catalog gate, so stores cannot reach it outside serialized access; the gate is
/// reentrant, so a store may call another store from inside an operation. Pure queries may use a
/// separate read-only connection: in WAL mode it reads the last committed state without waiting
/// for a writer, so owner queries are not blocked by background processing.
/// </summary>
internal sealed class CatalogDatabase(SqliteConnection connection, SqliteConnection? reader = null) : IDisposable
{
    private readonly Lock gate = new();
    private readonly Lock readerGate = new();

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

    /// <summary>
    /// Runs a query that writes nothing. Inside a write operation on the same thread it uses the
    /// writer connection, so it observes that operation's uncommitted changes.
    /// </summary>
    internal T ExecuteRead<T>(Func<SqliteConnection, T> query)
    {
        if (reader is null || gate.IsHeldByCurrentThread)
        {
            return Execute(query);
        }

        lock (readerGate)
        {
            return query(reader);
        }
    }

    /// <summary>
    /// Runs several read statements against one consistent snapshot on the read-only connection, so
    /// a revision number and the rows it describes come from the same committed state and owner
    /// queries never wait for background writes. Falls back to <see cref="ExecuteRead{T}"/> semantics
    /// inside a write operation or without a reader connection.
    /// </summary>
    internal T ExecuteSnapshot<T>(Func<SqliteConnection, T> query)
    {
        if (reader is null || gate.IsHeldByCurrentThread)
        {
            return Execute(query);
        }

        lock (readerGate)
        {
            Run(reader, "BEGIN DEFERRED");
            try
            {
                return query(reader);
            }
            finally
            {
                Run(reader, "COMMIT");
            }
        }
    }

    private static void Run(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        reader?.Dispose();
        connection.Dispose();
    }
}
