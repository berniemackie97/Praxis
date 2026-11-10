using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Praxis.Storage.Security;
using Praxis.Storage.Tests.Support;
using Xunit;

namespace Praxis.Storage.Tests.Security;

public sealed class SecurityRegistryTests : IDisposable
{
    private readonly string root = Path.Combine(TestPaths.Temp, "praxis-security-" + Guid.NewGuid().ToString("N"));

    public SecurityRegistryTests()
    {
        Directory.CreateDirectory(root);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void SignedProofBindsDeviceSpaceSourceAndOperationAndCannotBeReplayed()
    {
        using SecurityRegistry registry = SecurityRegistry.Open(root);
        Guid owner = registry.Operator.CreatePrincipal();
        using ECDsa key = NewKey();
        Guid device = registry.Operator.EnrollDevice(owner, key.ExportSubjectPublicKeyInfo());
        Guid space = registry.Operator.CreateSpace(owner, SecurityProcessingClass.ClientHeld);
        Guid source = Guid.NewGuid();
        registry.Operator.AssignSource(owner, space, source);

        DeviceChallenge challenge = registry.DeviceProofs.IssueChallenge(device, space, source, SecurityPermission.Read);
        byte[] signature = Sign(key, challenge);
        VerifiedDeviceProof? accepted = registry.DeviceProofs.Authenticate(challenge.ChallengeId, signature);

        Assert.NotNull(accepted);
        Assert.Equal(owner, accepted.PrincipalId);
        Assert.Equal(device, accepted.DeviceId);
        Assert.Equal(space, accepted.SpaceId);
        Assert.Equal((Guid?)source, accepted.SourceId);
        Assert.Equal(SecurityPermission.Read, accepted.Permission);
        Assert.True(registry.DeviceProofs.IsCurrent(accepted));
        Assert.Null(registry.DeviceProofs.Authenticate(challenge.ChallengeId, signature));

        DeviceChallenge write = registry.DeviceProofs.IssueChallenge(device, space, source, SecurityPermission.Write);
        Assert.Null(registry.DeviceProofs.Authenticate(write.ChallengeId, signature));
        Assert.Null(registry.DeviceProofs.Authenticate(write.ChallengeId, Sign(key, write)));
    }

    [Fact]
    public void WrongDeviceKeyCannotCompleteAChallenge()
    {
        using SecurityRegistry registry = SecurityRegistry.Open(root);
        Guid owner = registry.Operator.CreatePrincipal();
        Guid space = registry.Operator.CreateSpace(owner, SecurityProcessingClass.TrustedHost);
        using ECDsa enrolledKey = NewKey();
        using ECDsa attackerKey = NewKey();
        Guid device = registry.Operator.EnrollDevice(owner, enrolledKey.ExportSubjectPublicKeyInfo());

        DeviceChallenge challenge = registry.DeviceProofs.IssueChallenge(device, space, null, SecurityPermission.Read);
        Assert.Null(registry.DeviceProofs.Authenticate(challenge.ChallengeId, Sign(attackerKey, challenge)));
        Assert.Null(registry.DeviceProofs.Authenticate(challenge.ChallengeId, Sign(enrolledKey, challenge)));
        Assert.Null(registry.DeviceProofs.Authenticate(Guid.NewGuid(), Sign(enrolledKey, challenge)));
    }

    [Fact]
    public void ForeignPrincipalAndSourceCannotBeAuthorizedByAnOrganizationalGrouping()
    {
        using SecurityRegistry registry = SecurityRegistry.Open(root);
        Guid owner = registry.Operator.CreatePrincipal();
        Guid visitor = registry.Operator.CreatePrincipal();
        using ECDsa ownerKey = NewKey();
        using ECDsa visitorKey = NewKey();
        Guid ownerDevice = registry.Operator.EnrollDevice(owner, ownerKey.ExportSubjectPublicKeyInfo());
        Guid visitorDevice = registry.Operator.EnrollDevice(visitor, visitorKey.ExportSubjectPublicKeyInfo());
        Guid privateSpace = registry.Operator.CreateSpace(owner, SecurityProcessingClass.ClientHeld);
        Guid sharedSpace = registry.Operator.CreateSpace(owner, SecurityProcessingClass.TrustedHost);
        Guid privateSource = Guid.NewGuid();
        Guid sharedSource = Guid.NewGuid();
        registry.Operator.AssignSource(owner, privateSpace, privateSource);
        registry.Operator.AssignSource(owner, sharedSpace, sharedSource);
        registry.Operator.Grant(owner, sharedSpace, visitor, SecurityPermission.Read);

        DeviceChallenge shared = registry.DeviceProofs.IssueChallenge(visitorDevice, sharedSpace, sharedSource, SecurityPermission.Read);
        Assert.NotNull(registry.DeviceProofs.Authenticate(shared.ChallengeId, Sign(visitorKey, shared)));
        Assert.Throws<UnauthorizedAccessException>(() =>
            registry.DeviceProofs.IssueChallenge(visitorDevice, privateSpace, privateSource, SecurityPermission.Read));
        Assert.Throws<UnauthorizedAccessException>(() =>
            registry.DeviceProofs.IssueChallenge(visitorDevice, sharedSpace, privateSource, SecurityPermission.Read));
        Assert.Throws<UnauthorizedAccessException>(() =>
            registry.DeviceProofs.IssueChallenge(visitorDevice, sharedSpace, sharedSource, SecurityPermission.Write));
        Assert.Throws<InvalidOperationException>(() =>
            registry.Operator.AssignSource(owner, sharedSpace, privateSource));

        DeviceChallenge privateRead = registry.DeviceProofs.IssueChallenge(ownerDevice, privateSpace, privateSource, SecurityPermission.Read);
        Assert.NotNull(registry.DeviceProofs.Authenticate(privateRead.ChallengeId, Sign(ownerKey, privateRead)));
        Assert.Throws<UnauthorizedAccessException>(() =>
            registry.Operator.Grant(visitor, privateSpace, owner, SecurityPermission.Read));
    }

    [Fact]
    public void GrantRevocationTakesEffectBeforeNextReadEvenForVerifiedProof()
    {
        using SecurityRegistry registry = SecurityRegistry.Open(root);
        Guid owner = registry.Operator.CreatePrincipal();
        Guid member = registry.Operator.CreatePrincipal();
        Guid space = registry.Operator.CreateSpace(owner, SecurityProcessingClass.TrustedHost);
        using ECDsa key = NewKey();
        Guid device = registry.Operator.EnrollDevice(member, key.ExportSubjectPublicKeyInfo());
        registry.Operator.Grant(owner, space, member, SecurityPermission.Read | SecurityPermission.Write);

        DeviceChallenge pending = registry.DeviceProofs.IssueChallenge(device, space, null, SecurityPermission.Write);
        VerifiedDeviceProof? prior = registry.DeviceProofs.Authenticate(pending.ChallengeId, Sign(key, pending));
        Assert.NotNull(prior);
        Assert.True(registry.DeviceProofs.IsCurrent(prior));

        Assert.True(registry.Operator.RevokeGrant(owner, space, member));
        Assert.False(registry.Operator.RevokeGrant(owner, space, member));
        Assert.False(registry.DeviceProofs.IsCurrent(prior));
        Assert.Throws<UnauthorizedAccessException>(() =>
            registry.DeviceProofs.IssueChallenge(device, space, null, SecurityPermission.Read));

        registry.Operator.Grant(owner, space, member, SecurityPermission.Read);
        Assert.False(registry.DeviceProofs.IsCurrent(prior));
        Assert.Throws<UnauthorizedAccessException>(() =>
            registry.DeviceProofs.IssueChallenge(device, space, null, SecurityPermission.Write));
        DeviceChallenge newRead = registry.DeviceProofs.IssueChallenge(device, space, null, SecurityPermission.Read);
        VerifiedDeviceProof? renewed = registry.DeviceProofs.Authenticate(newRead.ChallengeId, Sign(key, newRead));
        Assert.NotNull(renewed);
        Assert.True(registry.DeviceProofs.IsCurrent(renewed));
        registry.Operator.Grant(owner, space, member, SecurityPermission.Read);
        Assert.False(registry.DeviceProofs.IsCurrent(renewed));
    }

    [Fact]
    public void RevokedDeviceAndDisabledPrincipalCannotProveOrReuseAccess()
    {
        using SecurityRegistry registry = SecurityRegistry.Open(root);
        Guid owner = registry.Operator.CreatePrincipal();
        Guid space = registry.Operator.CreateSpace(owner, SecurityProcessingClass.TrustedHost);
        using ECDsa key = NewKey();
        Guid device = registry.Operator.EnrollDevice(owner, key.ExportSubjectPublicKeyInfo());

        DeviceChallenge used = registry.DeviceProofs.IssueChallenge(device, space, null, SecurityPermission.Read);
        VerifiedDeviceProof? prior = registry.DeviceProofs.Authenticate(used.ChallengeId, Sign(key, used));
        Assert.NotNull(prior);
        DeviceChallenge outstanding = registry.DeviceProofs.IssueChallenge(device, space, null, SecurityPermission.Write);

        Assert.True(registry.Operator.RevokeDevice(device));
        Assert.False(registry.Operator.RevokeDevice(device));
        Assert.False(registry.DeviceProofs.IsCurrent(prior));
        Assert.Null(registry.DeviceProofs.Authenticate(outstanding.ChallengeId, Sign(key, outstanding)));
        Assert.Throws<UnauthorizedAccessException>(() =>
            registry.DeviceProofs.IssueChallenge(device, space, null, SecurityPermission.Read));

        using ECDsa secondKey = NewKey();
        Guid secondDevice = registry.Operator.EnrollDevice(owner, secondKey.ExportSubjectPublicKeyInfo());
        DeviceChallenge second = registry.DeviceProofs.IssueChallenge(secondDevice, space, null, SecurityPermission.Read);
        Assert.True(registry.Operator.DisablePrincipal(owner));
        Assert.Null(registry.DeviceProofs.Authenticate(second.ChallengeId, Sign(secondKey, second)));
        Assert.Throws<UnauthorizedAccessException>(() =>
            registry.DeviceProofs.IssueChallenge(secondDevice, space, null, SecurityPermission.Read));
    }

    [Fact]
    public void RegistryPersistsEnrollmentSpaceAndSingleUseProofAcrossRestart()
    {
        Guid installation, owner, space, device, source;
        using ECDsa key = NewKey();
        DeviceChallenge challenge;

        using (SecurityRegistry registry = SecurityRegistry.Open(root))
        {
            installation = registry.InstallationId;
            owner = registry.Operator.CreatePrincipal();
            space = registry.Operator.CreateSpace(owner, SecurityProcessingClass.ClientHeld);
            device = registry.Operator.EnrollDevice(owner, key.ExportSubjectPublicKeyInfo());
            source = Guid.NewGuid();
            registry.Operator.AssignSource(owner, space, source);
            challenge = registry.DeviceProofs.IssueChallenge(device, space, source, SecurityPermission.Read);
        }

        using (SecurityRegistry reopened = SecurityRegistry.Open(root))
        {
            Assert.Equal(installation, reopened.InstallationId);
            VerifiedDeviceProof? accepted = reopened.DeviceProofs.Authenticate(challenge.ChallengeId, Sign(key, challenge));
            Assert.NotNull(accepted);
            Assert.Equal(owner, accepted.PrincipalId);
            Assert.True(reopened.DeviceProofs.IsCurrent(accepted));
        }

        using SecurityRegistry final = SecurityRegistry.Open(root);
        Assert.Null(final.DeviceProofs.Authenticate(challenge.ChallengeId, Sign(key, challenge)));
    }

    [Fact]
    public void ChallengeExpiresAndOutstandingRequestsAreBounded()
    {
        MutableClock clock = new(DateTimeOffset.Parse("2026-11-10T12:00:00Z"));
        using SecurityRegistry registry = SecurityRegistry.Open(root, clock);
        Guid owner = registry.Operator.CreatePrincipal();
        Guid space = registry.Operator.CreateSpace(owner, SecurityProcessingClass.TrustedHost);
        using ECDsa key = NewKey();
        Guid device = registry.Operator.EnrollDevice(owner, key.ExportSubjectPublicKeyInfo());

        DeviceChallenge first = registry.DeviceProofs.IssueChallenge(device, space, null, SecurityPermission.Read);
        for (int i = 1; i < 16; i++)
        {
            registry.DeviceProofs.IssueChallenge(device, space, null, SecurityPermission.Read);
        }
        Assert.Throws<InvalidOperationException>(() =>
            registry.DeviceProofs.IssueChallenge(device, space, null, SecurityPermission.Read));

        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Null(registry.DeviceProofs.Authenticate(first.ChallengeId, Sign(key, first)));
        Assert.NotNull(registry.DeviceProofs.IssueChallenge(device, space, null, SecurityPermission.Read));
    }

    [Fact]
    public void EnrollmentRejectsInvalidKeyDuplicateKeyAndUnknownPrincipal()
    {
        using SecurityRegistry registry = SecurityRegistry.Open(root);
        Guid owner = registry.Operator.CreatePrincipal();
        using ECDsa key = NewKey();
        byte[] encoded = key.ExportSubjectPublicKeyInfo();
        Assert.Throws<ArgumentException>(() => registry.Operator.EnrollDevice(owner, new byte[16]));
        Assert.Throws<UnauthorizedAccessException>(() =>
            registry.Operator.EnrollDevice(Guid.NewGuid(), encoded));
        Assert.NotEqual(Guid.Empty, registry.Operator.EnrollDevice(owner, encoded));
        Assert.Throws<SqliteException>(() => registry.Operator.EnrollDevice(owner, encoded));
    }

    [Fact]
    public void RegistryIsExclusiveAndUnknownSchemaVersionFailsClosed()
    {
        using (SecurityRegistry registry = SecurityRegistry.Open(root))
        {
            Assert.Throws<IOException>(() => SecurityRegistry.Open(root));
        }

        string file = Path.Combine(root, "identity.sqlite");
        using (SqliteConnection connection = new($"Data Source={file};Pooling=False"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version=999";
            command.ExecuteNonQuery();
        }
        Assert.Throws<NotSupportedException>(() => SecurityRegistry.Open(root));
    }

    [Fact]
    public void UnsafeDirectoryPermissionsFailClosed()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite |
            UnixFileMode.UserExecute | UnixFileMode.GroupRead);
        Assert.Throws<UnauthorizedAccessException>(() => SecurityRegistry.Open(root));
    }

