using System.Net;
using System.Net.Sockets;
using Praxis.Application.Links;

namespace Praxis.Infrastructure.Links;

/// <summary>
/// The only way Praxis fetches the web for saved links. Every connection resolves the host itself,
/// refuses non-public addresses and connects to the checked address (so DNS cannot be rebound
/// between check and use); redirects are followed by hand and each hop is checked again; no
/// cookies, credentials or proxies; responses are bounded in size, time and content type.
/// </summary>
public sealed class SafeHttpFetcher : ILinkFetcher, IMediaDownloader, IDisposable
{
    public const int MaximumBytes = 16 * 1024 * 1024;
    private const int MaximumRedirects = 5;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static readonly HashSet<string> AllowedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "text/html", "application/xhtml+xml", "text/plain", "text/markdown", "application/json", "application/pdf",
        "image/png", "image/jpeg", "image/gif", "image/webp", "image/heic"
    };

    private readonly HttpClient client;
    private readonly Func<IPAddress, bool> allowAddress;

    public SafeHttpFetcher() : this(NetworkAddressPolicy.IsPublic)
    {
    }

    internal SafeHttpFetcher(Func<IPAddress, bool> allowAddress)
    {
        this.allowAddress = allowAddress;
        SocketsHttpHandler handler = new()
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            Credentials = null,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = ConnectAsync
        };
        client = new HttpClient(handler) { Timeout = Timeout, MaxResponseContentBufferSize = MaximumBytes };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Macintosh) Praxis/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,text/plain;q=0.9,application/pdf;q=0.8,*/*;q=0.5");
    }

    public async Task<FetchedPage> FetchAsync(Uri url, CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Timeout);
        try
        {
            var opened = await OpenAsync(url, MaximumBytes, type => AllowedTypes.Contains(type), _ => false, deadline.Token);
            using HttpResponseMessage? response = opened.Response;
            if (response is null)
            {
                return FetchedPage.Failed(opened.Reason ?? "fetch_failed", opened.Status);
            }
            using MemoryStream body = new();
            if (!await CopyBoundedAsync(response.Content, body, MaximumBytes, deadline.Token))
            {
                return FetchedPage.Failed("too_large", opened.Status);
            }
            return new FetchedPage(opened.Url, opened.Status, response.Content.Headers.ContentType?.ToString(), body.ToArray(), null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return FetchedPage.Failed("fetch_timeout");
        }
        catch (IOException)
        {
            return FetchedPage.Failed("unreachable");
        }
    }

    public async Task<string?> DownloadAsync(Uri url, string destination, int maximumBytes, Func<Uri, bool> blocked, CancellationToken cancellationToken)
    {
        if (maximumBytes is < 1 or > 128 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        bool complete = false, created = false;
        try
        {
            var opened = await OpenAsync(url, maximumBytes, type => type.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ||
                type.StartsWith("video/", StringComparison.OrdinalIgnoreCase) || type == "application/octet-stream", blocked, deadline.Token);
            using HttpResponseMessage? response = opened.Response;
            if (response is null)
            {
                return opened.Reason ?? "download_failed";
            }
            await using FileStream output = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81_920,
                            FileOptions.Asynchronous | FileOptions.SequentialScan);
            created = true;
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            complete = await CopyBoundedAsync(response.Content, output, maximumBytes, deadline.Token);
            return complete ? null : "too_large";
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return "download_timeout"; }
        catch (IOException) { return "download_failed"; }
        finally
        {
            if (created && !complete && File.Exists(destination))
            {
                File.Delete(destination);
            }
        }
    }

    private async Task<(HttpResponseMessage? Response, Uri Url, string? Reason, int Status)> OpenAsync(
        Uri url, int maximumBytes, Func<string, bool> allowedType, Func<Uri, bool> blocked, CancellationToken cancellationToken)
    {
        Uri current = url;
        for (int hop = 0; hop <= MaximumRedirects; hop++)
        {
            if (SavedLink.Parse(current.AbsoluteUri) is null)
            {
                return (null, current, "unsupported_url", 0);
            }
            if (blocked(current))
            {
                return (null, current, "host_blocked", 0);
            }
            using HttpRequestMessage request = new(HttpMethod.Get, current);
            HttpResponseMessage response;
            try { response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken); }
            catch (HttpRequestException exception) when (exception.InnerException is BlockedAddressException) { return (null, current, "private_address", 0); }
            catch (HttpRequestException) { return (null, current, "unreachable", 0); }
            int status = (int)response.StatusCode;
            if (status is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                response.Dispose(); continue;
            }
            string? reason = !response.IsSuccessStatusCode ? "http_error"
                : response.Content.Headers.ContentType?.MediaType is not { } type || !allowedType(type) ? "unsupported_content"
                : response.Content.Headers.ContentLength > maximumBytes ? "too_large" : null;
            if (reason is null)
            {
                return (response, current, null, status);
            }
            response.Dispose(); return (null, current, reason, status);
        }
        return (null, current, "too_many_redirects", 0);
    }

    internal static async Task<bool> CopyBoundedAsync(HttpContent content, Stream destination, int maximumBytes, CancellationToken cancellationToken)
    {
        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken);
        byte[] chunk = new byte[81_920];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            total += read;
            if (total > maximumBytes)
            {
                return false;
            }
            await destination.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
        return true;
    }

    public void Dispose() => client.Dispose();

    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        IPAddress[] addresses = IPAddress.TryParse(context.DnsEndPoint.Host, out IPAddress? literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
        IPAddress[] allowed = addresses.Where(allowAddress).ToArray();
        if (allowed.Length == 0 || allowed.Length != addresses.Length)
        {
            // A host that resolves to any private address is refused outright.
            throw new BlockedAddressException();
        }

        Socket socket = new(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private sealed class BlockedAddressException() : IOException("The link resolves to an address Praxis does not connect to.");
}
