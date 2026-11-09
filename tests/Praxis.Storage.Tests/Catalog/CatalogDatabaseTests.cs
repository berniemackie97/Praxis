using Microsoft.Data.Sqlite;
using Praxis.Storage;
using Xunit;

namespace Praxis.Storage.Tests;

public sealed class CatalogDatabaseTests : IDisposable
{
    private readonly CatalogDatabase database;

    public CatalogDatabaseTests()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        database = new CatalogDatabase(connection);
        database.Execute(c =>
        {
            using var create = c.CreateCommand("CREATE TABLE counter (value INTEGER NOT NULL)");
            create.ExecuteNonQuery();
            using var seed = c.CreateCommand("INSERT INTO counter (value) VALUES (0)");
            seed.ExecuteNonQuery();
        });
    }

    [Fact]
    public void NestedOperationOnTheOwningThreadDoesNotDeadlock()
    {
        var value = database.Execute(_ =>
            database.Execute(inner =>
            {
                using var read = inner.CreateCommand("SELECT value FROM counter");
                return (long)read.ExecuteScalar()!;
            }));

        Assert.Equal(0, value);
    }

    [Fact]
    public async Task ConcurrentReadModifyWriteOperationsAreSerialized()
    {
        const int writers = 8;
        const int incrementsPerWriter = 200;
        var start = new ManualResetEventSlim();
        var tasks = Enumerable.Range(0, writers).Select(_ => Task.Run(() =>
        {
            start.Wait();
            for (var i = 0; i < incrementsPerWriter; i++)
            {
                // Without whole-callback exclusion, interleaved callbacks would lose
                // updates or fault on the shared connection.
                database.Execute(connection =>
                {
                    using var read = connection.CreateCommand("SELECT value FROM counter");
                    var current = (long)read.ExecuteScalar()!;
                    using var write = connection.CreateCommand("UPDATE counter SET value = $value");
                    write.Parameters.AddWithValue("$value", current + 1);
                    write.ExecuteNonQuery();
                });
            }
        })).ToArray();

        start.Set();
        await Task.WhenAll(tasks);

        var total = database.Execute(connection =>
        {
            using var read = connection.CreateCommand("SELECT value FROM counter");
            return (long)read.ExecuteScalar()!;
        });
        Assert.Equal(writers * incrementsPerWriter, total);
    }

    public void Dispose() => database.Dispose();
}