    [Fact]
    public void PendingGrantProofCannotBeReanimatedByRevocationAndRegrant()
    {
        using SecurityRegistry registry = SecurityRegistry.Open(root);
        Guid owner = registry.Operator.CreatePrincipal();
        Guid member = registry.Operator.CreatePrincipal();
        Guid space = registry.Operator.CreateSpace(owner, SecurityProcessingClass.ClientHeld);
        using ECDsa key = NewKey();
        Guid device = registry.Operator.EnrollDevice(member, key.ExportSubjectPublicKeyInfo());
        registry.Operator.Grant(owner, space, member, SecurityPermission.Read);

        DeviceChallenge old = registry.DeviceProofs.IssueChallenge(device, space, null, SecurityPermission.Read);
        Assert.True(registry.Operator.RevokeGrant(owner, space, member));
        registry.Operator.Grant(owner, space, member, SecurityPermission.Read);

        Assert.Null(registry.DeviceProofs.Authenticate(old.ChallengeId, Sign(key, old)));
        DeviceChallenge current = registry.DeviceProofs.IssueChallenge(device, space, null, SecurityPermission.Read);
        Assert.NotNull(registry.DeviceProofs.Authenticate(current.ChallengeId, Sign(key, current)));
    }

    [Fact]
    public void AuthenticatedProofExpiresAndCannotBeTransferredToAnotherInstallation()
    {
        MutableClock clock = new(DateTimeOffset.Parse("2026-11-10T12:00:00Z"));
        using SecurityRegistry original = SecurityRegistry.Open(root, clock);
        Guid owner = original.Operator.CreatePrincipal();
        Guid space = original.Operator.CreateSpace(owner, SecurityProcessingClass.TrustedHost);
        using ECDsa key = NewKey();
        Guid device = original.Operator.EnrollDevice(owner, key.ExportSubjectPublicKeyInfo());
        DeviceChallenge challenge = original.DeviceProofs.IssueChallenge(device, space, null, SecurityPermission.Read);
        VerifiedDeviceProof? verified = original.DeviceProofs.Authenticate(challenge.ChallengeId, Sign(key, challenge));
        Assert.NotNull(verified);

        string anotherRoot = Path.Combine(TestPaths.Temp, "praxis-security-other-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(anotherRoot);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(anotherRoot,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        try
        {
            using SecurityRegistry another = SecurityRegistry.Open(anotherRoot, clock);
            Assert.NotEqual(original.InstallationId, another.InstallationId);
            Assert.False(another.DeviceProofs.IsCurrent(verified));
        }
        finally
        {
            Directory.Delete(anotherRoot, recursive: true);
        }

        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.False(original.DeviceProofs.IsCurrent(verified));
    }

    [Fact]
    public void InvalidPermissionAndNonP256KeysAreRejected()
    {
        using SecurityRegistry registry = SecurityRegistry.Open(root);
        Guid owner = registry.Operator.CreatePrincipal();
        Guid space = registry.Operator.CreateSpace(owner, SecurityProcessingClass.TrustedHost);
        using ECDsa good = NewKey();
        using ECDsa p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Guid device = registry.Operator.EnrollDevice(owner, good.ExportSubjectPublicKeyInfo());

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            registry.DeviceProofs.IssueChallenge(device, space, null, (SecurityPermission)3));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            registry.Operator.CreateSpace(owner, (SecurityProcessingClass)999));
        Assert.Throws<ArgumentException>(() =>
            registry.Operator.AssignSource(owner, space, Guid.Empty));
        Assert.Throws<ArgumentException>(() =>
            registry.Operator.EnrollDevice(owner, p384.ExportSubjectPublicKeyInfo()));
    }

    private static ECDsa NewKey() => ECDsa.Create(ECCurve.NamedCurves.nistP256);

    private static byte[] Sign(ECDsa key, DeviceChallenge challenge) =>
        key.SignData(challenge.Message, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(root,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class MutableClock(DateTimeOffset current) : TimeProvider
    {
        private DateTimeOffset now = current;

        public override DateTimeOffset GetUtcNow() => now;

        internal void Advance(TimeSpan elapsed) => now += elapsed;
    }
}
