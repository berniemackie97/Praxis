using Microsoft.Data.Sqlite;
using Praxis.Inference;
using Praxis.Inference.Ollama;
using Praxis.Storage.Catalog;

namespace Praxis.Storage.Models;

/// <summary>Owner-selected local model for one processing role.</summary>
public sealed record ModelProfile(
    string Role, string Provider, string Endpoint, string Model, int ContextTokens, bool Enabled)
{
    public const string LocalEnrichment = "local-enrichment";
}

/// <summary>
/// Persists the owner's model choices in the private catalog so they travel with backups and
/// can be changed from the workbench. Only loopback endpoints of supported local providers
/// are accepted.
/// </summary>
public sealed class LocalModelProfiles
{
    private readonly CatalogDatabase database;

    internal LocalModelProfiles(CatalogDatabase database) => this.database = database;

    public ModelProfile? Get(string role)
    {
        ValidateRole(role);
        return database.Execute(connection =>
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
        ValidateRole(profile.Role);
        if (profile.Provider != OllamaRuntime.ProviderName)
        {
            throw new ArgumentException("The model provider is not supported.", nameof(profile));
        }

        string endpoint = LoopbackEndpoint.Validate(profile.Endpoint).GetLeftPart(UriPartial.Authority) + "/";
        string model = profile.Model?.Trim() ?? "";
        if (model.Length is < 1 or > 200 || model.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
        {
            throw new ArgumentException("The model name is invalid.", nameof(profile));
        }

        if (profile.ContextTokens is < 2_048 or > 262_144)
        {
            throw new ArgumentOutOfRangeException(nameof(profile), "Context must be 2,048 to 262,144 tokens.");
        }

        ModelProfile saved = profile with { Endpoint = endpoint, Model = model };
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
            command.Parameters.AddWithValue("$now", CatalogValues.Timestamp(DateTimeOffset.UtcNow));
            command.ExecuteNonQuery();
        });
        return saved;
    }

    private static void ValidateRole(string role)
    {
        if (role != ModelProfile.LocalEnrichment)
        {
            throw new ArgumentException("The model role is not supported.", nameof(role));
        }
    }
}
