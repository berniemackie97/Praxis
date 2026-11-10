using System.Text;
using Praxis.Knowledge.Vaults;
using Xunit;

namespace Praxis.Knowledge.Tests.Vaults;

public sealed class LocalVaultComparisonTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(),
        "praxis-vault-compare-" + Guid.NewGuid().ToString("N"));

    public LocalVaultComparisonTests() => Directory.CreateDirectory(root);

    [Fact]
    public void RestoredArchiveMatchesOriginalNotesAttachmentsAndHiddenConfiguration()
    {
        string source = Path.Combine(root, "source");
        Directory.CreateDirectory(source);
        Write(source, "Notes/entry.md", [0xef, 0xbb, 0xbf, .. Encoding.UTF8.GetBytes("# Café\r\n")]);
        Write(source, "media/image.png", [0, 255, 19, 4]);
        Write(source, ".obsidian/app.json", Encoding.UTF8.GetBytes("{\"theme\":\"dark\"}"));
        string archive = Path.Combine(root, "snapshot.zip");
        LocalVaultExchange.CaptureArchive(source, archive);

        string destination = Path.Combine(root, "restored");
        LocalVaultExchange.RestoreArchive(archive, destination);

        VaultComparison report = LocalVaultComparison.CompareArchive(archive, destination);
        Assert.True(report.IsMatch);
        Assert.Equal(3, report.MatchedFiles);
        Assert.Empty(report.Differences);
        Assert.Equal(File.ReadAllBytes(Path.Combine(source, "Notes", "entry.md")),
            File.ReadAllBytes(Path.Combine(destination, "Notes", "entry.md")));
    }

    [Fact]
    public void ChangedNoteAndAttachmentAreDetectedWithoutChangingTheFolder()
    {
        string source = Path.Combine(root, "source");
        Directory.CreateDirectory(source);
        Write(source, "note.md", Encoding.UTF8.GetBytes("before"));
        Write(source, "image.png", [1, 2, 3]);
        string archive = Path.Combine(root, "snapshot.zip");
        LocalVaultExchange.CaptureArchive(source, archive);

        Write(source, "note.md", Encoding.UTF8.GetBytes("after"));
        Write(source, "image.png", [1, 2, 4]);
        VaultComparison report = LocalVaultComparison.CompareArchive(archive, source);

        Assert.False(report.IsMatch);
        Assert.Equal(0, report.MatchedFiles);
        Assert.Equal([
            new VaultFileDifference("image.png", "changed"),
            new VaultFileDifference("note.md", "changed")
        ], report.Differences);
        Assert.Equal("after", File.ReadAllText(Path.Combine(source, "note.md")));
        Assert.Equal([1, 2, 4], File.ReadAllBytes(Path.Combine(source, "image.png")));
    }

    [Fact]
    public void MissingAndUnexpectedFilesAreListedInStablePathOrder()
    {
        string source = Path.Combine(root, "source");
        Directory.CreateDirectory(source);
        Write(source, "Notes/one.md", Encoding.UTF8.GetBytes("one"));
        Write(source, "Notes/two.md", Encoding.UTF8.GetBytes("two"));
        string archive = Path.Combine(root, "snapshot.zip");
        LocalVaultExchange.CaptureArchive(source, archive);

        File.Delete(Path.Combine(source, "Notes", "two.md"));
        Write(source, "Notes/three.md", Encoding.UTF8.GetBytes("three"));
        VaultComparison report = LocalVaultComparison.CompareArchive(archive, source);

        Assert.Equal(1, report.MatchedFiles);
        Assert.Equal([
            new VaultFileDifference("Notes/three.md", "unexpected"),
            new VaultFileDifference("Notes/two.md", "missing")
        ], report.Differences);
    }

    [Fact]
    public void CorruptedArchiveIsRejectedBeforeFilesystemComparison()
    {
        string directory = Path.Combine(root, "source");
        Directory.CreateDirectory(directory);
        Write(directory, "note.md", Encoding.UTF8.GetBytes("original"));
        string archive = Path.Combine(root, "corrupt.zip");
        File.WriteAllBytes(archive, [1, 2, 3]);

        Assert.Throws<InvalidDataException>(() =>
            LocalVaultComparison.CompareArchive(archive, directory));
        Assert.Equal("original", File.ReadAllText(Path.Combine(directory, "note.md")));
    }

    [Fact]
    public void MissingDirectoryIsRejectedWithoutCreatingIt()
    {
        string directory = Path.Combine(root, "source");
        Directory.CreateDirectory(directory);
        Write(directory, "note.md", Encoding.UTF8.GetBytes("note"));
        string archive = Path.Combine(root, "snapshot.zip");
        LocalVaultExchange.CaptureArchive(directory, archive);
        string missing = Path.Combine(root, "missing");

        Assert.Throws<DirectoryNotFoundException>(() =>
            LocalVaultComparison.CompareArchive(archive, missing));
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public void SymlinkedFileIsRejectedInsteadOfFollowingItsTarget()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string directory = Path.Combine(root, "source");
        Directory.CreateDirectory(directory);
        Write(directory, "note.md", Encoding.UTF8.GetBytes("note"));
        string archive = Path.Combine(root, "snapshot.zip");
        LocalVaultExchange.CaptureArchive(directory, archive);
        File.CreateSymbolicLink(Path.Combine(directory, "alias.md"),
            Path.Combine(directory, "note.md"));

        Assert.Throws<InvalidDataException>(() =>
            LocalVaultComparison.CompareArchive(archive, directory));
    }

    private static void Write(string directory, string relative, byte[] content)
    {
        string path = Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
