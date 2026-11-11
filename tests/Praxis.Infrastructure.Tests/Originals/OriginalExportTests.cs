using System.Security.Cryptography;
using Praxis.Domain.Originals;
using Praxis.Infrastructure.FileSystem;
using Praxis.Infrastructure.Persistence;
using Praxis.Infrastructure.Tests.Support;
using Xunit;

namespace Praxis.Infrastructure.Tests.Originals;

public sealed class OriginalExportTests : IDisposable
{
    private readonly string workspace = Path.Combine(TestPaths.Temp,
        "praxis-export-" + Guid.NewGuid().ToString("N"));
    private readonly string catalogRoot;
    private readonly string exportRoot;

    public OriginalExportTests()
    {
        catalogRoot = Path.Combine(workspace, "catalog");
        exportRoot = Path.Combine(workspace, "exports");
        Directory.CreateDirectory(catalogRoot);
        Directory.CreateDirectory(exportRoot);
        if (!OperatingSystem.IsWindows())
        {
            const UnixFileMode mode = UnixFileMode.UserRead |
                UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            File.SetUnixFileMode(workspace, mode);
            File.SetUnixFileMode(catalogRoot, mode);
            File.SetUnixFileMode(exportRoot, mode);
        }
    }

    [Fact]
    public async Task ExportsExactBinaryOriginalAfterWatchedSourceDisappearsAndRestart()
    {
        byte[] bytes = Enumerable.Range(0, 256_000).Select(x => (byte)(x % 251)).ToArray();
        CapturedOriginal captured;
        using (SqliteCatalog catalog = SqliteCatalog.Open(catalogRoot))
        {
            captured = await Capture(catalog, "offline.bin", bytes, "one");
            OriginalExport exported = await catalog.Originals.ExportAsync(
                captured.ItemId, captured.Revision, Path.Combine(exportRoot, "copy.bin"));
            Assert.Equal(captured.ItemId, exported.ItemId);
            Assert.Equal(captured.Sha256, exported.Sha256);
            Assert.Equal(bytes.LongLength, exported.Length);
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(exportRoot, "copy.bin")));
            await catalog.Originals.VerifyAsync(captured.ItemId);
            Assert.Empty(Directory.EnumerateFiles(exportRoot, "*.part"));
        }

        using SqliteCatalog reopened = SqliteCatalog.Open(catalogRoot);
        await reopened.Originals.ExportAsync(
            captured.ItemId, captured.Revision, Path.Combine(exportRoot, "recovered.bin"));
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(exportRoot, "recovered.bin")));
    }

    [Fact]
    public async Task ExistingDestinationsAreNeverOverwrittenOrDeleted()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(catalogRoot);
        CapturedOriginal captured = await Capture(catalog, "note.txt", [2, 3, 4], "one");
        string destination = Path.Combine(exportRoot, "keep.txt");
        File.WriteAllBytes(destination, [9, 8, 7]);

        await Assert.ThrowsAsync<IOException>(() =>
            catalog.Originals.ExportAsync(captured.ItemId, 1, destination));
        Assert.Equal(new byte[] { 9, 8, 7 }, File.ReadAllBytes(destination));
        Assert.Empty(Directory.EnumerateFiles(exportRoot, ".praxis-export-*.part"));
        await catalog.Originals.VerifyAsync(captured.ItemId);
    }

    [Fact]
    public async Task StaleRevokedAndUnknownItemsCannotExport()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(catalogRoot);
        CapturedOriginal initial = await Capture(catalog, "revised.md", [1], "one");
        await Capture(catalog, "revised.md", [2], "two");
        await Assert.ThrowsAsync<ArgumentException>(() =>
            catalog.Originals.ExportAsync(Guid.Empty, 1,
                Path.Combine(exportRoot, "invalid.md")));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalog.Originals.ExportAsync(initial.ItemId, 1,
                Path.Combine(exportRoot, "outdated.md")));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalog.Originals.ExportAsync(Guid.NewGuid(), 1,
                Path.Combine(exportRoot, "unknown.md")));

        Assert.True(catalog.Sources.Revoke(initial.SourceId));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalog.Originals.ExportAsync(initial.ItemId, 2,
                Path.Combine(exportRoot, "revoked.md")));

        Assert.Empty(Directory.EnumerateFileSystemEntries(exportRoot));
        await catalog.Originals.VerifyAsync(initial.ItemId);
    }

    [Fact]
    public async Task CorruptOriginalFailsVerificationWithoutPublishingAFile()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(catalogRoot);
        CapturedOriginal captured = await Capture(catalog, "file.bin", [1, 2, 3, 4], "one");
        string path = Path.Combine(catalogRoot, "objects",
            captured.Sha256[..2], captured.Sha256[2..4], captured.Sha256);
        File.WriteAllBytes(path, [1, 2, 3, 5]);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            catalog.Originals.ExportAsync(captured.ItemId, 1,
                Path.Combine(exportRoot, "unverified.bin")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(exportRoot));
        Assert.Equal(new byte[] { 1, 2, 3, 5 }, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task ExportRejectsCatalogPathsAndSymbolicLinkRedirection()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(catalogRoot);
        CapturedOriginal captured = await Capture(catalog, "file.bin", [1, 2, 3], "one");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalog.Originals.ExportAsync(captured.ItemId, 1,
                Path.Combine(catalogRoot, "illegal.bin")));
        Assert.False(File.Exists(Path.Combine(catalogRoot, "illegal.bin")));

        if (OperatingSystem.IsMacOS() &&
            catalogRoot.StartsWith("/var/", StringComparison.Ordinal))
        {
            string alternateRoot = "/private" + catalogRoot;
            if (Directory.Exists(alternateRoot))
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    catalog.Originals.ExportAsync(captured.ItemId, 1,
                        Path.Combine(alternateRoot, "alternate.bin")));
                Assert.False(File.Exists(Path.Combine(catalogRoot, "alternate.bin")));
            }
        }

        if (OperatingSystem.IsMacOS() &&
            Directory.Exists(catalogRoot.ToUpperInvariant()))
        {
            string caseAlias = Path.Combine(catalogRoot.ToUpperInvariant(), "case-alias.bin");
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                catalog.Originals.ExportAsync(captured.ItemId, 1, caseAlias));
            Assert.False(File.Exists(caseAlias));
        }

        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string linked = Path.Combine(workspace, "linked");
        Directory.CreateSymbolicLink(linked, catalogRoot);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalog.Originals.ExportAsync(captured.ItemId, 1,
                Path.Combine(linked, "redirected.bin")));
        Assert.False(File.Exists(Path.Combine(catalogRoot, "redirected.bin")));

        string chained = Path.Combine(workspace, "chained");
        Directory.CreateSymbolicLink(chained, linked);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalog.Originals.ExportAsync(captured.ItemId, 1,
                Path.Combine(chained, "nested.bin")));
        Assert.False(File.Exists(Path.Combine(catalogRoot, "nested.bin")));

        string permittedLink = Path.Combine(workspace, "permitted-link");
        Directory.CreateSymbolicLink(permittedLink, exportRoot);
        OriginalExport receipt = await catalog.Originals.ExportAsync(captured.ItemId, 1,
            Path.Combine(permittedLink, "safe.bin"));
        Assert.Equal(captured.Sha256, receipt.Sha256);
        Assert.Equal(new byte[] { 1, 2, 3 },
            File.ReadAllBytes(Path.Combine(exportRoot, "safe.bin")));
    }

    [Fact]
    public async Task CancellationLeavesOnlyOriginalBytes()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(catalogRoot);
        CapturedOriginal captured = await Capture(catalog, "original.bin", new byte[300_000], "one");
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            catalog.Originals.ExportAsync(captured.ItemId, 1,
                Path.Combine(exportRoot, "cancelled.bin"), cancellation.Token));
        Assert.Empty(Directory.EnumerateFileSystemEntries(exportRoot));
        await catalog.Originals.VerifyAsync(captured.ItemId);
    }

    [Fact]
    public async Task EmptyOriginalCanBeExportedAndVerified()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(catalogRoot);
        CapturedOriginal captured = await Capture(catalog, "empty.txt", [], "one");
        string destination = Path.Combine(exportRoot, "empty.txt");

        OriginalExport exported = await catalog.Originals.ExportAsync(captured.ItemId, 1, destination);
        Assert.Equal(0, exported.Length);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Array.Empty<byte>())), exported.Sha256);
        Assert.Empty(File.ReadAllBytes(destination));
        await catalog.Originals.VerifyAsync(captured.ItemId);
    }

    [Fact]
    public async Task DirectoryReplacementAfterStagingCannotRedirectExportIntoPrivateCatalog()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string output = Path.Combine(exportRoot, "safe.bin");
        string moved = Path.Combine(workspace, "moved-exports");
        using (OriginalExportDestination export = OriginalExportDestination.Open(output, catalogRoot))
        {
            await using (FileStream staging = export.CreateStaging())
            {
                await staging.WriteAsync(new byte[] { 3, 2, 1 });
                staging.Flush(flushToDisk: true);
            }

            Directory.Move(exportRoot, moved);
            Directory.CreateSymbolicLink(exportRoot, catalogRoot);
            Assert.Throws<InvalidOperationException>(() => export.Publish());
            Assert.False(File.Exists(Path.Combine(catalogRoot, "safe.bin")));
            Assert.False(File.Exists(Path.Combine(moved, "safe.bin")));
        }

        Assert.Empty(Directory.EnumerateFiles(moved, ".praxis-export-*.part"));
    }

    [Fact]
    public async Task PinnedDirectoryCannotBeMovedIntoPrivateCatalogBeforePublication()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string moved = Path.Combine(catalogRoot, "disallowed-export");
        using (OriginalExportDestination export =
               OriginalExportDestination.Open(Path.Combine(exportRoot, "blocked.bin"), catalogRoot))
        {
            await using (FileStream staging = export.CreateStaging())
            {
                await staging.WriteAsync(new byte[] { 7, 8, 9 });
                staging.Flush(flushToDisk: true);
            }

            Directory.Move(exportRoot, moved);
            Assert.Throws<InvalidOperationException>(() => export.Publish());
            Assert.False(File.Exists(Path.Combine(moved, "blocked.bin")));
        }

        Assert.Empty(Directory.EnumerateFiles(moved, ".praxis-export-*.part"));
    }

    [Fact]
    public async Task PublicationRefusesDestinationSymlinkCreatedAfterStaging()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string output = Path.Combine(exportRoot, "already-linked.bin");
        string protectedFile = Path.Combine(catalogRoot, "original-protection.txt");
        File.WriteAllBytes(protectedFile, [11, 12]);
        using (OriginalExportDestination export = OriginalExportDestination.Open(output, catalogRoot))
        {
            await using (FileStream staging = export.CreateStaging())
            {
                await staging.WriteAsync(new byte[] { 42, 43 });
                staging.Flush(flushToDisk: true);
            }

            File.CreateSymbolicLink(output, protectedFile);
            IOException error = Assert.Throws<IOException>(() => export.Publish());
            Assert.Contains("already exists", error.Message);
            Assert.Equal(new byte[] { 11, 12 }, File.ReadAllBytes(protectedFile));
        }

        Assert.Empty(Directory.EnumerateFiles(exportRoot, ".praxis-export-*.part"));
    }

    [Fact]
    public void PosixStagingFilesAreNotReadableByOtherUsers()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using (OriginalExportDestination export =
               OriginalExportDestination.Open(Path.Combine(exportRoot, "private.bin"), catalogRoot))
        {
            using FileStream staging = export.CreateStaging();
            string path = Assert.Single(Directory.EnumerateFiles(exportRoot, ".praxis-export-*.part"));
            UnixFileMode mode = File.GetUnixFileMode(path);
            Assert.Equal(UnixFileMode.None, mode & ~(UnixFileMode.UserRead | UnixFileMode.UserWrite));
        }

        Assert.Empty(Directory.EnumerateFiles(exportRoot, ".praxis-export-*.part"));
    }

    [Fact]
    public async Task IndependentSourcesWithIdenticalContentExportWithoutSharingSourceIdentity()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(catalogRoot);
        CapturedOriginal first = await Capture(catalog, "first.bin", [9, 1, 4, 7], "first");
        CapturedOriginal second = await Capture(catalog, "second.bin", [9, 1, 4, 7], "second");
        Assert.NotEqual(first.ItemId, second.ItemId);
        Assert.Equal(first.Sha256, second.Sha256);

        OriginalExport firstExport = await catalog.Originals.ExportAsync(first.ItemId, 1,
            Path.Combine(exportRoot, "first-copy.bin"));
        OriginalExport secondExport = await catalog.Originals.ExportAsync(second.ItemId, 1,
            Path.Combine(exportRoot, "second-copy.bin"));
        Assert.Equal(first.ItemId, firstExport.ItemId);
        Assert.Equal(second.ItemId, secondExport.ItemId);
        Assert.Equal(new byte[] { 9, 1, 4, 7 }, File.ReadAllBytes(Path.Combine(exportRoot, "first-copy.bin")));
        Assert.Equal(new byte[] { 9, 1, 4, 7 }, File.ReadAllBytes(Path.Combine(exportRoot, "second-copy.bin")));
    }

    [Fact]
    public async Task ConcurrentExportsCannotReplaceAnAlreadyPublishedDestination()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(catalogRoot);
        byte[] firstBytes = Enumerable.Repeat((byte)41, 500_000).ToArray();
        byte[] secondBytes = Enumerable.Repeat((byte)42, 500_000).ToArray();
        CapturedOriginal first = await Capture(catalog, "first.bin", firstBytes, "one");
        CapturedOriginal second = await Capture(catalog, "second.bin", secondBytes, "two");
        string destination = Path.Combine(exportRoot, "winner.bin");

        async Task<bool> TryExport(CapturedOriginal source)
        {
            try
            {
                await catalog.Originals.ExportAsync(source.ItemId, source.Revision, destination);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
        }

        bool[] completed = await Task.WhenAll(TryExport(first), TryExport(second));
        Assert.Single(completed, success => success);
        byte[] output = File.ReadAllBytes(destination);
        Assert.True(output.SequenceEqual(firstBytes) || output.SequenceEqual(secondBytes));
        Assert.Empty(Directory.EnumerateFiles(exportRoot, ".praxis-export-*.part"));
        await catalog.Originals.VerifyAsync(first.ItemId);
        await catalog.Originals.VerifyAsync(second.ItemId);
    }

    private async Task<CapturedOriginal> Capture(
        SqliteCatalog catalog, string name, byte[] bytes, string key)
    {
        using MemoryStream stream = new(bytes);
        return await catalog.Originals.CaptureFileAsync(Path.Combine(workspace, "missing", name),
            stream, 1_000_000, key);
    }

    public void Dispose()
    {
        if (Directory.Exists(workspace))
        {
            Directory.Delete(workspace, recursive: true);
        }
    }
}
