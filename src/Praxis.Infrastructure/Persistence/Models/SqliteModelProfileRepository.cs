using Microsoft.Data.Sqlite;
using Praxis.Application.Models;
using Praxis.Domain.Models;

namespace Praxis.Infrastructure.Persistence.Models;

/// <summary>SQLite persistence of the owner's model profiles; only normalized, valid profiles are stored.</summary>
public sealed class SqliteModelProfileRepository : IModelProfileRepository
{
    private readonly CatalogDatabase database;
    private readonly TimeProvider clock;

    internal SqliteModelProfileRepository(CatalogDatabase database, TimeProvider clock)
    {
        this.database = database;
        this.clock = clock;
    }

    public ModelProfile? Get(string role)
    {
        ModelProfile.ValidateRole(role);
        return database.ExecuteRead(connection =>
        {
            using SqliteCommand query = connection.CreateCommand("""
                                                                 SELECT provider, endpoint, model, context_tokens, enabled
                                                                 FROM model_profiles WHERE role = $role
                                                                 """);
            query.Parameters.AddWithValue("$role", role);
            using SqliteDataReader reader = query.ExecuteReader();
            return reader.Read()
                ? new ModelProfile(role, reader.GetString(0), reader.GetString(1),
                    reader.GetString(2), reader.GetInt32(3), reader.GetInt64(4) == 1)
                : null;
        });
    }

    public ModelProfile Save(ModelProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ModelProfile saved = profile.Normalized();
        database.Execute(connection =>
        {
            using SqliteCommand command = connection.CreateCommand("""
                                                                   INSERT INTO model_profiles
                                                                       (role, provider, endpoint, model, context_tokens, enabled, updated_utc)
                                                                   VALUES ($role, $provider, $endpoint, $model, $context, $enabled, $now)
                                                                   ON CONFLICT(role) DO UPDATE SET provider = excluded.provider,
                                                                       endpoint = excluded.endpoint, model = excluded.model,
                                                                       context_tokens = excluded.context_tokens, enabled = excluded.enabled,
                                                                       updated_utc = excluded.updated_utc
                                                                   """);
            command.Parameters.AddWithValue("$role", saved.Role);
            command.Parameters.AddWithValue("$provider", saved.Provider);
            command.Parameters.AddWithValue("$endpoint", saved.Endpoint);
            command.Parameters.AddWithValue("$model", saved.Model);
            command.Parameters.AddWithValue("$context", saved.ContextTokens);
            command.Parameters.AddWithValue("$enabled", saved.Enabled ? 1 : 0);
            command.Parameters.AddWithValue("$now", CatalogValues.Timestamp(clock.GetUtcNow()));
            command.ExecuteNonQuery();
        });
        return saved;
    }
}
