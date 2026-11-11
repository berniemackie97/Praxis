using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Praxis.Domain.Security;
using Praxis.Infrastructure.Persistence.Security.Registry;
using Praxis.Infrastructure.Persistence.Sync;
using Praxis.Infrastructure.Security.Cryptography;
using Xunit;

namespace Praxis.SyncHost.Tests;

public sealed class PrivateSyncHostTests
{
    [Fact]
    public async Task TwoEnrolledDevicesExchangeEncryptedNotesWithDurableReceiptAndConflicts()
    {
        await using TestHost host = new();
        await host.StartAsync();
        using OpaqueSyncTransportClient first = host.ConnectFirst();
        using OpaqueSyncTransportClient second = host.ConnectSecond();
        Guid noteId = Guid.NewGuid();
        OpaqueNoteContent local = new("private/hello.md", "# First private edit");
        byte[] ciphertext = host.Encrypt(noteId, 1, OpaqueSyncAction.Upsert, local);
        OpaqueSyncMutation firstEdit = host.Mutation(host.FirstDevice, noteId,
            Guid.NewGuid(), 0, OpaqueSyncAction.Upsert, ciphertext);

        OpaqueSyncReceipt accepted = await first.SubmitAsync(firstEdit);
        Assert.Equal(OpaqueSyncStatus.Applied, accepted.Status);
        Assert.Equal(1, accepted.Sequence);
        Assert.Equal(accepted, await first.SubmitAsync(firstEdit));

        OpaqueSyncPage page = await second.PullAsync(
            host.Pull(host.SecondDevice, 0, 100));
        Assert.Equal(OpaqueSyncStatus.Ok, page.Status);
        Assert.Single(page.Changes);
        Assert.Equal(1, page.NextCursor);
        Assert.Equal(1, page.HeadSequence);
        Assert.Equal(ciphertext, page.Changes[0].Ciphertext);
        Assert.Equal(local, host.Decrypt(noteId, 1, OpaqueSyncAction.Upsert, ciphertext));

        OpaqueNoteContent edited = new("private/renamed.md", "# Second private edit");
        OpaqueSyncMutation secondEdit = host.Mutation(host.SecondDevice, noteId,
            Guid.NewGuid(), 1, OpaqueSyncAction.Upsert,
            host.Encrypt(noteId, 2, OpaqueSyncAction.Upsert, edited));
        Assert.Equal(2, (await second.SubmitAsync(secondEdit)).Sequence);

        OpaqueSyncMutation stale = host.Mutation(host.FirstDevice, noteId,
            Guid.NewGuid(), 1, OpaqueSyncAction.Upsert,
            host.Encrypt(noteId, 2, OpaqueSyncAction.Upsert, local));
        OpaqueSyncReceipt conflict = await first.SubmitAsync(stale);
        Assert.Equal(OpaqueSyncStatus.StaleRevision, conflict.Status);
        Assert.Equal(2, conflict.Sequence);
        Assert.Equal(2, conflict.Revision);

        OpaqueSyncMutation delete = host.Mutation(host.FirstDevice, noteId,
            Guid.NewGuid(), 2, OpaqueSyncAction.Delete, []);
        Assert.Equal(3, (await first.SubmitAsync(delete)).Sequence);
        OpaqueSyncPage after = await second.PullAsync(
            host.Pull(host.SecondDevice, 1, 100));
        Assert.Equal(new long[] { 2, 3 }, after.Changes.Select(c => c.Sequence));
        Assert.True(after.Changes[1].Deleted);
        Assert.Empty(after.Changes[1].Ciphertext);
        Assert.Equal(3, after.HeadSequence);
        Assert.Equal(3, after.NextCursor);

        Assert.True(host.Registry.Operator.RevokeDevice(host.SecondDevice));
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            second.PullAsync(host.Pull(host.SecondDevice, 0, 100)));
    }

    [Fact]
    public async Task NetworkRejectsUnsupportedVersionsInvalidCursorsAndReusedProofs()
    {
        await using TestHost host = new();
        await host.StartAsync();
        using OpaqueSyncTransportClient client = host.ConnectFirst();
        Guid noteId = Guid.NewGuid();
        OpaqueSyncMutation mutation = host.Mutation(host.FirstDevice, noteId,
            Guid.NewGuid(), 0, OpaqueSyncAction.Upsert,
            host.Encrypt(noteId, 1, OpaqueSyncAction.Upsert, new("a.md", "A")));

        Assert.Equal(OpaqueSyncStatus.UnsupportedProtocolVersion,
            (await client.SubmitAsync(mutation with { ProtocolVersion = 2 })).Status);
        Assert.Equal(OpaqueSyncStatus.InvalidRequest,
            (await client.SubmitAsync(mutation with
            {
                OperationId = Guid.NewGuid(),
                Ciphertext = new byte[2]
            })).Status);
        Assert.Equal(OpaqueSyncStatus.WrongCollectionEpoch,
            (await client.SubmitAsync(mutation with
            {
                OperationId = Guid.NewGuid(),
                CollectionEpoch = Guid.NewGuid()
            })).Status);
        Assert.Equal(OpaqueSyncStatus.InvalidCursor,
            (await client.PullAsync(host.Pull(host.FirstDevice, 1, 100))).Status);
        Assert.Equal(OpaqueSyncStatus.InvalidCursor,
            (await client.PullAsync(host.Pull(host.FirstDevice, 0, 101))).Status);
        Assert.Equal(0, (await client.PullAsync(host.Pull(host.FirstDevice, 0, 10))).HeadSequence);

        using HttpClient raw = host.RawFirst();
        using HttpResponseMessage challengeResponse = await raw.PostAsJsonAsync(
            "v1/sync/challenge",
            new SyncChallengeRequest(host.FirstDevice, host.Space, SecurityPermission.Write));
        challengeResponse.EnsureSuccessStatusCode();
        DeviceChallenge challenge = (await challengeResponse.Content.ReadFromJsonAsync<DeviceChallenge>())!;
        byte[] signature = host.SignFirst(challenge.Message);
        SyncSubmitRequest once = new(challenge.ChallengeId, signature, mutation);
        using HttpResponseMessage allowed = await raw.PostAsJsonAsync("v1/sync/submit", once);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        using HttpResponseMessage replay = await raw.PostAsJsonAsync("v1/sync/submit", once);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Equal(1, (await client.PullAsync(host.Pull(host.FirstDevice, 0, 100))).HeadSequence);
    }

    [Fact]
    public async Task AmbiguousAcknowledgmentReplaysIdenticalReceiptAfterHostRestart()
    {
        await using TestHost host = new();
        await host.StartAsync();
        Guid record = Guid.NewGuid();
        OpaqueSyncMutation mutation = host.Mutation(host.FirstDevice, record,
            Guid.NewGuid(), 0, OpaqueSyncAction.Upsert,
            host.Encrypt(record, 1, OpaqueSyncAction.Upsert,
                new OpaqueNoteContent("private/restart.md", "Survives restart")));
        OpaqueSyncReceipt committed;
        using (OpaqueSyncTransportClient client = host.ConnectFirst())
        {
            committed = await client.SubmitAsync(mutation);
        }

        await host.RestartAsync();
        using OpaqueSyncTransportClient retried = host.ConnectFirst();
        Assert.Equal(committed, await retried.SubmitAsync(mutation));
        OpaqueSyncPage page = await retried.PullAsync(
            host.Pull(host.FirstDevice, 0, 10));
        Assert.Single(page.Changes);
        Assert.Equal(committed.Sequence, page.HeadSequence);
        Assert.Equal(mutation.Ciphertext, page.Changes[0].Ciphertext);
    }

    private sealed class TestHost : IAsyncDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(),
            "praxis-sync-tls-" + Guid.NewGuid().ToString("N"));
        private readonly X509Certificate2 authority;
        private readonly X509Certificate2 serverCertificate;
        private readonly X509Certificate2 firstCertificate;
        private readonly X509Certificate2 secondCertificate;
        private readonly ECDsa firstSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly ECDsa secondSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly byte[] contentKey = ClientSpaceKeys.GenerateContentKey();
        private OpaqueSyncStore store;
        private WebApplication? app;
        private Uri? endpoint;

        public SecurityRegistry Registry { get; private set; }
        public Guid FirstDevice { get; }
        public Guid SecondDevice { get; }
        public Guid Space { get; }
        public Guid CollectionEpoch { get; }

        public TestHost()
        {
            string registryRoot = Path.Combine(root, "registry");
            string syncRoot = Path.Combine(root, "sync");
            PrivateDirectory(root);
            PrivateDirectory(registryRoot);
            PrivateDirectory(syncRoot);
            Registry = SecurityRegistry.Open(registryRoot);
            store = OpaqueSyncStore.Open(syncRoot);
            authority = IssueRoot();

            Guid owner = Registry.Operator.CreatePrincipal();
            FirstDevice = Enroll(owner, firstSigner);
            SecondDevice = Enroll(owner, secondSigner);
            Space = Registry.Operator.CreateSpace(owner, SecurityProcessingClass.ClientHeld);
            CollectionEpoch = Guid.NewGuid();
            store.Provision(Space, CollectionEpoch);

            VerifiedDeviceProof write = Prove(firstSigner, FirstDevice, SecurityPermission.Write);
            // The two real wrapping keys were registered at enrollment; use owner-signed
            // recipient pins independent of the registry's presented directory.
            IReadOnlyDictionary<Guid, byte[]> trusted = new Dictionary<Guid, byte[]>
            {
                [FirstDevice] = firstSigner.ExportSubjectPublicKeyInfo(),
                [SecondDevice] = secondSigner.ExportSubjectPublicKeyInfo()
            };
            SpaceKeyPublication publication = ClientSpaceKeys.Protect(
                Registry.InstallationId, Space, 1, FirstDevice, firstSigner,
                Registry.SpaceKeys.Recipients(write), trusted, contentKey,
                ClientSpaceKeys.GenerateRecoverySecret());
            Registry.SpaceKeys.Publish(write, publication);

            using ECDsa serverSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            serverCertificate = IssueLeaf(authority, serverSigner, isServer: true);
            firstCertificate = IssueLeaf(authority, firstSigner, isServer: false);
            secondCertificate = IssueLeaf(authority, secondSigner, isServer: false);
        }

        public async Task StartAsync()
        {
            app = PrivateSyncHost.Build(IPAddress.Loopback, 0,
                serverCertificate, authority, Registry, store);
            await app.StartAsync();
            endpoint = new Uri(app.Urls.Single().TrimEnd('/') + "/");
        }

        public async Task RestartAsync()
        {
            if (app is null)
            {
                throw new InvalidOperationException("The sync host is not running.");
            }

            await app.StopAsync();
            await app.DisposeAsync();
            app = null;
            store.Dispose();
            Registry.Dispose();
            Registry = SecurityRegistry.Open(Path.Combine(root, "registry"));
            store = OpaqueSyncStore.Open(Path.Combine(root, "sync"));
            await StartAsync();
        }

        public OpaqueSyncTransportClient ConnectFirst() =>
            OpaqueSyncTransportClient.Connect(endpoint!, firstCertificate, authority,
                FirstDevice, firstSigner);

        public OpaqueSyncTransportClient ConnectSecond() =>
            OpaqueSyncTransportClient.Connect(endpoint!, secondCertificate, authority,
                SecondDevice, secondSigner);

        public HttpClient RawFirst()
        {
            HttpClientHandler handler = new()
            {
                ClientCertificateOptions = ClientCertificateOption.Manual,
                ServerCertificateCustomValidationCallback = (_, cert, _, errors) =>
                    cert is not null &&
                    (errors & (System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch |
                               System.Net.Security.SslPolicyErrors.RemoteCertificateNotAvailable)) == 0 &&
                    PinnedCertificateChain.Verify(cert, authority,
                        PinnedCertificateChain.ServerAuthentication)
            };
            handler.ClientCertificates.Add(firstCertificate);
            return new HttpClient(handler) { BaseAddress = endpoint! };
        }

        public byte[] Encrypt(Guid record, long revision, OpaqueSyncAction action,
            OpaqueNoteContent note) =>
            ClientOpaqueNoteCipher.Encrypt(contentKey, Space, CollectionEpoch, record,
                revision, action, 1, note);

        public OpaqueNoteContent Decrypt(Guid record, long revision,
            OpaqueSyncAction action, byte[] ciphertext) =>
            ClientOpaqueNoteCipher.Decrypt(contentKey, Space, CollectionEpoch,
                record, revision, action, 1, ciphertext);

        public OpaqueSyncMutation Mutation(Guid device, Guid record, Guid operation,
            long expected, OpaqueSyncAction action, byte[] payload) =>
            new(1, Space, CollectionEpoch, device, operation, record,
                expected, action, 1, payload);

        public OpaqueSyncPull Pull(Guid device, long cursor, int limit) =>
            new(1, Space, CollectionEpoch, device, 1, cursor, limit);

        public byte[] SignFirst(byte[] message) => firstSigner.SignData(
            message, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        private VerifiedDeviceProof Prove(ECDsa signer, Guid device, SecurityPermission permission)
        {
            DeviceChallenge challenge = Registry.DeviceProofs.IssueChallenge(
                device, Space, null, permission);
            return Registry.DeviceProofs.Authenticate(challenge.ChallengeId,
                signer.SignData(challenge.Message, HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) ??
                throw new InvalidOperationException("The enrollment proof failed.");
        }

        private Guid Enroll(Guid owner, ECDsa key)
        {
            using RSA recipient = RSA.Create();
            recipient.KeySize = 3072;
            byte[] signing = key.ExportSubjectPublicKeyInfo();
            byte[] wrapping = recipient.ExportSubjectPublicKeyInfo();
            byte[] signature = key.SignData(
                DeviceWrappingKey.BindingMessage(signing, wrapping),
                HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return Registry.Operator.EnrollDevice(owner, signing, wrapping, signature);
        }

        private static X509Certificate2 IssueRoot()
        {
            using ECDsa signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            CertificateRequest request = new("CN=Praxis Test CA", signer, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(
                request.PublicKey, false));
            return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddDays(7));
        }

        private static X509Certificate2 IssueLeaf(X509Certificate2 root,
            ECDsa signer, bool isServer)
        {
            CertificateRequest request = new(isServer ? "CN=localhost" : "CN=Praxis Device",
                signer, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature, true));
            OidCollection usages = new()
            {
                new Oid(isServer ? PinnedCertificateChain.ServerAuthentication :
                    PinnedCertificateChain.ClientAuthentication)
            };
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, true));
            if (isServer)
            {
                SubjectAlternativeNameBuilder san = new();
                san.AddIpAddress(IPAddress.Loopback);
                san.AddDnsName("localhost");
                request.CertificateExtensions.Add(san.Build());
            }

            using X509Certificate2 issued = request.Create(root,
                DateTimeOffset.UtcNow.AddMinutes(-5),
                DateTimeOffset.UtcNow.AddHours(1), RandomNumberGenerator.GetBytes(16));
            return issued.CopyWithPrivateKey(signer);
        }

        private static void PrivateDirectory(string path)
        {
            Directory.CreateDirectory(path);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead |
                    UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (app is not null)
            {
                await app.StopAsync();
                await app.DisposeAsync();
            }

            firstCertificate.Dispose();
            secondCertificate.Dispose();
            serverCertificate.Dispose();
            authority.Dispose();
            firstSigner.Dispose();
            secondSigner.Dispose();
            store.Dispose();
            Registry.Dispose();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
