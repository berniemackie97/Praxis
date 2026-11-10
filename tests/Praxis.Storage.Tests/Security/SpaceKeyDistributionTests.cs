using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Praxis.Storage.Security;
using Praxis.Storage.Tests.Support;
using Xunit;

namespace Praxis.Storage.Tests.Security;

public sealed class SpaceKeyDistributionTests : IDisposable
{
    private readonly string root = Path.Combine(TestPaths.Temp,
        "praxis-space-keys-" + Guid.NewGuid().ToString("N"));

    public SpaceKeyDistributionTests()
    {
        Directory.CreateDirectory(root);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void TwoDevicesReceiveDistinctEncryptedKeysAndOwnerRecoversOffline()
    {
        using SecurityRegistry registry = SecurityRegistry.Open(root);
        Guid owner = registry.Operator.CreatePrincipal();
        Guid member = registry.Operator.CreatePrincipal();
        using ECDsa signer = NewSigner();
        using ECDsa memberSigner = NewSigner();
        using RSA ownerRsa = NewRecipient();
        using RSA memberRsa = NewRecipient();
        Guid ownerDevice = Enroll(registry, owner, signer, ownerRsa);
        Guid memberDevice = Enroll(registry, member, memberSigner, memberRsa);
        Guid space = registry.Operator.CreateSpace(owner, SecurityProcessingClass.ClientHeld);
        registry.Operator.Grant(owner, space, member, SecurityPermission.Read);

        VerifiedDeviceProof ownerWrite = Prove(registry, signer, ownerDevice, space, SecurityPermission.Write);
        IReadOnlyList<RecipientWrappingKey> recipients = registry.SpaceKeys.Recipients(ownerWrite);
        Assert.Equal(2, recipients.Count);
        byte[] key = ClientSpaceKeys.GenerateContentKey();
        byte[] secret = ClientSpaceKeys.GenerateRecoverySecret();
        SpaceKeyPublication package = ClientSpaceKeys.Protect(
            registry.InstallationId, space, 1, ownerDevice, signer, recipients, key, secret);

        registry.SpaceKeys.Publish(ownerWrite, package);
        AvailableSpaceKey? ownerView = registry.SpaceKeys.Download(
            Prove(registry, signer, ownerDevice, space, SecurityPermission.Read));
        AvailableSpaceKey? memberView = registry.SpaceKeys.Download(
            Prove(registry, memberSigner, memberDevice, space, SecurityPermission.Read));

        Assert.NotNull(ownerView);
        Assert.NotNull(memberView);
        Assert.NotNull(ownerView.OwnerRecovery);
        Assert.Null(memberView.OwnerRecovery);
        Assert.Single(registry.SpaceKeys.Recipients(ownerWrite), x => x.DeviceId == memberDevice);
        Assert.Equal(key, ClientSpaceKeys.UnlockForDevice(
            registry.InstallationId, ownerView, ownerRsa, signer, 1));
        Assert.Equal(key, ClientSpaceKeys.UnlockForDevice(
            registry.InstallationId, memberView, memberRsa, signer, 1));
        Assert.Equal(key, ClientSpaceKeys.UnlockWithRecovery(
            registry.InstallationId, ownerView, secret, signer, 1));
        Assert.NotEqual(Convert.ToBase64String(ownerView.DeviceKey.WrappedKey),
            Convert.ToBase64String(memberView.DeviceKey.WrappedKey));

        using SqliteConnection inspected = new($"Data Source={Path.Combine(root, "identity.sqlite")};Pooling=False");
        inspected.Open();
        using SqliteCommand inspect = inspected.CreateCommand();
        inspect.CommandText = "SELECT commitment, recovery_ciphertext FROM space_key_epochs WHERE space_id=$space";
        inspect.Parameters.AddWithValue("$space", space.ToString("N"));
        using SqliteDataReader reader = inspect.ExecuteReader();
        Assert.True(reader.Read());
        Assert.NotEqual(Convert.ToBase64String(key), Convert.ToBase64String((byte[])reader.GetValue(0)));
        Assert.NotEqual(Convert.ToBase64String(key), Convert.ToBase64String((byte[])reader.GetValue(1)));
    }

    [Fact]
    public void InvalidBindingAndIncorrectRecipientPrivateKeyAreRejected()
    {
        using SecurityRegistry registry = SecurityRegistry.Open(root);
        Guid owner = registry.Operator.CreatePrincipal();
        using ECDsa signer = NewSigner();
        using ECDsa impostor = NewSigner();
        using RSA ownerRsa = NewRecipient();
        using RSA impostorRsa = NewRecipient();
        byte[] signing = signer.ExportSubjectPublicKeyInfo();
        byte[] wrapping = ownerRsa.ExportSubjectPublicKeyInfo();
        byte[] binding = Sign(signer, DeviceWrappingKey.BindingMessage(signing, wrapping));
        Assert.False(DeviceWrappingKey.Verify(signing, impostorRsa.ExportSubjectPublicKeyInfo(), binding));
        Assert.Throws<ArgumentException>(() =>
            registry.Operator.EnrollDevice(owner, signing, wrapping,
                Sign(impostor, DeviceWrappingKey.BindingMessage(signing, wrapping))));

        Guid device = registry.Operator.EnrollDevice(owner, signing, wrapping, binding);
        Guid space = registry.Operator.CreateSpace(owner, SecurityProcessingClass.TrustedHost);
        VerifiedDeviceProof ownerWrite = Prove(registry, signer, device, space, SecurityPermission.Write);
        byte[] key = ClientSpaceKeys.GenerateContentKey();
        byte[] secret = ClientSpaceKeys.GenerateRecoverySecret();
        SpaceKeyPublication package = ClientSpaceKeys.Protect(
            registry.InstallationId, space, 1, device, signer,
            registry.SpaceKeys.Recipients(ownerWrite), key, secret);
        registry.SpaceKeys.Publish(ownerWrite, package);
        AvailableSpaceKey? available = registry.SpaceKeys.Download(
            Prove(registry, signer, device, space, SecurityPermission.Read));
        Assert.NotNull(available);
        Assert.Throws<CryptographicException>(() =>
            ClientSpaceKeys.UnlockForDevice(registry.InstallationId, available, impostorRsa, signer, 1));
        Assert.Throws<CryptographicException>(() =>
            ClientSpaceKeys.UnlockWithRecovery(registry.InstallationId, available,
                ClientSpaceKeys.GenerateRecoverySecret(), signer, 1));
    }

    [Fact]
    public void TamperedEnvelopeAndWrongSignerAreRejected()
    {
        using SecurityRegistry registry = SecurityRegistry.Open(root);
        Guid owner = registry.Operator.CreatePrincipal();
        using ECDsa signer = NewSigner();
        using ECDsa impostor = NewSigner();
        using RSA rsa = NewRecipient();
        Guid device = Enroll(registry, owner, signer, rsa);
        Guid space = registry.Operator.CreateSpace(owner, SecurityProcessingClass.ClientHeld);
        VerifiedDeviceProof write = Prove(registry, signer, device, space, SecurityPermission.Write);
        byte[] key = ClientSpaceKeys.GenerateContentKey();
        byte[] recovery = ClientSpaceKeys.GenerateRecoverySecret();
        SpaceKeyPublication original = ClientSpaceKeys.Protect(registry.InstallationId,
            space, 1, device, signer, registry.SpaceKeys.Recipients(write), key, recovery);
        byte[] modifiedCiphertext = original.Recipients[0].WrappedKey.ToArray();
        modifiedCiphertext[17] ^= 1;
        SpaceKeyPublication tampered = original with
        {
            Recipients = [original.Recipients[0] with { WrappedKey = modifiedCiphertext }]
        };
        Assert.Throws<UnauthorizedAccessException>(() => registry.SpaceKeys.Publish(write, tampered));
        Assert.Throws<ArgumentException>(() =>
            registry.SpaceKeys.Publish(write, original with { SignerDeviceId = Guid.NewGuid() }));
        RecoveryKeyEnvelope tamperedRecovery = original.Recovery with
        {
            CiphertextAndTag = new byte[48]
        };
        Assert.Throws<UnauthorizedAccessException>(() =>
            registry.SpaceKeys.Publish(write, original with { Recovery = tamperedRecovery }));

        registry.SpaceKeys.Publish(write, original);
        AvailableSpaceKey? saved = registry.SpaceKeys.Download(
            Prove(registry, signer, device, space, SecurityPermission.Read));
        Assert.NotNull(saved);
        Assert.Throws<CryptographicException>(() =>
            ClientSpaceKeys.UnlockForDevice(registry.InstallationId, saved, rsa, impostor, 1));
        Assert.Throws<CryptographicException>(() =>
            ClientSpaceKeys.UnlockForDevice(registry.InstallationId, saved with
            {
                KeyCommitment = new byte[32]
            }, rsa, signer, 1));
        Assert.Throws<CryptographicException>(() =>
            ClientSpaceKeys.UnlockWithRecovery(registry.InstallationId, saved with
            {
                OwnerRecovery = saved.OwnerRecovery! with { Nonce = new byte[12] }
            }, recovery, signer, 1));
    }

    [Fact]
    public void RevokedMemberCannotReceiveCurrentKeyUntilOwnerRotatesAndOldKeyIsNotErased()
    {
        using SecurityRegistry registry = SecurityRegistry.Open(root);
        Guid owner = registry.Operator.CreatePrincipal();
        Guid member = registry.Operator.CreatePrincipal();
        using ECDsa signer = NewSigner();
        using ECDsa memberSigner = NewSigner();
        using RSA ownerRsa = NewRecipient();
        using RSA memberRsa = NewRecipient();
        Guid ownerDevice = Enroll(registry, owner, signer, ownerRsa);
        Guid memberDevice = Enroll(registry, member, memberSigner, memberRsa);
        Guid space = registry.Operator.CreateSpace(owner, SecurityProcessingClass.ClientHeld);
        registry.Operator.Grant(owner, space, member, SecurityPermission.Read);

        VerifiedDeviceProof write = Prove(registry, signer, ownerDevice, space, SecurityPermission.Write);
        byte[] oldKey = ClientSpaceKeys.GenerateContentKey();
        SpaceKeyPublication first = ClientSpaceKeys.Protect(registry.InstallationId, space, 1,
            ownerDevice, signer, registry.SpaceKeys.Recipients(write),
            oldKey, ClientSpaceKeys.GenerateRecoverySecret());
        registry.SpaceKeys.Publish(write, first);
        VerifiedDeviceProof readMember = Prove(registry, memberSigner, memberDevice, space, SecurityPermission.Read);
        AvailableSpaceKey? historical = registry.SpaceKeys.Download(readMember);
        Assert.NotNull(historical);
        Assert.True(registry.Operator.RevokeGrant(owner, space, member));
        Assert.False(registry.DeviceProofs.IsCurrent(readMember));
        Assert.Throws<UnauthorizedAccessException>(() => registry.SpaceKeys.Download(readMember));
        Assert.Null(registry.SpaceKeys.Download(Prove(registry, signer, ownerDevice, space, SecurityPermission.Read)));

        byte[] newKey = ClientSpaceKeys.GenerateContentKey();
        SpaceKeyPublication rotated = ClientSpaceKeys.Protect(registry.InstallationId, space, 2,
            ownerDevice, signer, registry.SpaceKeys.Recipients(write),
            newKey, ClientSpaceKeys.GenerateRecoverySecret());
        registry.SpaceKeys.Publish(write, rotated);

        AvailableSpaceKey? current = registry.SpaceKeys.Download(
            Prove(registry, signer, ownerDevice, space, SecurityPermission.Read));
        Assert.NotNull(current);
        Assert.Equal(2, current.Epoch);
        Assert.Equal(newKey, ClientSpaceKeys.UnlockForDevice(
            registry.InstallationId, current, ownerRsa, signer, 2));
        Assert.Throws<CryptographicException>(() =>
            ClientSpaceKeys.UnlockForDevice(registry.InstallationId, historical, memberRsa, signer, 2));
        Assert.Equal(oldKey, ClientSpaceKeys.UnlockForDevice(
            registry.InstallationId, historical, memberRsa, signer, 1));
        Assert.Throws<UnauthorizedAccessException>(() =>
            registry.SpaceKeys.Download(Prove(registry, memberSigner, memberDevice, space, SecurityPermission.Read)));
    }

    [Fact]
    public void MissingRecipientAndDuplicateEpochCannotPublishPartialState()
    {
        using SecurityRegistry registry = SecurityRegistry.Open(root);
        Guid owner = registry.Operator.CreatePrincipal();
        Guid member = registry.Operator.CreatePrincipal();
        using ECDsa signer = NewSigner();
        using ECDsa memberSigner = NewSigner();
        using RSA ownerRsa = NewRecipient();
        using RSA memberRsa = NewRecipient();
        Guid ownerDevice = Enroll(registry, owner, signer, ownerRsa);
        Enroll(registry, member, memberSigner, memberRsa);
        Guid space = registry.Operator.CreateSpace(owner, SecurityProcessingClass.TrustedHost);
        registry.Operator.Grant(owner, space, member, SecurityPermission.Read);
        VerifiedDeviceProof write = Prove(registry, signer, ownerDevice, space, SecurityPermission.Write);
        IReadOnlyList<RecipientWrappingKey> all = registry.SpaceKeys.Recipients(write);

        SpaceKeyPublication missing = ClientSpaceKeys.Protect(
            registry.InstallationId, space, 1, ownerDevice, signer,
            all.Where(x => x.DeviceId == ownerDevice).ToArray(),
            ClientSpaceKeys.GenerateContentKey(), ClientSpaceKeys.GenerateRecoverySecret());
        Assert.Throws<UnauthorizedAccessException>(() => registry.SpaceKeys.Publish(write, missing));
        Assert.Null(registry.SpaceKeys.Download(
            Prove(registry, signer, ownerDevice, space, SecurityPermission.Read)));

        SpaceKeyPublication first = ClientSpaceKeys.Protect(
            registry.InstallationId, space, 1, ownerDevice, signer, all,
            ClientSpaceKeys.GenerateContentKey(), ClientSpaceKeys.GenerateRecoverySecret());
        registry.SpaceKeys.Publish(write, first);
        Assert.Throws<InvalidOperationException>(() => registry.SpaceKeys.Publish(write, first));
        SpaceKeyPublication skipped = ClientSpaceKeys.Protect(
            registry.InstallationId, space, 3, ownerDevice, signer, all,
            ClientSpaceKeys.GenerateContentKey(), ClientSpaceKeys.GenerateRecoverySecret());
        Assert.Throws<InvalidOperationException>(() => registry.SpaceKeys.Publish(write, skipped));
        Assert.Equal(1, registry.SpaceKeys.Download(
            Prove(registry, signer, ownerDevice, space, SecurityPermission.Read))!.Epoch);
    }

    [Fact]
    public void NewEnrolledDeviceForcesRotationAndBothDevicesCanRecoverLatestEpoch()
    {
        using SecurityRegistry registry = SecurityRegistry.Open(root);
        Guid owner = registry.Operator.CreatePrincipal();
        using ECDsa firstSigner = NewSigner();
        using ECDsa secondSigner = NewSigner();
        using RSA firstRsa = NewRecipient();
        using RSA secondRsa = NewRecipient();
        Guid firstDevice = Enroll(registry, owner, firstSigner, firstRsa);
        Guid space = registry.Operator.CreateSpace(owner, SecurityProcessingClass.ClientHeld);
        VerifiedDeviceProof firstWrite = Prove(registry, firstSigner, firstDevice, space, SecurityPermission.Write);
        SpaceKeyPublication first = ClientSpaceKeys.Protect(
            registry.InstallationId, space, 1, firstDevice, firstSigner,
            registry.SpaceKeys.Recipients(firstWrite), ClientSpaceKeys.GenerateContentKey(),
            ClientSpaceKeys.GenerateRecoverySecret());
        registry.SpaceKeys.Publish(firstWrite, first);
        Guid secondDevice = Enroll(registry, owner, secondSigner, secondRsa);
        Assert.Null(registry.SpaceKeys.Download(
            Prove(registry, firstSigner, firstDevice, space, SecurityPermission.Read)));

        byte[] nextKey = ClientSpaceKeys.GenerateContentKey();
        SpaceKeyPublication second = ClientSpaceKeys.Protect(
            registry.InstallationId, space, 2, firstDevice, firstSigner,
            registry.SpaceKeys.Recipients(firstWrite), nextKey,
            ClientSpaceKeys.GenerateRecoverySecret());
        registry.SpaceKeys.Publish(firstWrite, second);
        AvailableSpaceKey? newDevice = registry.SpaceKeys.Download(
            Prove(registry, secondSigner, secondDevice, space, SecurityPermission.Read));
        Assert.NotNull(newDevice);
        Assert.Equal(nextKey, ClientSpaceKeys.UnlockForDevice(
            registry.InstallationId, newDevice, secondRsa, firstSigner, 2));
    }

    [Fact]
    public void OwnerCanUnlockPreviouslySavedRecoveryEnvelopeAfterAllDevicesRevoked()
    {
        Guid installation, space;
        AvailableSpaceKey? offlineRecovery;
        byte[] key = ClientSpaceKeys.GenerateContentKey();
        byte[] secret = ClientSpaceKeys.GenerateRecoverySecret();
        using ECDsa signer = NewSigner();
        using RSA rsa = NewRecipient();

        using (SecurityRegistry registry = SecurityRegistry.Open(root))
        {
            installation = registry.InstallationId;
            Guid owner = registry.Operator.CreatePrincipal();
            Guid device = Enroll(registry, owner, signer, rsa);
            space = registry.Operator.CreateSpace(owner, SecurityProcessingClass.ClientHeld);
            VerifiedDeviceProof write = Prove(registry, signer, device, space, SecurityPermission.Write);
            SpaceKeyPublication publication = ClientSpaceKeys.Protect(
                installation, space, 1, device, signer, registry.SpaceKeys.Recipients(write), key, secret);
            registry.SpaceKeys.Publish(write, publication);
            offlineRecovery = registry.SpaceKeys.Download(
                Prove(registry, signer, device, space, SecurityPermission.Read));
            Assert.NotNull(offlineRecovery);
            Assert.True(registry.Operator.RevokeDevice(device));
        }

        using (SecurityRegistry reopened = SecurityRegistry.Open(root))
        {
            Assert.Throws<UnauthorizedAccessException>(() =>
                Prove(reopened, signer, offlineRecovery!.DeviceKey.DeviceId,
                    space, SecurityPermission.Read));
        }

        Assert.Equal(key, ClientSpaceKeys.UnlockWithRecovery(
            installation, offlineRecovery!, secret, signer, 1));
    }

    [Fact]
    public void LegacySigningOnlyDeviceCannotSilentlyReceiveEncryptedKey()
    {
        using SecurityRegistry registry = SecurityRegistry.Open(root);
        Guid owner = registry.Operator.CreatePrincipal();
        using ECDsa signer = NewSigner();
        using RSA rsa = NewRecipient();
        Guid device = registry.Operator.EnrollDevice(owner, signer.ExportSubjectPublicKeyInfo());
        Guid space = registry.Operator.CreateSpace(owner, SecurityProcessingClass.ClientHeld);
        VerifiedDeviceProof write = Prove(registry, signer, device, space, SecurityPermission.Write);
        Assert.Throws<InvalidOperationException>(() => registry.SpaceKeys.Recipients(write));
        byte[] signing = signer.ExportSubjectPublicKeyInfo();
        byte[] wrapping = rsa.ExportSubjectPublicKeyInfo();
        registry.Operator.RegisterWrappingKey(device, wrapping,
            Sign(signer, DeviceWrappingKey.BindingMessage(signing, wrapping)));
        Assert.Single(registry.SpaceKeys.Recipients(write));
        Assert.Throws<InvalidOperationException>(() =>
            registry.Operator.RegisterWrappingKey(device, wrapping,
                Sign(signer, DeviceWrappingKey.BindingMessage(signing, wrapping))));
    }

    [Fact]
    public void MemberCannotEnumerateOrPublishOtherMembersKeyEnvelopes()
    {
        using SecurityRegistry registry = SecurityRegistry.Open(root);
        Guid owner = registry.Operator.CreatePrincipal();
        Guid member = registry.Operator.CreatePrincipal();
        using ECDsa signer = NewSigner();
        using ECDsa memberSigner = NewSigner();
        using RSA rsa = NewRecipient();
        using RSA memberRsa = NewRecipient();
        Guid ownerDevice = Enroll(registry, owner, signer, rsa);
        Guid memberDevice = Enroll(registry, member, memberSigner, memberRsa);
        Guid space = registry.Operator.CreateSpace(owner, SecurityProcessingClass.ClientHeld);
        registry.Operator.Grant(owner, space, member, SecurityPermission.Read | SecurityPermission.Write);
        VerifiedDeviceProof memberWrite = Prove(registry, memberSigner, memberDevice, space, SecurityPermission.Write);
        Assert.Throws<UnauthorizedAccessException>(() => registry.SpaceKeys.Recipients(memberWrite));

        VerifiedDeviceProof ownerWrite = Prove(registry, signer, ownerDevice, space, SecurityPermission.Write);
        SpaceKeyPublication pub = ClientSpaceKeys.Protect(registry.InstallationId, space, 1,
            ownerDevice, signer, registry.SpaceKeys.Recipients(ownerWrite),
            ClientSpaceKeys.GenerateContentKey(), ClientSpaceKeys.GenerateRecoverySecret());
        Assert.Throws<ArgumentException>(() => registry.SpaceKeys.Publish(memberWrite, pub));
        registry.SpaceKeys.Publish(ownerWrite, pub);
        AvailableSpaceKey? memberView = registry.SpaceKeys.Download(
            Prove(registry, memberSigner, memberDevice, space, SecurityPermission.Read));
        Assert.NotNull(memberView);
        Assert.Null(memberView.OwnerRecovery);
        Assert.Equal(memberDevice, memberView.DeviceKey.DeviceId);
    }

    private static ECDsa NewSigner() => ECDsa.Create(ECCurve.NamedCurves.nistP256);

    private static RSA NewRecipient()
    {
        RSA rsa = RSA.Create();
        rsa.KeySize = 3072;
        return rsa;
    }

    private static Guid Enroll(SecurityRegistry registry, Guid principal, ECDsa signer, RSA recipient)
    {
        byte[] signingKey = signer.ExportSubjectPublicKeyInfo();
        byte[] wrappingKey = recipient.ExportSubjectPublicKeyInfo();
        return registry.Operator.EnrollDevice(principal, signingKey, wrappingKey,
            Sign(signer, DeviceWrappingKey.BindingMessage(signingKey, wrappingKey)));
    }

    private static VerifiedDeviceProof Prove(SecurityRegistry registry, ECDsa signer,
        Guid deviceId, Guid spaceId, SecurityPermission permission)
    {
        DeviceChallenge challenge = registry.DeviceProofs.IssueChallenge(
            deviceId, spaceId, null, permission);
        return registry.DeviceProofs.Authenticate(challenge.ChallengeId, Sign(signer, challenge.Message)) ??
            throw new UnauthorizedAccessException("The enrolled device proof failed.");
    }

    private static byte[] Sign(ECDsa signer, byte[] message) =>
        signer.SignData(message, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
