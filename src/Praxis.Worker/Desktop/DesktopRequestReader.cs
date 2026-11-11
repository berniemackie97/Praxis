using System.Text;
using Praxis.Application.Desktop;

namespace Praxis.Worker.Desktop;

internal sealed class DesktopRequestReader(TextReader input)
{
    private readonly char[] buffer = new char[4096];
    private int start;
    private int end;

    internal async ValueTask<string?> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StringBuilder? frame = null;
        while (true)
        {
            if (start == end)
            {
                end = await input.ReadAsync(buffer.AsMemory(), cancellationToken);
                start = 0;
                if (end == 0)
                {
                    return frame?.ToString();
                }
            }

            int newline = Array.IndexOf(buffer, '\n', start, end - start);
            int length = (newline < 0 ? end : newline) - start;
            if (length > DesktopProtocol.MaximumRequestCharacters - (frame?.Length ?? 0))
            {
                throw new InvalidDataException("The desktop request exceeds its size limit.");
            }

            if (newline >= 0)
            {
                string line = frame is null
                    ? new string(buffer, start, length)
                    : frame.Append(buffer, start, length).ToString();
                start = newline + 1;
                return line.TrimEnd('\r');
            }

            frame ??= new StringBuilder(length);
            frame.Append(buffer, start, length);
            start = end;
        }
    }
}
