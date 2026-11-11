using System.Security.Cryptography;
using System.Text;
using Praxis.Domain.Intake;
using Praxis.Domain.Originals;
using Praxis.Infrastructure.Originals;
using Praxis.Infrastructure.Persistence;

namespace Praxis.Infrastructure.Intake;

public sealed class LocalQuickCapture(SqliteCatalog catalog, string intakeDirectory)
{
    private const int MaximumTextCharacters = 8_000;
    private const int MaximumOriginalBytes = 32 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private readonly LocalSourceIntake intake = new(catalog);

    public async Task<QuickCaptureReceipt> CaptureAsync(Guid captureId, string? title, string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (captureId == Guid.Empty)
        {
            throw new ArgumentException("A capture identifier is required.", nameof(captureId));
        }

        string heading = ValidateTitle(title);
        byte[] bytes = EncodeNote(heading, text);
        string root = intake.ValidateSourcePath(intakeDirectory, expectDirectory: true);
        string destination = ResolveDestination(root, heading, captureId);
        CaptureRequestContext capture = CaptureRequestContext.QuickNote(root);
        capture.Validate(destination);

        if (!File.Exists(destination))
        {
            await PublishAsync(root, destination, captureId, bytes, cancellationToken);
        }

        await VerifyPublishedAsync(destination, bytes, cancellationToken);
        using MemoryStream content = new(bytes, writable: false);
        CapturedOriginal original = await catalog.Originals.CaptureFileAsync(destination, content, bytes.Length, "quick-" + captureId.ToString("N"), capture, cancellationToken);
        return new QuickCaptureReceipt(destination, original);
    }

    private static string ValidateTitle(string? title)
    {
        string heading = title?.Trim().Normalize(NormalizationForm.FormC) ?? "";
        if (heading.Length > 80 || heading.Any(char.IsControl))
        {
            throw new ArgumentException("The note title is invalid.", nameof(title));
        }

        return heading;
    }

    private static byte[] EncodeNote(string heading, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (text.Length > MaximumTextCharacters || text.Any(c => (char.IsControl(c) && c is not ('\n' or '\r' or '\t')) || c == '\u007f'))
        {
            throw new ArgumentException("The note contains invalid or excessive text.", nameof(text));
        }

        string markdown = heading.Length == 0 ? text : $"# {heading}\n\n{text}";
        byte[] bytes = StrictUtf8.GetBytes(markdown);
        return bytes.Length > MaximumOriginalBytes ? throw new ArgumentException("The note exceeds the capture byte limit.", nameof(text)) : bytes;
    }

    private static string ResolveDestination(string root, string heading, Guid captureId)
    {
        string slug = new string(heading.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' ').Take(40).ToArray()).Trim().Replace(' ', '-');
        if (slug.Length == 0)
        {
            slug = "Note";
        }

        string suffix = "-" + captureId.ToString("N") + ".md";
        string destination = Path.Combine(root, slug + suffix);
        string[] previous = Directory.EnumerateFiles(root, "*" + suffix, SearchOption.TopDirectoryOnly).Take(2).ToArray();
        if (previous.Length > 1 || previous.Any(path => !string.Equals(path, destination, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("This capture identifier already belongs to a different note.");
        }

        return destination;
    }

    private static async Task PublishAsync(string root, string destination, Guid captureId, byte[] bytes, CancellationToken cancellationToken)
    {
        string staging = Path.Combine(root, "." + captureId.ToString("N") + "-" + Guid.NewGuid().ToString("N") + ".part");
        try
        {
            FileStreamOptions options = new()
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = PrivateFile;
            }

            await using (FileStream output = new(staging, options))
            {
                await output.WriteAsync(bytes, cancellationToken);
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(staging, destination);
        }
        finally
        {
            File.Delete(staging);
        }
    }

    private static async Task VerifyPublishedAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("A captured note cannot be a symbolic link.");
        }

        await using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length != bytes.Length)
        {
            throw new InvalidOperationException("The existing note differs from this capture. Its content was not overwritten.");
        }

        byte[] observed = new byte[bytes.Length];
        await input.ReadExactlyAsync(observed, cancellationToken);
        if (!CryptographicOperations.FixedTimeEquals(observed, bytes) || input.ReadByte() != -1)
        {
            throw new InvalidOperationException("The existing note differs from this capture. Its content was not overwritten.");
        }
    }
}
