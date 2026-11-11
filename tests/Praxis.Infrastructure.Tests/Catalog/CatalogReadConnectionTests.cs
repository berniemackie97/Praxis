using Microsoft.Data.Sqlite;
using Praxis.Infrastructure.Persistence;
using Praxis.Infrastructure.Tests.Support;
using Xunit;

namespace Praxis.Infrastructure.Tests.Catalog;

public sealed class CatalogReadConnectionTests : IDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private readonly string root = Path.Combine(TestPaths.Temp, "praxis-read-connection-" + Guid.NewGuid().ToString("N"));
    private readonly CatalogDatabase database;

    public CatalogReadConnectionTests()
    {
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "catalog.sqlite");
        SqliteConnection writer = new($"Data Source={path};Pooling=False");
        writer.Open();
        using (SqliteCommand setup = writer.CreateCommand("PRAGMA journal_mode = WAL; CREATE TABLE counter (value INTEGER NOT NULL); INSERT INTO counter VALUES (1);"))
        {
            setup.ExecuteNonQuery();
        }

        SqliteConnection reader = new($"Data Source={path};Mode=ReadOnly;Pooling=False");
        reader.Open();
        database = new CatalogDatabase(writer, reader);
    }

    [Fact]
    public async Task QueriesReadCommittedStateWhileAWriteHoldsTheGate()
    {
        using ManualResetEventSlim writing = new();
        using ManualResetEventSlim release = new();
        Task write = Task.Run(() => database.Execute(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand update = connection.CreateCommand("UPDATE counter SET value = 2");
            update.Transaction = transaction;
            update.ExecuteNonQuery();
            writing.Set();
            release.Wait(Deadline);
            transaction.Commit();
        }));
        Assert.True(writing.Wait(Deadline));

        long during = await Task.Run(() => Value(database.ExecuteRead));
        release.Set();
        await write.WaitAsync(Deadline);

        Assert.Equal(1, during);
        Assert.Equal(2, Value(database.ExecuteRead));
    }

    [Fact]
    public void QueriesInsideAWriteSeeItsUncommittedChanges()
    {
        long inside = database.Execute(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand update = connection.CreateCommand("UPDATE counter SET value = 5");
            update.Transaction = transaction;
            update.ExecuteNonQuery();
            long seen = Value(database.ExecuteRead);
            transaction.Rollback();
            return seen;
        });

        Assert.Equal(5, inside);
        Assert.Equal(1, Value(database.ExecuteRead));
    }

    public void Dispose()
    {
        database.Dispose();
        Directory.Delete(root, recursive: true);
    }

    private static long Value(Func<Func<SqliteConnection, long>, long> read) => read(connection =>
    {
        using SqliteCommand query = connection.CreateCommand("SELECT value FROM counter");
        return (long)query.ExecuteScalar()!;
    });
}
