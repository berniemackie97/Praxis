using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Praxis.Domain.Security;
using Praxis.Infrastructure.Persistence.Security.Registry;
using Praxis.Infrastructure.Persistence.Sync;
using Praxis.Infrastructure.Security.Cryptography;

namespace Praxis.SyncHost;

public static class PrivateSyncHost
{
    private const int MaximumRequestBytes = 96 * 1024;

    public static WebApplication Build(IPAddress bindAddress, int port,
        X509Certificate2 serverCertificate, X509Certificate2 trustedDeviceRoot,
        SecurityRegistry registry, OpaqueSyncStore store)
    {
        if (OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException(
                "The private TLS 1.3 server requires a supported Linux or Windows host.");
        }

        return Build(bindAddress, port, serverCertificate, trustedDeviceRoot,
            registry, store, SslProtocols.Tls13);
    }

    internal static WebApplication BuildMacOsLoopback(IPAddress bindAddress, int port,
        X509Certificate2 serverCertificate, X509Certificate2 trustedDeviceRoot,
        SecurityRegistry registry, OpaqueSyncStore store)
    {
        if (!OperatingSystem.IsMacOS() || !IPAddress.IsLoopback(bindAddress))
        {
            throw new InvalidOperationException(
                "The macOS TLS 1.2 compatibility listener is restricted to loopback tests.");
        }

        return Build(bindAddress, port, serverCertificate, trustedDeviceRoot,
            registry, store, SslProtocols.Tls12);
    }

