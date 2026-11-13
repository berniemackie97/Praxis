using System.Net.Sockets;
using System.Text;

namespace Praxis.Infrastructure.Agents;

/// <summary>
/// The process a CLI agent launches as its "praxis" MCP server: it forwards newline-delimited
/// JSON-RPC between its stdio and the worker's per-session socket after presenting the token.
/// It holds no catalog access of its own.
/// </summary>
public static class McpRelay
{
    public static async Task<int> RunAsync(string socketPath, string token, Stream input, Stream output, CancellationToken cancellationToken)
    {
        using Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken);
        }
        catch (SocketException)
        {
            return 2;
        }

        await using NetworkStream stream = new(socket, ownsSocket: false);
        await using StreamWriter toWorker = new(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        await toWorker.WriteLineAsync(token);
        using StreamReader fromAgent = new(input, new UTF8Encoding(false));
        using StreamReader fromWorker = new(stream, new UTF8Encoding(false));
        await using StreamWriter toAgent = new(output, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        Task upstream = Task.Run(async () =>
        {
            try
            {
                while (await fromAgent.ReadLineAsync(cancellationToken) is { } line)
                {
                    await toWorker.WriteLineAsync(line.AsMemory(), cancellationToken);
                }

                socket.Shutdown(SocketShutdown.Send);
            }
            catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException)
            {
                // The worker closed the session (or rejected the token); nothing more to forward.
            }
        }, cancellationToken);
        while (await fromWorker.ReadLineAsync(cancellationToken) is { } line)
        {
            await toAgent.WriteLineAsync(line.AsMemory(), cancellationToken);
        }

        await upstream;
        return 0;
    }
}
