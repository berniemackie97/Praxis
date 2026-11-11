using System.Net;
using System.Net.Http.Json;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Praxis.Domain.Security;
using Praxis.Infrastructure.Persistence.Security.Registry;
using Praxis.Infrastructure.Persistence.Sync;
using Praxis.Infrastructure.Security.Cryptography;
using Praxis.Notes.Journal;
using Praxis.Notes.Synchronization;
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
        using HttpResponseMessage impersonation = await raw.PostAsJsonAsync(
            "v1/sync/challenge",
            new SyncChallengeRequest(host.SecondDevice, host.Space, SecurityPermission.Read));
        Assert.Equal(HttpStatusCode.Unauthorized, impersonation.StatusCode);

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
    public async Task NegotiatedProtocolIsExplicitAndUnsupportedVersionIsRejected()
    {
        await using TestHost host = new();
        await host.StartAsync();

        using HttpClient verified = host.RawFirst();
        string[] negotiated = (await verified.GetStringAsync("__test/tls-protocol")).Split('|');
        Assert.Equal(2, negotiated.Length);
        Assert.Equal(host.ExpectedProtocol.ToString(), negotiated[0]);
        Assert.True(negotiated[1].Contains("GCM", StringComparison.Ordinal) ||
            negotiated[1].Contains("CHACHA20_POLY1305", StringComparison.Ordinal));
        if (host.ExpectedProtocol == SslProtocols.Tls12)
        {
            Assert.Contains("ECDHE", negotiated[1]);
        }

        SslProtocols unsupported = host.ExpectedProtocol == SslProtocols.Tls12
            ? SslProtocols.Tls13 : SslProtocols.Tls12;
        using HttpClient rejected = host.RawFirst(unsupported);
        await Assert.ThrowsAsync<HttpRequestException>(() => rejected.PostAsJsonAsync(
            "v1/sync/challenge",
            new SyncChallengeRequest(host.FirstDevice, host.Space, SecurityPermission.Read)));
    }

    [Fact]
    public async Task MissingOrUntrustedClientCertificateCannotObtainChallenges()
    {
        await using TestHost host = new();
        await host.StartAsync();
        SyncChallengeRequest request = new(
            host.FirstDevice, host.Space, SecurityPermission.Read);

        using HttpClient missing = host.RawFirst(includeCertificate: false);
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            missing.PostAsJsonAsync("v1/sync/challenge", request));

        using X509Certificate2 outsider = host.UntrustedCertificate();
        using HttpClient untrusted = host.RawFirst(certificate: outsider);
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            untrusted.PostAsJsonAsync("v1/sync/challenge", request));

        using HttpClient trusted = host.RawFirst();
        using HttpResponseMessage accepted = await trusted.PostAsJsonAsync(
            "v1/sync/challenge", request);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    [Fact]
    public async Task ProductionListenerCannotSilentlyDowngradeOnMacOs()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        await using TestHost host = new();
        host.VerifyProductionListenerIsUnsupported();
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

    [Fact]
    public async Task TwoPersistedNoteJournalsReconcileConflictsAndAuthenticatedTombstonesOverTls()
    {
        await using TestHost host = new();
        await host.StartAsync();
        using OpaqueSyncTransportClient firstClient = host.ConnectFirst();
        using OpaqueSyncTransportClient secondClient = host.ConnectSecond();
        using OfflineNoteJournal first = OfflineNoteJournal.Open(
            host.NewClientDirectory("journal-first"), host.CollectionEpoch, host.FirstDevice);
        using OfflineNoteJournal second = OfflineNoteJournal.Open(
            host.NewClientDirectory("journal-second"), host.CollectionEpoch, host.SecondDevice);
        using AuthenticatedNoteExchange firstExchange = host.Exchange(first, firstClient, "outbox-first");
        using AuthenticatedNoteExchange secondExchange = host.Exchange(second, secondClient, "outbox-second");
        OfflineNoteSynchronizer firstSync = new(first, host.Space, firstExchange);
        OfflineNoteSynchronizer secondSync = new(second, host.Space, secondExchange);

        Guid recordId = Guid.NewGuid();
        first.Queue(recordId, LocalNoteAction.Upsert, "notes/a.md", "original");
        Assert.Equal(NoteSyncState.UpToDate, (await firstSync.ReconcileAsync()).State);
        Assert.Equal(NoteSyncState.UpToDate, (await secondSync.ReconcileAsync()).State);
        Assert.Equal(first.Records.ToArray(), second.Records.ToArray());

        PendingNoteEdit losing = first.Queue(recordId, LocalNoteAction.Upsert,
            "notes/a.md", "first offline contribution");
        second.Queue(recordId, LocalNoteAction.Upsert, "notes/renamed.md", "second accepted");
        Assert.Equal(NoteSyncState.UpToDate, (await secondSync.ReconcileAsync()).State);
        await firstSync.ReconcileAsync();
        RejectedNoteEdit conflict = Assert.Single(first.Conflicts);
        Assert.Equal(losing.OperationId, conflict.Edit.OperationId);
        Assert.Equal("first offline contribution", Assert.Single(first.Pending).Markdown);
        Assert.Equal("second accepted", conflict.Current?.Markdown);
        Assert.Equal(2, Assert.Single(first.Records).Revision);

        first.Resolve(losing.OperationId, LocalNoteAction.Upsert,
            "notes/renamed.md", "resolved contribution");
        Assert.Equal(NoteSyncState.UpToDate, (await firstSync.ReconcileAsync()).State);
        Assert.Equal(NoteSyncState.UpToDate, (await secondSync.ReconcileAsync()).State);
        Assert.Equal(first.Records.ToArray(), second.Records.ToArray());
        Assert.Equal(3, Assert.Single(second.Records).Revision);

        first.Queue(recordId, LocalNoteAction.Delete);
        Assert.Equal(NoteSyncState.UpToDate, (await firstSync.ReconcileAsync()).State);
        Assert.Equal(NoteSyncState.UpToDate, (await secondSync.ReconcileAsync()).State);
        Assert.True(Assert.Single(second.Records).Deleted);
        Assert.Equal("resolved contribution", Assert.Single(second.Records).Markdown);

        second.Queue(recordId, LocalNoteAction.Restore, "notes/restored.md", "restored body");
        Assert.Equal(NoteSyncState.UpToDate, (await secondSync.ReconcileAsync()).State);
        Assert.Equal(NoteSyncState.UpToDate, (await firstSync.ReconcileAsync()).State);
        Assert.Equal(first.Records.ToArray(), second.Records.ToArray());
        Assert.Equal(5, Assert.Single(first.Records).Revision);
        Assert.False(Assert.Single(first.Records).Deleted);
        Assert.Empty(first.Pending);
        Assert.Empty(second.Pending);
    }

    [Fact]
    public async Task LostAckAndHostRestartReplayIdenticalEncryptedBytesFromDisk()
    {
        await using TestHost host = new();
        await host.StartAsync();
        string journalRoot = host.NewClientDirectory("crash-journal");
        string outboxRoot = host.NewClientDirectory("crash-outbox");
        Guid recordId = Guid.NewGuid();
        Guid operationId;
        using (OfflineNoteJournal journal = OfflineNoteJournal.Open(
                   journalRoot, host.CollectionEpoch, host.FirstDevice))
        using (OpaqueSyncTransportClient firstClient = host.ConnectFirst())
        using (AuthenticatedNoteExchange exchange = host.Exchange(journal, firstClient, outboxRoot))
        {
            PendingNoteEdit edit = journal.Queue(recordId, LocalNoteAction.Upsert,
                "notes/crash.md", "private crash recovery");
            operationId = edit.OperationId;
            NoteSubmitResult accepted = await exchange.SubmitAsync(
                new NoteSubmitRequest(1, host.Space, host.CollectionEpoch,
                    host.FirstDevice, edit), CancellationToken.None);
            Assert.Equal(NoteSubmitStatus.Applied, accepted.Status);
            Assert.Equal(1, accepted.Sequence);
            Assert.Single(journal.Pending);
            Assert.True(File.Exists(Path.Combine(outboxRoot, operationId.ToString("N") + ".json")));
        }

        await host.RestartAsync();
        using (OfflineNoteJournal journal = OfflineNoteJournal.Open(
                   journalRoot, host.CollectionEpoch, host.FirstDevice))
        using (OpaqueSyncTransportClient reconnected = host.ConnectFirst())
        using (AuthenticatedNoteExchange exchange = host.Exchange(journal, reconnected, outboxRoot))
        {
            OfflineNoteSynchronizer synchronizer = new(journal, host.Space, exchange);
            Assert.Equal(operationId, Assert.Single(journal.Pending).OperationId);
            Assert.Equal(NoteSyncState.UpToDate, (await synchronizer.ReconcileAsync()).State);
            Assert.Equal(1, Assert.Single(journal.Records).Revision);
            Assert.Equal(1, journal.Cursor);
            Assert.Empty(journal.Pending);
            Assert.Empty(journal.AcknowledgedChanges);
            Assert.Empty(journal.Conflicts);
        }

        using OpaqueSyncTransportClient read = host.ConnectSecond();
        Assert.Single((await read.PullAsync(host.Pull(host.SecondDevice, 0, 100))).Changes);
    }

    [Fact]
    public async Task ChangedLocalKeyCannotRewriteAnAlreadySealedOperation()
    {
        await using TestHost host = new();
        await host.StartAsync();
        string journalRoot = host.NewClientDirectory("key-journal");
        string outboxRoot = host.NewClientDirectory("key-outbox");
        using (OfflineNoteJournal journal = OfflineNoteJournal.Open(
                   journalRoot, host.CollectionEpoch, host.FirstDevice))
        using (OpaqueSyncTransportClient client = host.ConnectFirst())
        using (AuthenticatedNoteExchange exchange = host.Exchange(journal, client, outboxRoot))
        {
            PendingNoteEdit pending = journal.Queue(Guid.NewGuid(),
                LocalNoteAction.Upsert, "notes/secret.md", "never change sealed bytes");
            Assert.Equal(NoteSubmitStatus.Applied,
                (await exchange.SubmitAsync(new NoteSubmitRequest(1, host.Space,
                    host.CollectionEpoch, host.FirstDevice, pending),
                    CancellationToken.None)).Status);
        }

        using OfflineNoteJournal reopened = OfflineNoteJournal.Open(
            journalRoot, host.CollectionEpoch, host.FirstDevice);
        using OpaqueSyncTransportClient retry = host.ConnectFirst();
        using AuthenticatedNoteExchange wrongKey = new(reopened, host.Space, 1,
            ClientSpaceKeys.GenerateContentKey(), retry, outboxRoot);
        OfflineNoteSynchronizer synchronizer = new(reopened, host.Space, wrongKey);
        await Assert.ThrowsAnyAsync<CryptographicException>(() => synchronizer.ReconcileAsync());
        Assert.Single(reopened.Pending);
        Assert.Equal(0, reopened.Cursor);
        Assert.Single((await retry.PullAsync(host.Pull(host.FirstDevice, 0, 100))).Changes);
    }

    [Fact]
    public async Task LegacyUnsealedRemoteDeletionCannotAdvanceTheOfflineJournal()
    {
        await using TestHost host = new();
        await host.StartAsync();
        using OpaqueSyncTransportClient sender = host.ConnectFirst();
        using OpaqueSyncTransportClient receiver = host.ConnectSecond();
        using OfflineNoteJournal journal = OfflineNoteJournal.Open(
            host.NewClientDirectory("legacy-journal"), host.CollectionEpoch, host.SecondDevice);
        using AuthenticatedNoteExchange exchange = host.Exchange(journal, receiver, "legacy-outbox");
        OfflineNoteSynchronizer synchronizer = new(journal, host.Space, exchange);

        Guid record = Guid.NewGuid();
        Assert.Equal(OpaqueSyncStatus.Applied,
            (await sender.SubmitAsync(host.Mutation(host.FirstDevice, record,
                Guid.NewGuid(), 0, OpaqueSyncAction.Upsert,
                host.Encrypt(record, 1, OpaqueSyncAction.Upsert,
                    new OpaqueNoteContent("notes/legacy.md", "original"))))).Status);
        await synchronizer.ReconcileAsync();
        Assert.Equal(1, journal.Cursor);

        Assert.Equal(OpaqueSyncStatus.Applied,
            (await sender.SubmitAsync(host.Mutation(host.FirstDevice, record,
                Guid.NewGuid(), 1, OpaqueSyncAction.Delete, []))).Status);
        await Assert.ThrowsAsync<InvalidDataException>(() => synchronizer.ReconcileAsync());
        Assert.Equal(1, journal.Cursor);
        Assert.False(Assert.Single(journal.Records).Deleted);
    }

    [Fact]
    public async Task HostOpaquePathCollisionStopsBeforeAdvancingTheClientCheckpoint()
    {
        await using TestHost host = new();
        await host.StartAsync();
        using OpaqueSyncTransportClient firstClient = host.ConnectFirst();
        using OpaqueSyncTransportClient secondClient = host.ConnectSecond();
        using OfflineNoteJournal first = OfflineNoteJournal.Open(
            host.NewClientDirectory("collision-first"), host.CollectionEpoch, host.FirstDevice);
        using OfflineNoteJournal second = OfflineNoteJournal.Open(
            host.NewClientDirectory("collision-second"), host.CollectionEpoch, host.SecondDevice);
        using AuthenticatedNoteExchange firstExchange = host.Exchange(first, firstClient, "collision-outbox-first");
        using AuthenticatedNoteExchange secondExchange = host.Exchange(second, secondClient, "collision-outbox-second");
        OfflineNoteSynchronizer firstSync = new(first, host.Space, firstExchange);
        OfflineNoteSynchronizer secondSync = new(second, host.Space, secondExchange);

        Guid firstRecord = Guid.NewGuid();
        Guid secondRecord = Guid.NewGuid();
        first.Queue(firstRecord, LocalNoteAction.Upsert, "same/path.md", "first contents");
        second.Queue(secondRecord, LocalNoteAction.Upsert, "same/path.md", "second contents");
        await firstSync.ReconcileAsync();

        await Assert.ThrowsAsync<InvalidDataException>(() => secondSync.ReconcileAsync());
        Assert.Equal(0, second.Cursor);
        Assert.Equal(secondRecord, Assert.Single(second.Records).Id);
        Assert.Single(second.AcknowledgedChanges);
        Assert.Empty(second.Pending);

        await Assert.ThrowsAsync<InvalidDataException>(() => firstSync.ReconcileAsync());
        Assert.Equal(1, first.Cursor);
        Assert.Equal(firstRecord, Assert.Single(first.Records).Id);
        Assert.Empty(first.Pending);
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
        private readonly ECDsa serverSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
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

            serverCertificate = IssueLeaf(authority, serverSigner, isServer: true);
            firstCertificate = IssueLeaf(authority, firstSigner, isServer: false);
            secondCertificate = IssueLeaf(authority, secondSigner, isServer: false);
        }

        public string NewClientDirectory(string label)
        {
            string path = Path.Combine(root, label);
            PrivateDirectory(path);
            return path;
        }

        public AuthenticatedNoteExchange Exchange(OfflineNoteJournal journal,
            OpaqueSyncTransportClient client, string outboxName) =>
            new(journal, Space, 1, contentKey, client, NewClientDirectory(outboxName));

        public SslProtocols ExpectedProtocol =>
            OperatingSystem.IsMacOS() ? SslProtocols.Tls12 : SslProtocols.Tls13;

        public async Task StartAsync()
        {
            app = OperatingSystem.IsMacOS()
                ? PrivateSyncHost.BuildMacOsLoopback(IPAddress.Loopback, 0,
                    serverCertificate, authority, Registry, store)
                : PrivateSyncHost.Build(IPAddress.Loopback, 0,
                    serverCertificate, authority, Registry, store);
            app.MapGet("/__test/tls-protocol", (HttpContext context) =>
            {
                ITlsHandshakeFeature? tls = context.Features.Get<ITlsHandshakeFeature>();
                return tls is null ? Results.Problem() :
                    Results.Text($"{tls.Protocol}|{tls.NegotiatedCipherSuite}");
            });
            await app.StartAsync();
            endpoint = new Uri(app.Urls.Single().TrimEnd('/') + "/");
        }

        public void VerifyProductionListenerIsUnsupported()
        {
            Assert.Throws<PlatformNotSupportedException>(() => PrivateSyncHost.Build(
                IPAddress.Loopback, 0, serverCertificate, authority, Registry, store));
            Assert.Throws<InvalidOperationException>(() =>
                PrivateSyncHost.BuildMacOsLoopback(IPAddress.Any, 0,
                    serverCertificate, authority, Registry, store));
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
            OperatingSystem.IsMacOS()
                ? OpaqueSyncTransportClient.ConnectMacOsLoopback(
                    endpoint!, firstCertificate, authority, FirstDevice, firstSigner)
                : OpaqueSyncTransportClient.Connect(
                    endpoint!, firstCertificate, authority, FirstDevice, firstSigner);

        public OpaqueSyncTransportClient ConnectSecond() =>
            OperatingSystem.IsMacOS()
                ? OpaqueSyncTransportClient.ConnectMacOsLoopback(
                    endpoint!, secondCertificate, authority, SecondDevice, secondSigner)
                : OpaqueSyncTransportClient.Connect(
                    endpoint!, secondCertificate, authority, SecondDevice, secondSigner);

        public HttpClient RawFirst(SslProtocols? protocols = null,
            bool includeCertificate = true, X509Certificate2? certificate = null)
        {
            HttpClientHandler handler = new()
            {
                ClientCertificateOptions = ClientCertificateOption.Manual,
                SslProtocols = protocols ?? ExpectedProtocol,
                ServerCertificateCustomValidationCallback = (_, cert, _, errors) =>
                    cert is not null &&
                    (errors & (System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch |
                               System.Net.Security.SslPolicyErrors.RemoteCertificateNotAvailable)) == 0 &&
                    PinnedCertificateChain.Verify(cert, authority,
                        PinnedCertificateChain.ServerAuthentication)
            };
            if (includeCertificate)
            {
                handler.ClientCertificates.Add(certificate ?? firstCertificate);
            }

            return new HttpClient(handler) { BaseAddress = endpoint! };
        }

        public X509Certificate2 UntrustedCertificate()
        {
            using X509Certificate2 outsideAuthority = IssueRoot();
            using ECDsa outsideKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            return IssueLeaf(outsideAuthority, outsideKey, isServer: false);
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
            serverSigner.Dispose();
            store.Dispose();
            Registry.Dispose();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