    private static WebApplication Build(IPAddress bindAddress, int port,
        X509Certificate2 serverCertificate, X509Certificate2 trustedDeviceRoot,
        SecurityRegistry registry, OpaqueSyncStore store, SslProtocols protocols)
    {
        ArgumentNullException.ThrowIfNull(bindAddress);
        ArgumentNullException.ThrowIfNull(serverCertificate);
        ArgumentNullException.ThrowIfNull(trustedDeviceRoot);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(store);
        if (!serverCertificate.HasPrivateKey || port is < 0 or > 65535)
        {
            throw new ArgumentException("A server TLS certificate and valid port are required.");
        }

        WebApplicationBuilder builder = WebApplication.CreateBuilder(
            new WebApplicationOptions { Args = [], EnvironmentName = Environments.Production });
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.Configure(new ConfigurationBuilder().Build(), reloadOnChange: false);
            server.Limits.MaxRequestBodySize = MaximumRequestBytes;
            server.Limits.MaxConcurrentConnections = 32;
            server.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            server.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(10);
            server.Listen(bindAddress, port, endpoint => endpoint.UseHttps(https =>
            {
                https.ServerCertificate = serverCertificate;
                https.SslProtocols = protocols;
                https.HandshakeTimeout = TimeSpan.FromSeconds(10);
                https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                // The operator-controlled registry revokes enrolled devices at every request.
                // Offline CA CRL/OCSP publication is not implemented in this host yet.
                https.CheckCertificateRevocation = false;
                https.ClientCertificateValidation = (certificate, _, _) =>
                    PinnedCertificateChain.Verify(certificate, trustedDeviceRoot,
                        PinnedCertificateChain.ClientAuthentication);
            }));
        });
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(
                context => RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.ClientCertificate?.Thumbprint ?? "no-certificate",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 120,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                        AutoReplenishment = true
                    }));
        });
        builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.AspNetCore.Routing.EndpointMiddleware", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.AspNetCore.Server.Kestrel", LogLevel.Warning);

        WebApplication app = builder.Build();
        app.UseRateLimiter();
        SemaphoreSlim inFlight = new(8, 8);
        OpaqueSyncGateway gateway = new(registry, store);
        app.Use(async (context, next) =>
        {
            if (!inFlight.Wait(0))
            {
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                return;
            }

            using CancellationTokenSource timeout =
                CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            context.RequestAborted = timeout.Token;
            try
            {
                await next(context);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                if (!context.Response.HasStarted)
                {
                    context.Response.StatusCode = StatusCodes.Status408RequestTimeout;
                }
            }
            finally
            {
                inFlight.Release();
            }
        });

        app.MapPost("/v1/sync/challenge", async (HttpContext context) =>
        {
            SyncChallengeRequest? request = await Read<SyncChallengeRequest>(context);
            if (request is null || request.DeviceId == Guid.Empty ||
                request.SpaceId == Guid.Empty ||
                request.Permission is not (SecurityPermission.Read or SecurityPermission.Write))
            {
                return Results.BadRequest();
            }

            if (Identify(context, registry, trustedDeviceRoot, request.DeviceId) is null)
            {
                return Results.Unauthorized();
            }

            try
            {
                DeviceChallenge challenge = registry.DeviceProofs.IssueChallenge(
                    request.DeviceId, request.SpaceId, null, request.Permission);
                return Results.Json(challenge);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            catch (InvalidOperationException)
            {
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            }
        });

        app.MapPost("/v1/sync/submit", async (HttpContext context) =>
        {
            SyncSubmitRequest? request = await Read<SyncSubmitRequest>(context);
            if (request?.Mutation is not { } mutation ||
                request.Signature is not { Length: 64 })
            {
                return Results.BadRequest();
            }

            VerifiedCertificateIdentity? identity =
                Identify(context, registry, trustedDeviceRoot, mutation.DeviceId);
            if (identity is null)
            {
                return Results.Unauthorized();
            }

            VerifiedDeviceProof? proof =
                registry.DeviceProofs.Authenticate(request.ChallengeId, request.Signature);
            if (proof is null)
            {
                return Results.Unauthorized();
            }

            try
            {
                OpaqueSyncReceipt receipt = gateway.Submit(identity, proof, mutation);
                int status = receipt.Status switch
                {
                    OpaqueSyncStatus.Applied => StatusCodes.Status200OK,
                    OpaqueSyncStatus.StaleRevision or
                    OpaqueSyncStatus.OperationKeyReused or
                    OpaqueSyncStatus.WrongCollectionEpoch or
                    OpaqueSyncStatus.ResetRequired => StatusCodes.Status409Conflict,
                    OpaqueSyncStatus.QuotaExceeded => StatusCodes.Status507InsufficientStorage,
                    _ => StatusCodes.Status400BadRequest
                };
                return Results.Json(receipt, statusCode: status);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            catch (ArgumentException)
            {
                return Results.BadRequest();
            }
        });

        app.MapPost("/v1/sync/pull", async (HttpContext context) =>
        {
            SyncPullRequest? request = await Read<SyncPullRequest>(context);
            if (request?.Pull is not { } pull ||
                request.Signature is not { Length: 64 })
            {
                return Results.BadRequest();
            }

            VerifiedCertificateIdentity? identity =
                Identify(context, registry, trustedDeviceRoot, pull.DeviceId);
            if (identity is null)
            {
                return Results.Unauthorized();
            }

            VerifiedDeviceProof? proof =
                registry.DeviceProofs.Authenticate(request.ChallengeId, request.Signature);
            if (proof is null)
            {
                return Results.Unauthorized();
            }

            try
            {
                OpaqueSyncPage page = gateway.Pull(identity, proof, pull);
                int status = page.Status switch
                {
                    OpaqueSyncStatus.Ok => StatusCodes.Status200OK,
                    OpaqueSyncStatus.WrongCollectionEpoch or
                    OpaqueSyncStatus.CursorExpired or
                    OpaqueSyncStatus.ResetRequired => StatusCodes.Status409Conflict,
                    _ => StatusCodes.Status400BadRequest
                };
                return Results.Json(page, statusCode: status);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            catch (ArgumentException)
            {
                return Results.BadRequest();
            }
        });

        app.MapPost("/v1/sync/snapshot", async (HttpContext context) =>
        {
            SyncSnapshotRequest? request = await Read<SyncSnapshotRequest>(context);
            if (request?.Snapshot is not { } snapshot ||
                request.Signature is not { Length: 64 })
            {
                return Results.BadRequest();
            }

            VerifiedCertificateIdentity? identity =
                Identify(context, registry, trustedDeviceRoot, snapshot.DeviceId);
            if (identity is null)
            {
                return Results.Unauthorized();
            }

            VerifiedDeviceProof? proof =
                registry.DeviceProofs.Authenticate(request.ChallengeId, request.Signature);
            if (proof is null)
            {
                return Results.Unauthorized();
            }

            try
            {
                OpaqueSyncSnapshotPage page = gateway.Snapshot(identity, proof, snapshot);
                int status = page.Status switch
                {
                    OpaqueSyncStatus.Ok => StatusCodes.Status200OK,
                    OpaqueSyncStatus.WrongCollectionEpoch or
                    OpaqueSyncStatus.ResetRequired => StatusCodes.Status409Conflict,
                    _ => StatusCodes.Status400BadRequest
                };
                return Results.Json(page, statusCode: status);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            catch (ArgumentException)
            {
                return Results.BadRequest();
            }
        });

        return app;
    }

    private static VerifiedCertificateIdentity? Identify(HttpContext context,
        SecurityRegistry registry, X509Certificate2 root, Guid deviceId) =>
        context.Connection.ClientCertificate is { } certificate
            ? registry.Certificates.AuthenticateWithRegistryRevocation(
                certificate, root, deviceId)
            : null;

    private static async Task<T?> Read<T>(HttpContext context)
    {
        try
        {
            return await context.Request.ReadFromJsonAsync<T>(context.RequestAborted);
        }
        catch (BadHttpRequestException)
        {
            return default;
        }
        catch (System.Text.Json.JsonException)
        {
            return default;
        }
        catch (NotSupportedException)
        {
            return default;
        }
    }
}
