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
public sealed class SafeHttpFetcher : ILinkFetcher, IDisposable
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
        Uri current = url;
        for (int hop = 0; hop <= MaximumRedirects; hop++)
        {
            if (SavedLink.Parse(current.AbsoluteUri) is null)
            {
                return FetchedPage.Failed("unsupported_url");
            }

            using HttpRequestMessage request = new(HttpMethod.Get, current);
            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            catch (HttpRequestException exception) when (exception.InnerException is BlockedAddressException)
            {
                return FetchedPage.Failed("private_address");
            }
            catch (HttpRequestException)
            {
                return FetchedPage.Failed("unreachable");
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return FetchedPage.Failed("fetch_timeout");
            }

            using (response)
            {
                int status = (int)response.StatusCode;
                if (status is >= 300 and < 400 && response.Headers.Location is { } location)
                {
                    current = location.IsAbsoluteUri ? location : new Uri(current, location);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    return FetchedPage.Failed("http_error", status);
                }

                string? type = response.Content.Headers.ContentType?.MediaType;
                if (type is null || !AllowedTypes.Contains(type))
                {
                    return FetchedPage.Failed("unsupported_content", status);
                }

                if (response.Content.Headers.ContentLength > MaximumBytes)
                {
                    return FetchedPage.Failed("too_large", status);
                }

                byte[]? body = await ReadBoundedAsync(response.Content, cancellationToken);
                return body is null
                    ? FetchedPage.Failed("too_large", status)
                    : new FetchedPage(current, status, response.Content.Headers.ContentType?.ToString() ?? type, body, null);
            }
        }

        return FetchedPage.Failed("too_many_redirects");
    }

    public void Dispose() => client.Dispose();

    private static async Task<byte[]?> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken);
        using MemoryStream buffer = new();
        byte[] chunk = new byte[81_920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaximumBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

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
