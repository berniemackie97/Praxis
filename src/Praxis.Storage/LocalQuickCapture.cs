using System.Security.Cryptography;
using System.Text;

namespace Praxis.Storage;

public sealed record LocalQuickCaptureReceipt(string SourcePath, CapturedOriginal Original);

public sealed class LocalQuickCapture(LocalCatalog catalog, string intakeDirectory)
{
    private const int MaximumTextCharacters = 8_000;
    private const int MaximumOriginalBytes = 32 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly UnixFileMode PrivateFile =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private readonly LocalSourceIntake intake = new(catalog);

    public async Task<LocalQuickCaptureReceipt> CaptureAsync(
        Guid captureId, string? title, string text, CancellationToken cancellationToken = default)
    {
        if (captureId == Guid.Empty)
        {
            throw new ArgumentException("A capture identifier is required.", nameof(captureId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (text.Length > MaximumTextCharacters || text.Any(IsInvalidTextCharacter))
        {
            throw new ArgumentException("The note contains invalid or excessive text.", nameof(text));
        }

        var heading = title?.Trim().Normalize(NormalizationForm.FormC) ?? "";
        if (heading.Length > 80 || heading.Any(char.IsControl))
        {
            throw new ArgumentException("The note title is invalid.", nameof(title));
        }

        var markdown = heading.Length == 0 ? text : $"# {heading}\n\n{text}";
        var bytes = StrictUtf8.GetBytes(markdown);
        if (bytes.Length > MaximumOriginalBytes)
        {
            throw new ArgumentException("The note exceeds the capture byte limit.", nameof(text));
        }

        var root = intake.ValidateSourcePath(intakeDirectory, expectDirectory: true);
        var slug = new string(heading.Where(c =>
            char.IsLetterOrDigit(c) || c is '-' or '_' or ' ').Take(40).ToArray())
            .Trim().Replace(' ', '-');
        if (slug.Length == 0)
        {
            slug = "Note";
        }

        var suffix = "-" + captureId.ToString("N") + ".md";
        var destination = Path.Combine(root, slug + suffix);
        var previous = Directory.EnumerateFiles(root, "*" + suffix, SearchOption.TopDirectoryOnly)
            .Take(2).ToArray();
        if (previous.Any(path => !string.Equals(path, destination, StringComparison.Ordinal)) ||
            previous.Length > 1)
        {
            throw new InvalidOperationException("This capture identifier already belongs to another note.");
        }

        if (!File.Exists(destination))
        {
            var staging = Path.Combine(root,
                "." + captureId.ToString("N") + "-" + Guid.NewGuid().ToString("N") + ".part");
            try
            {
                var options = new FileStreamOptions
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

                await using (var output = new FileStream(staging, options))
                {
                    await output.WriteAsync(bytes, cancellationToken);
                    await output.FlushAsync(cancellationToken);
                    output.Flush(flushToDisk: true);
                }

                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    File.Move(staging, destination);
                }
                catch (IOException) when (File.Exists(destination))
                {
                    // Another submission with the same capture ID won the atomic publication.
                }
            }
            finally
            {
                File.Delete(staging);
            }
        }

        if ((File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("A captured note cannot be a symbolic link.");
        }

        var actual = new FileInfo(destination);
        if (actual.Length != bytes.Length ||
            !CryptographicOperations.FixedTimeEquals(
                await File.ReadAllBytesAsync(destination, cancellationToken), bytes))
        {
            throw new InvalidOperationException(
                "The existing note differs from this capture. Its content was not overwritten.");
        }

        var accepted = await intake.ImportFileAsync(
            destination, bytes.Length, "quick-" + captureId.ToString("N"), cancellationToken);
        if (accepted.Sha256 != Convert.ToHexStringLower(SHA256.HashData(bytes)))
        {
            throw new InvalidDataException("The captured note changed during import.");
        }

        return new LocalQuickCaptureReceipt(destination, accepted);
    }

    private static bool IsInvalidTextCharacter(char c) =>
        (char.IsControl(c) && c is not ('\n' or '\r' or '\t')) || c == '\u007f';
}
