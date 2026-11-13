using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Praxis.Application.Agents.Mcp;

namespace Praxis.Infrastructure.Agents;

/// <summary>
/// Serves one CLI agent session's MCP traffic on a Unix domain socket in a fresh owner-only
/// directory. A connection must present the session's random token first; the socket and
/// directory are deleted when the session ends. Only the session's own relay can reach it.
/// </summary>
public sealed class AgentToolSocketHost : IAsyncDisposable
{
    private const int MaximumLine = 4 * 1024 * 1024;
    private readonly Socket listener;
    private readonly McpServerProtocol protocol;
    private readonly CancellationTokenSource stop = new();
    private readonly Task accepting;

    public string Directory { get; }
    public string SocketPath { get; }
    public string Token { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    public AgentToolSocketHost(string parent, McpServerProtocol protocol)
    {
        this.protocol = protocol;
        Directory = Path.Combine(parent, "agent-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)));
        System.IO.Directory.CreateDirectory(Directory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        SocketPath = Path.Combine(Directory, "s");
        listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
        listener.Listen(4);
        accepting = AcceptAsync();
    }

    private async Task AcceptAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            Socket connection;
            try
            {
                connection = await listener.AcceptAsync(stop.Token);
            }
            catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            _ = ServeAsync(connection);
        }
    }

    private async Task ServeAsync(Socket connection)
    {
        await using NetworkStream stream = new(connection, ownsSocket: true);
        using StreamReader reader = new(stream, new UTF8Encoding(false), false, 64 * 1024);
        await using StreamWriter writer = new(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        try
        {
            string? token = await reader.ReadLineAsync(stop.Token);
            if (token is null || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(token), Encoding.ASCII.GetBytes(Token)))
            {
                return;
            }

            while (await reader.ReadLineAsync(stop.Token) is { } line)
            {
                if (line.Length > MaximumLine)
                {
                    return;
                }

                if (await protocol.HandleAsync(line, stop.Token) is { } response)
                {
                    await writer.WriteLineAsync(response.AsMemory(), stop.Token);
                }
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException)
        {
            // The session ended or the relay disconnected.
        }
    }

    private int disposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 1)
        {
            return;
        }

        await stop.CancelAsync();
        listener.Dispose();
        try
        {
            await accepting;
        }
        catch (OperationCanceledException)
        {
        }

        if (System.IO.Directory.Exists(Directory))
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }

        stop.Dispose();
    }
}
