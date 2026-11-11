using System.Security.Cryptography;
using Praxis.Domain.Security;
using Praxis.Infrastructure.Persistence.Security.Registry;
using Praxis.Infrastructure.Persistence.Sync;
using Praxis.Infrastructure.Security.Cryptography;
using Praxis.Infrastructure.Tests.Support;
using Xunit;

namespace Praxis.Infrastructure.Tests.Sync;

public sealed class OpaqueSyncGatewayTests : IDisposable
{
    private readonly string root = Path.Combine(TestPaths.Temp,
        "praxis-sync-gate-" + Guid.NewGuid().ToString("N"));

    public OpaqueSyncGatewayTests()
    {
        Directory.CreateDirectory(root);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(root,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void CurrentSignedSpaceProofAndContentKeyAreRequiredBeforeDataAccess()
    {
        string identityRoot = Path.Combine(root, "registry");
        string syncRoot = Path.Combine(root, "sync");
        PrivateDirectory(identityRoot);
        PrivateDirectory(syncRoot);
        using SecurityRegistry registry = SecurityRegistry.Open(identityRoot);
        using OpaqueSyncStore store = OpaqueSyncStore.Open(syncRoot);
        Guid owner = registry.Operator.CreatePrincipal();
        using ECDsa signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using RSA recipient = RSA.Create();
        recipient.KeySize = 3072;
        byte[] signing = signer.ExportSubjectPublicKeyInfo();
        byte[] wrapping = recipient.ExportSubjectPublicKeyInfo();
        Guid device = registry.Operator.EnrollDevice(owner, signing, wrapping,
            Sign(signer, DeviceWrappingKey.BindingMessage(signing, wrapping)));
        Guid space = registry.Operator.CreateSpace(owner, SecurityProcessingClass.ClientHeld);
        Guid epoch = Guid.NewGuid();
        store.Provision(space, epoch);
        OpaqueSyncGateway gateway = new(registry, store);
        VerifiedCertificateIdentity session = new(owner, device, DateTimeOffset.UtcNow.AddMinutes(10));

        VerifiedDeviceProof write = Prove(registry, signer, device, space, SecurityPermission.Write);
        OpaqueSyncMutation operation = new(1, space, epoch, device, Guid.NewGuid(),
            Guid.NewGuid(), 0, OpaqueSyncAction.Upsert, 1,
            RandomNumberGenerator.GetBytes(48));
        Assert.Throws<UnauthorizedAccessException>(() =>
            gateway.Submit(session, write, operation));

        SpaceKeyPublication published = ClientSpaceKeys.Protect(
            registry.InstallationId, space, 1, device, signer,
            registry.SpaceKeys.Recipients(write),
            new Dictionary<Guid, byte[]> { [device] = signing },
            ClientSpaceKeys.GenerateContentKey(), ClientSpaceKeys.GenerateRecoverySecret());
        registry.SpaceKeys.Publish(write, published);
        Assert.Equal(OpaqueSyncStatus.Applied,
            gateway.Submit(session, write, operation).Status);
        Assert.Throws<UnauthorizedAccessException>(() =>
            gateway.Submit(session, write, operation with { DeviceId = Guid.NewGuid() }));
        Assert.Throws<UnauthorizedAccessException>(() =>
            gateway.Submit(new VerifiedCertificateIdentity(owner, Guid.NewGuid(),
                DateTimeOffset.UtcNow.AddMinutes(10)), write, operation));
        Assert.Throws<UnauthorizedAccessException>(() =>
            gateway.Submit(session, write, operation with { ContentEpoch = 2 }));
        Assert.Throws<UnauthorizedAccessException>(() =>
            gateway.Pull(session, write, new OpaqueSyncPull(1, space, epoch, device, 1, 0, 100)));

        VerifiedDeviceProof read = Prove(registry, signer, device, space, SecurityPermission.Read);
        OpaqueSyncPage page = gateway.Pull(session, read,
            new OpaqueSyncPull(1, space, epoch, device, 1, 0, 100));
        Assert.Single(page.Changes);
        Assert.Equal(operation.Ciphertext, page.Changes[0].Ciphertext);

        Assert.True(registry.Operator.RevokeDevice(device));
        Assert.Throws<UnauthorizedAccessException>(() =>
            gateway.Pull(session, read,
                new OpaqueSyncPull(1, space, epoch, device, 1, 0, 100)));
        Assert.Throws<UnauthorizedAccessException>(() =>
            gateway.Submit(session, write, operation));
    }

    private static VerifiedDeviceProof Prove(SecurityRegistry registry, ECDsa signer,
        Guid device, Guid space, SecurityPermission permission)
    {
        DeviceChallenge challenge = registry.DeviceProofs.IssueChallenge(
            device, space, null, permission);
        return registry.DeviceProofs.Authenticate(challenge.ChallengeId,
            Sign(signer, challenge.Message)) ??
            throw new InvalidOperationException("The device proof was rejected.");
    }

    private static byte[] Sign(ECDsa signer, byte[] message) =>
        signer.SignData(message, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    private static void PrivateDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
