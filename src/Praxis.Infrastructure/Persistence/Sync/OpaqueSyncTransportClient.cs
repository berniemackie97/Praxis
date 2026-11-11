using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Praxis.Domain.Security;
using Praxis.Infrastructure.Security.Cryptography;

namespace Praxis.Infrastructure.Persistence.Sync;

public sealed class OpaqueSyncTransportClient : IDisposable
{
    private readonly HttpClient http;
    private readonly ECDsa signer;
    private readonly Guid deviceId;

    private OpaqueSyncTransportClient(HttpClient http, ECDsa signer, Guid deviceId)
    {
        this.http = http;
        this.signer = signer;
        this.deviceId = deviceId;
    }

    public static OpaqueSyncTransportClient Connect(Uri endpoint,
        X509Certificate2 clientCertificate, X509Certificate2 pinnedServerRoot,
        Guid deviceId, ECDsa deviceSigningKey)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(clientCertificate);
        ArgumentNullException.ThrowIfNull(pinnedServerRoot);
        ArgumentNullException.ThrowIfNull(deviceSigningKey);
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != Uri.UriSchemeHttps ||
            endpoint.UserInfo.Length != 0 || deviceId == Guid.Empty ||
            !clientCertificate.HasPrivateKey)
        {
            throw new ArgumentException("An authenticated HTTPS endpoint and device are required.");
        }

        HttpClientHandler handler = new()
        {
            ClientCertificateOptions = ClientCertificateOption.Manual,
            ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
                certificate is not null &&
                (errors & (SslPolicyErrors.RemoteCertificateNameMismatch |
                           SslPolicyErrors.RemoteCertificateNotAvailable)) == 0 &&
                PinnedCertificateChain.Verify(certificate, pinnedServerRoot,
                    PinnedCertificateChain.ServerAuthentication)
        };
        handler.ClientCertificates.Add(clientCertificate);
        HttpClient client = new(handler) { BaseAddress = endpoint, Timeout = TimeSpan.FromSeconds(20) };
        return new OpaqueSyncTransportClient(client, deviceSigningKey, deviceId);
    }

    public async Task<OpaqueSyncReceipt> SubmitAsync(
        OpaqueSyncMutation mutation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        if (mutation.DeviceId != deviceId)
        {
            throw new ArgumentException("The mutation does not belong to this device.", nameof(mutation));
        }

        (Guid challengeId, byte[] signature) = await Authorize(
            mutation.SpaceId, SecurityPermission.Write, cancellationToken);
        using HttpResponseMessage response = await http.PostAsJsonAsync("v1/sync/submit",
            new SyncSubmitRequest(challengeId, signature, mutation), cancellationToken);
        if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Conflict or
            HttpStatusCode.BadRequest or HttpStatusCode.InsufficientStorage))
        {
            response.EnsureSuccessStatusCode();
        }

        return await response.Content.ReadFromJsonAsync<OpaqueSyncReceipt>(
            cancellationToken) ?? throw new InvalidDataException("The sync receipt is missing.");
    }

    public async Task<OpaqueSyncPage> PullAsync(
        OpaqueSyncPull request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.DeviceId != deviceId)
        {
            throw new ArgumentException("The pull request does not belong to this device.", nameof(request));
        }

        (Guid challengeId, byte[] signature) = await Authorize(
            request.SpaceId, SecurityPermission.Read, cancellationToken);
        using HttpResponseMessage response = await http.PostAsJsonAsync("v1/sync/pull",
            new SyncPullRequest(challengeId, signature, request), cancellationToken);
        if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Conflict or
            HttpStatusCode.BadRequest))
        {
            response.EnsureSuccessStatusCode();
        }

        return await response.Content.ReadFromJsonAsync<OpaqueSyncPage>(
            cancellationToken) ?? throw new InvalidDataException("The sync page is missing.");
    }

    private async Task<(Guid, byte[])> Authorize(Guid spaceId, SecurityPermission permission,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await http.PostAsJsonAsync("v1/sync/challenge",
            new SyncChallengeRequest(deviceId, spaceId, permission), cancellationToken);
        response.EnsureSuccessStatusCode();
        DeviceChallenge? challenge = await response.Content.ReadFromJsonAsync<DeviceChallenge>(
            cancellationToken);
        if (challenge is null || challenge.ChallengeId == Guid.Empty ||
            challenge.Message.Length is < 32 or > 1024 ||
            challenge.ExpiresUtc <= DateTimeOffset.UtcNow)
        {
            throw new InvalidDataException("The device challenge is invalid.");
        }

        byte[] signature = signer.SignData(challenge.Message, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return (challenge.ChallengeId, signature);
    }

    public void Dispose() => http.Dispose();
}
