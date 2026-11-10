using Microsoft.Data.Sqlite;
using Praxis.Storage.Catalog;
using Xunit;

namespace Praxis.Storage.Tests.Catalog;

public sealed class CatalogDatabaseTests : IDisposable
{
    private readonly CatalogDatabase database;

    public CatalogDatabaseTests()
    {
        SqliteConnection connection = new("Data Source=:memory:");
        connection.Open();
        database = new CatalogDatabase(connection);
        database.Execute(c =>
        {
            using SqliteCommand create = c.CreateCommand("CREATE TABLE counter (value INTEGER NOT NULL)");
            create.ExecuteNonQuery();
            using SqliteCommand seed = c.CreateCommand("INSERT INTO counter (value) VALUES (0)");
            seed.ExecuteNonQuery();
        });
    }

    [Fact]
    public void NestedOperationOnTheOwningThreadDoesNotDeadlock()
    {
        long value = database.Execute(_ =>
            database.Execute(inner =>
            {
                using SqliteCommand read = inner.CreateCommand("SELECT value FROM counter");
                return (long)read.ExecuteScalar()!;
            }));

        Assert.Equal(0, value);
    }

    [Fact]
    public async Task ConcurrentReadModifyWriteOperationsAreSerialized()
    {
        const int writers = 8;
        const int incrementsPerWriter = 200;
        ManualResetEventSlim start = new();
        Task[] tasks = Enumerable.Range(0, writers).Select(_ => Task.Run(() =>
        {
            start.Wait();
            for (int i = 0; i < incrementsPerWriter; i++)
            {
                // Without whole-callback exclusion, interleaved callbacks would lose
                // updates or fault on the shared connection.
                database.Execute(connection =>
                {
                    using SqliteCommand read = connection.CreateCommand("SELECT value FROM counter");
                    long current = (long)read.ExecuteScalar()!;
                    using SqliteCommand write = connection.CreateCommand("UPDATE counter SET value = $value");
                    write.Parameters.AddWithValue("$value", current + 1);
                    write.ExecuteNonQuery();
                });
            }
        })).ToArray();

        start.Set();
        await Task.WhenAll(tasks);

        long total = database.Execute(connection =>
        {
            using SqliteCommand read = connection.CreateCommand("SELECT value FROM counter");
            return (long)read.ExecuteScalar()!;
        });
        Assert.Equal(writers * incrementsPerWriter, total);
    }

    public void Dispose() => database.Dispose();
}
