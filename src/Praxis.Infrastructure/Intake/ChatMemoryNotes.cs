using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Praxis.Application.Agents.Memory;
using Praxis.Application.Intake;
using Praxis.Application.Originals;
using Praxis.Application.Policy;
using Praxis.Application.Search;
using Praxis.Application.Sources;
using Praxis.Domain.Common;
using Praxis.Domain.Policy;
using Praxis.Domain.Sources;

namespace Praxis.Infrastructure.Intake;

/// <summary>Edits current, captured Markdown in the approved primary Inbox. Prior originals remain recoverable.</summary>
public sealed partial class ChatMemoryNotes(
    ISourceRepository sources, ITextSearchIndex search, IOriginalStore originals, IPolicyRepository policy,
    IIntakeSupervisor intake, string catalogRoot, string inbox) : IChatMemoryNotes
{
    private const int MaximumBytes = 64 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly FileSourceIntake paths = new(originals, catalogRoot);
    private readonly SemaphoreSlim writes = new(1, 1);

    public IReadOnlyList<ChatMemoryNote> Find(string query, CancellationToken cancellationToken)
    {
        string terms = string.Join(' ', Terms().Matches(query).Select(match => match.Value).Distinct(StringComparer.OrdinalIgnoreCase).Take(16));
        if (terms.Length == 0)
        {
            return [];
        }

        return search.QueryAny(terms, 30).DistinctBy(hit => hit.SourceId).Where(hit => IsMarkdown(hit.SourceLocator))
            .Take(12).Select(hit => TryOpen(hit.SourceId, cancellationToken)).OfType<ChatMemoryNote>().ToArray();
    }

    public ChatMemoryNote Open(Guid sourceId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            CurrentFileVersion original = sources.CurrentFile(sourceId) ?? throw Unavailable();
            string path = Locate(original);
            byte[] bytes = ReadBounded(path);
            if (Digest(bytes) != original.Digest)
            {
                throw Stale();
            }

            return new ChatMemoryNote(sourceId, Path.GetFileNameWithoutExtension(path), original.Digest, Utf8.GetString(bytes));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            throw Unavailable();
        }
    }

    public async Task<bool> ReplaceAsync(ChatMemoryNote current, string markdown, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(markdown) || markdown.Length > MaximumBytes ||
            markdown.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t')))
        {
            throw new ArgumentException("The updated note must be nonempty bounded Markdown.");
        }

        byte[] bytes = Utf8.GetBytes(markdown);
        if (bytes.Length > MaximumBytes)
        {
            throw new ArgumentException("The updated note is too large.");
        }

        await writes.WaitAsync(cancellationToken);
        try
        {
            CurrentFileVersion original = sources.CurrentFile(current.SourceId) ?? throw Unavailable();
            string path = Locate(original);
            if (current.Digest != original.Digest || current.Digest != Digest(ReadBounded(path)))
            {
                throw Stale();
            }

            string nextDigest = Digest(bytes);
            if (nextDigest == current.Digest)
            {
                return false;
            }

            SourceCategory? category = policy.ReadCategory(current.SourceId);
            // Commit the stricter policy *before* the watcher or any other consumer sees new bytes.
            policy.SaveCategory(new SourceCategory(current.SourceId, category?.Category ?? DataCategories.Documents,
                SourceCategory.OwnerOrigin, LocalOnly: true));
            string staging = Path.Combine(Path.GetDirectoryName(path)!, $".praxis-memory-{Guid.NewGuid():N}.part");
            try
            {
                FileStreamOptions options = new() { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
                if (!OperatingSystem.IsWindows())
                {
                    options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                }

                await using (FileStream stream = new(staging, options))
                {
                    await stream.WriteAsync(bytes, cancellationToken);
                    stream.Flush(flushToDisk: true);
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (Locate(sources.CurrentFile(current.SourceId) ?? throw Unavailable()) != path ||
                    current.Digest != Digest(ReadBounded(path)))
                {
                    throw Stale();
                }

                File.Move(staging, path, overwrite: true);
            }
            finally
            {
                File.Delete(staging);
            }

            // Intake observes the same path; capture now so the next chat can reopen this revision.
            using MemoryStream content = new(bytes, writable: false);
            await originals.CaptureFileAsync(path, content, MaximumBytes, "memory-" + Guid.NewGuid().ToString("N"),
                CaptureContexts.QuickNote(paths.ValidateSourcePath(inbox, expectDirectory: true)), CancellationToken.None);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("Could not finish the note update. Reopen the note before retrying.");
        }
        finally
        {
            writes.Release();
        }
    }

    private ChatMemoryNote? TryOpen(Guid id, CancellationToken token)
    {
        try { return Open(id, token); }
        catch (InvalidOperationException) { return null; }
    }

    private string Locate(CurrentFileVersion source)
    {
        intake.EnsurePrimaryRootApproved();
        string root = paths.ValidateSourcePath(inbox, expectDirectory: true);
        string path = paths.ValidateSourcePath(source.Locator, expectDirectory: false);
        if (!PathScope.Contains(root, path) || !IsMarkdown(path) || path != source.Locator ||
            Path.GetFileName(path).Equals("AGENTS.md", StringComparison.OrdinalIgnoreCase))
        {
            throw Unavailable();
        }

        return path;
    }

    private static byte[] ReadBounded(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumBytes)
        {
            throw Unavailable();
        }

        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1)
        {
            throw Stale();
        }

        return bytes;
    }

    private static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static bool IsMarkdown(string path) => Path.GetExtension(path).Equals(".md", StringComparison.OrdinalIgnoreCase);
    private static InvalidOperationException Unavailable() => new("This note is not an editable, indexed Markdown file in the approved Primary Inbox.");
    private static InvalidOperationException Stale() => new("The note changed since it was indexed or opened. Reopen it after intake finishes.");

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}_-]{2,27}")]
    private static partial Regex Terms();
}
