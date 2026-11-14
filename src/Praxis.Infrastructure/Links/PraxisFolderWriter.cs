using System.Security.Cryptography;
using Praxis.Application.Originals;
using Praxis.Domain.Originals;
using Praxis.Infrastructure.Intake;

namespace Praxis.Infrastructure.Links;

/// <summary>
/// Writes a Praxis-made file into a named folder inside the owner's primary intake folder (atomic,
/// owner-only) and captures it at once, so it becomes a source with provenance like anything the
/// owner drops there.
/// </summary>
internal sealed class PraxisFolderWriter(IOriginalStore originals, string catalogDirectory, string intakeDirectory, string folderName)
{
    private const long MaximumBytes = 16 * 1024 * 1024;
    private readonly FileSourceIntake intake = new(originals, catalogDirectory);

    internal async Task<string> WriteAsync(string name, string extension, byte[] content, CancellationToken cancellationToken)
    {
        if (content.LongLength > MaximumBytes || extension.Length is < 2 or > 10 || extension[0] != '.' || !extension[1..].All(char.IsAsciiLetterOrDigit))
        {
            throw new ArgumentException("The file is too large or its type is invalid.");
        }

        string root = intake.ValidateSourcePath(intakeDirectory, expectDirectory: true);
        string folder = Path.Combine(root, folderName);
        Directory.CreateDirectory(folder);
        string digest = Convert.ToHexStringLower(SHA256.HashData(content));
        string path = Path.Combine(folder, Slug(name) + "-" + digest[..10] + extension);
        if (!File.Exists(path))
        {
            string staging = path + "." + Guid.NewGuid().ToString("N") + ".part";
            FileStreamOptions options = new() { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            try
            {
                await using (FileStream output = new(staging, options))
                {
                    await output.WriteAsync(content, cancellationToken);
                    output.Flush(flushToDisk: true);
                }

                File.Move(staging, path, overwrite: true);
            }
            finally
            {
                File.Delete(staging);
            }
        }

        using MemoryStream stream = new(content, writable: false);
        CaptureRequestContext context = CaptureContexts.QuickNote(root);
        context.Validate(path);
        await originals.CaptureFileAsync(path, stream, content.LongLength, $"{folderName.Replace(' ', '-').ToLowerInvariant()}-{digest[..32]}", context, cancellationToken);
        return path;
    }

    private static string Slug(string name)
    {
        string slug = new string(name.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_').Take(60).ToArray()).Trim().Replace(' ', '-');
        return slug.Length == 0 ? "Link" : slug;
    }
}
