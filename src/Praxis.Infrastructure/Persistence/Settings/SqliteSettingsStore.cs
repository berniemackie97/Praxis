using Praxis.Application.Policy;

namespace Praxis.Infrastructure.Persistence.Settings;

/// <summary>Small owner settings, one value per key.</summary>
public sealed class SqliteSettingsStore : ISettingsStore
{
    private readonly CatalogDatabase database;
    private readonly TimeProvider clock;

    internal SqliteSettingsStore(CatalogDatabase database, TimeProvider clock)
    {
        this.database = database;
        this.clock = clock;
    }

    public string? Read(string key) => database.ExecuteRead(connection =>
    {
        using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand("SELECT value FROM runtime_settings WHERE key = $key");
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    });

    public void Write(string key, string value)
    {
        if (key.Length is < 1 or > 64 || value.Length > 256 || value.Any(char.IsControl))
        {
            throw new ArgumentException("The setting is invalid.");
        }

        database.Execute(connection =>
        {
            using Microsoft.Data.Sqlite.SqliteTransaction transaction = connection.BeginTransaction();
            connection.Run(transaction, """
                INSERT INTO runtime_settings (key, value, updated_utc) VALUES ($key, $value, $now)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_utc = excluded.updated_utc
                """, ("$key", key), ("$value", value), ("$now", CatalogValues.Timestamp(clock.GetUtcNow())));
            transaction.Commit();
            return true;
        });
    }
}
