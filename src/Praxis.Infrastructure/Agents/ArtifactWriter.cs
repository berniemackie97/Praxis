using System.Text;
using Praxis.Application.Agents;
using Praxis.Application.Originals;
using Praxis.Domain.Originals;
using Praxis.Infrastructure.Intake;

namespace Praxis.Infrastructure.Agents;

/// <summary>
/// Writes agent-authored Markdown to "Praxis Artifacts" inside the owner's primary intake folder
/// (atomic replace, owner-only permissions) and captures it, so each write is a source revision
/// with provenance like any other note.
/// </summary>
public sealed class ArtifactWriter(IOriginalStore originals, string catalogDirectory, string intakeDirectory) : IArtifactWriter
{
    private const int MaximumBytes = 256 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly FileSourceIntake intake = new(originals, catalogDirectory);

    public async Task<(string Path, CapturedOriginal Original)> WriteAsync(Guid artifactId, string title, string markdown, CancellationToken cancellationToken)
    {
        string heading = title.Trim();
        if (artifactId == Guid.Empty || heading.Length is < 1 or > 80 || heading.Any(char.IsControl) || markdown.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
        {
            throw new ArgumentException("The artifact title or content is invalid.");
        }

        byte[] bytes = StrictUtf8.GetBytes($"# {heading}\n\n{markdown.Trim()}\n");
        if (bytes.Length > MaximumBytes)
        {
            throw new ArgumentException("The artifact is too large.");
        }

        string root = intake.ValidateSourcePath(intakeDirectory, expectDirectory: true);
        string folder = Path.Combine(root, "Praxis Artifacts");
        Directory.CreateDirectory(folder);
        string existing = Directory.EnumerateFiles(folder, "*-" + artifactId.ToString("N")[..12] + ".md").FirstOrDefault()
                          ?? Path.Combine(folder, Slug(heading) + "-" + artifactId.ToString("N")[..12] + ".md");
        string staging = existing + "." + Guid.NewGuid().ToString("N") + ".part";
        FileStreamOptions options = new() { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        try
        {
            await using (FileStream output = new(staging, options))
            {
                await output.WriteAsync(bytes, cancellationToken);
                output.Flush(flushToDisk: true);
            }

            File.Move(staging, existing, overwrite: true);
        }
        finally
        {
            File.Delete(staging);
        }

        using MemoryStream content = new(bytes, writable: false);
        CaptureRequestContext context = CaptureContexts.QuickNote(root);
        context.Validate(existing);
        CapturedOriginal original = await originals.CaptureFileAsync(existing, content, bytes.Length,
            $"artifact-{artifactId:N}-{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))[..16]}", context, cancellationToken);
        return (existing, original);
    }

    private static string Slug(string title)
    {
        string slug = new string(title.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-').Take(40).ToArray()).Trim().Replace(' ', '-');
        return slug.Length == 0 ? "Artifact" : slug;
    }
}
