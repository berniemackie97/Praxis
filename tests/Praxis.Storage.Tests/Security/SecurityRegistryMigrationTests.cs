using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Praxis.Storage.Security;
using Praxis.Storage.Tests.Support;
using Xunit;

namespace Praxis.Storage.Tests.Security;

public sealed class SecurityRegistryMigrationTests : IDisposable
{
    private readonly string root = Path.Combine(TestPaths.Temp,
        "praxis-security-upgrade-" + Guid.NewGuid().ToString("N"));

    public SecurityRegistryMigrationTests()
    {
        Directory.CreateDirectory(root);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void ExistingV1DeviceAndSpaceSurviveMigrationAndCanEnrollWrappingKey()
    {
        using ECDsa signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using RSA wrapping = RSA.Create();
        wrapping.KeySize = 3072;
        Guid principal = Guid.NewGuid(), device = Guid.NewGuid(), space = Guid.NewGuid();
        string database = Path.Combine(root, "identity.sqlite");

        using (SqliteConnection legacy = new($"Data Source={database};Pooling=False"))
        {
            legacy.Open();
            using SqliteCommand setup = legacy.CreateCommand();
            setup.CommandText = """
                CREATE TABLE installation (singleton INTEGER PRIMARY KEY CHECK(singleton=1),
                                           installation_id TEXT NOT NULL UNIQUE) STRICT;
                CREATE TABLE principals (principal_id TEXT PRIMARY KEY, created_utc TEXT NOT NULL,
                                         disabled_utc TEXT) STRICT;
                CREATE TABLE devices (device_id TEXT PRIMARY KEY,
                                      principal_id TEXT NOT NULL REFERENCES principals(principal_id),
                                      public_key BLOB NOT NULL UNIQUE, enrolled_utc TEXT NOT NULL,
                                      revoked_utc TEXT) STRICT;
                CREATE TABLE security_spaces (space_id TEXT PRIMARY KEY,
                                              owner_id TEXT NOT NULL REFERENCES principals(principal_id),
                                              processing_class TEXT NOT NULL, created_utc TEXT NOT NULL) STRICT;
                CREATE TABLE space_grants (space_id TEXT NOT NULL REFERENCES security_spaces(space_id),
                                          principal_id TEXT NOT NULL REFERENCES principals(principal_id),
                                          permissions INTEGER NOT NULL, grant_version INTEGER NOT NULL DEFAULT 1,
                                          revoked_utc TEXT, PRIMARY KEY (space_id, principal_id)) STRICT;
                CREATE TABLE source_spaces (source_id TEXT PRIMARY KEY,
                                            space_id TEXT NOT NULL REFERENCES security_spaces(space_id)) STRICT;
                CREATE TABLE proof_challenges (
                    challenge_id TEXT PRIMARY KEY,
                    device_id TEXT NOT NULL REFERENCES devices(device_id),
                    space_id TEXT NOT NULL REFERENCES security_spaces(space_id),
                    source_id TEXT, permission INTEGER NOT NULL,
                    grant_version INTEGER NOT NULL, nonce BLOB NOT NULL,
                    expires_utc TEXT NOT NULL, consumed INTEGER NOT NULL DEFAULT 0
                ) STRICT;
                PRAGMA user_version=1;
                """;
            setup.ExecuteNonQuery();
            using (SqliteCommand seed = legacy.CreateCommand())
            {
                seed.CommandText = "INSERT INTO installation VALUES (1, $installation)";
                seed.Parameters.AddWithValue("$installation", Guid.NewGuid().ToString("N"));
                seed.ExecuteNonQuery();
            }

            using (SqliteCommand seed = legacy.CreateCommand())
            {
                seed.CommandText = "INSERT INTO principals VALUES ($id, $when, NULL)";
                seed.Parameters.AddWithValue("$id", principal.ToString("N"));
                seed.Parameters.AddWithValue("$when", "2026-11-10T00:00:00Z");
                seed.ExecuteNonQuery();
            }

            using (SqliteCommand seed = legacy.CreateCommand())
            {
                seed.CommandText = "INSERT INTO devices VALUES ($device, $principal, $signing, $when, NULL)";
                seed.Parameters.AddWithValue("$device", device.ToString("N"));
                seed.Parameters.AddWithValue("$principal", principal.ToString("N"));
                seed.Parameters.AddWithValue("$signing", signer.ExportSubjectPublicKeyInfo());
                seed.Parameters.AddWithValue("$when", "2026-11-10T00:00:00Z");
                seed.ExecuteNonQuery();
            }

            using (SqliteCommand seed = legacy.CreateCommand())
            {
                seed.CommandText = "INSERT INTO security_spaces VALUES ($space, $owner, $class, $when)";
                seed.Parameters.AddWithValue("$space", space.ToString("N"));
                seed.Parameters.AddWithValue("$owner", principal.ToString("N"));
                seed.Parameters.AddWithValue("$class", "client-held");
                seed.Parameters.AddWithValue("$when", "2026-11-10T00:00:00Z");
                seed.ExecuteNonQuery();
            }
        }

        using SecurityRegistry upgraded = SecurityRegistry.Open(root);
        DeviceChallenge challenge = upgraded.DeviceProofs.IssueChallenge(
            device, space, null, SecurityPermission.Write);
        byte[] signature = signer.SignData(challenge.Message, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        VerifiedDeviceProof? proof = upgraded.DeviceProofs.Authenticate(challenge.ChallengeId, signature);
        Assert.NotNull(proof);
        Assert.Equal(principal, proof.PrincipalId);
        Assert.Throws<InvalidOperationException>(() => upgraded.SpaceKeys.Recipients(proof));

        byte[] signing = signer.ExportSubjectPublicKeyInfo();
        byte[] publicWrapping = wrapping.ExportSubjectPublicKeyInfo();
        byte[] binding = signer.SignData(DeviceWrappingKey.BindingMessage(signing, publicWrapping),
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        upgraded.Operator.RegisterWrappingKey(device, publicWrapping, binding);
        Assert.Single(upgraded.SpaceKeys.Recipients(proof));

        using SqliteConnection inspect = new($"Data Source={database};Pooling=False");
        inspect.Open();
        using SqliteCommand version = inspect.CreateCommand();
        version.CommandText = "PRAGMA user_version";
        Assert.Equal(3L, Convert.ToInt64(version.ExecuteScalar()));
    }

    [Fact]
    public void UnknownNewVersionAndDamagedSchemaFailClosed()
    {
        using (SecurityRegistry initialized = SecurityRegistry.Open(root))
        {
            Assert.NotEqual(Guid.Empty, initialized.InstallationId);
        }

        string database = Path.Combine(root, "identity.sqlite");
        using (SqliteConnection tamper = new($"Data Source={database};Pooling=False"))
        {
            tamper.Open();
            using SqliteCommand command = tamper.CreateCommand();
            command.CommandText = "PRAGMA user_version=999";
            command.ExecuteNonQuery();
        }

        Assert.Throws<NotSupportedException>(() => SecurityRegistry.Open(root));
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
