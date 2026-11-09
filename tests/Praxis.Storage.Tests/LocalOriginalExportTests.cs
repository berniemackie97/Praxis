using System.Security.Cryptography;
using Praxis.Storage;
using Xunit;

namespace Praxis.Storage.Tests;

public sealed class LocalOriginalExportTests : IDisposable
{
    private readonly string workspace = Path.Combine(Path.GetTempPath(),
        "praxis-export-" + Guid.NewGuid().ToString("N"));
    private readonly string catalogRoot;
    private readonly string exportRoot;

    public LocalOriginalExportTests()
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
        var bytes = Enumerable.Range(0, 256_000).Select(x => (byte)(x % 251)).ToArray();
        CapturedOriginal captured;
        using (var catalog = LocalCatalog.Open(catalogRoot))
        {
            captured = await Capture(catalog, "offline.bin", bytes, "one");
            var exported = await catalog.ExportOriginalAsync(
                captured.ItemId, captured.Revision, Path.Combine(exportRoot, "copy.bin"));
            Assert.Equal(captured.ItemId, exported.ItemId);
            Assert.Equal(captured.Sha256, exported.Sha256);
            Assert.Equal(bytes.LongLength, exported.Length);
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(exportRoot, "copy.bin")));
            await catalog.VerifyOriginalAsync(captured.ItemId);
            Assert.Empty(Directory.EnumerateFiles(exportRoot, "*.part"));
        }

        using var reopened = LocalCatalog.Open(catalogRoot);
        await reopened.ExportOriginalAsync(
            captured.ItemId, captured.Revision, Path.Combine(exportRoot, "recovered.bin"));
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(exportRoot, "recovered.bin")));
    }

    [Fact]
    public async Task ExistingDestinationsAreNeverOverwrittenOrDeleted()
    {
        using var catalog = LocalCatalog.Open(catalogRoot);
        var captured = await Capture(catalog, "note.txt", [2, 3, 4], "one");
        var destination = Path.Combine(exportRoot, "keep.txt");
        File.WriteAllBytes(destination, [9, 8, 7]);

        await Assert.ThrowsAsync<IOException>(() =>
            catalog.ExportOriginalAsync(captured.ItemId, 1, destination));
        Assert.Equal(new byte[] { 9, 8, 7 }, File.ReadAllBytes(destination));
        Assert.Empty(Directory.EnumerateFiles(exportRoot, ".praxis-export-*.part"));
        await catalog.VerifyOriginalAsync(captured.ItemId);
    }

    [Fact]
    public async Task StaleRevokedAndUnknownItemsCannotExport()
    {
        using var catalog = LocalCatalog.Open(catalogRoot);
        var initial = await Capture(catalog, "revised.md", [1], "one");
        await Capture(catalog, "revised.md", [2], "two");
        await Assert.ThrowsAsync<ArgumentException>(() =>
            catalog.ExportOriginalAsync(Guid.Empty, 1,
                Path.Combine(exportRoot, "invalid.md")));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalog.ExportOriginalAsync(initial.ItemId, 1,
                Path.Combine(exportRoot, "outdated.md")));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalog.ExportOriginalAsync(Guid.NewGuid(), 1,
                Path.Combine(exportRoot, "unknown.md")));

        Assert.True(catalog.RevokeSource(initial.SourceId));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalog.ExportOriginalAsync(initial.ItemId, 2,
                Path.Combine(exportRoot, "revoked.md")));

        Assert.Empty(Directory.EnumerateFileSystemEntries(exportRoot));
        await catalog.VerifyOriginalAsync(initial.ItemId);
    }

    [Fact]
    public async Task CorruptOriginalFailsVerificationWithoutPublishingAFile()
    {
        using var catalog = LocalCatalog.Open(catalogRoot);
        var captured = await Capture(catalog, "file.bin", [1, 2, 3, 4], "one");
        var path = Path.Combine(catalogRoot, "objects",
            captured.Sha256[..2], captured.Sha256[2..4], captured.Sha256);
        File.WriteAllBytes(path, [1, 2, 3, 5]);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            catalog.ExportOriginalAsync(captured.ItemId, 1,
                Path.Combine(exportRoot, "unverified.bin")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(exportRoot));
        Assert.Equal(new byte[] { 1, 2, 3, 5 }, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task ExportRejectsCatalogPathsAndSymbolicLinkRedirection()
    {
        using var catalog = LocalCatalog.Open(catalogRoot);
        var captured = await Capture(catalog, "file.bin", [1, 2, 3], "one");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalog.ExportOriginalAsync(captured.ItemId, 1,
                Path.Combine(catalogRoot, "illegal.bin")));
        Assert.False(File.Exists(Path.Combine(catalogRoot, "illegal.bin")));

        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var linked = Path.Combine(workspace, "linked");
        Directory.CreateSymbolicLink(linked, catalogRoot);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalog.ExportOriginalAsync(captured.ItemId, 1,
                Path.Combine(linked, "redirected.bin")));
        Assert.False(File.Exists(Path.Combine(catalogRoot, "redirected.bin")));
    }

    [Fact]
    public async Task CancellationLeavesOnlyOriginalBytes()
    {
        using var catalog = LocalCatalog.Open(catalogRoot);
        var captured = await Capture(catalog, "original.bin", new byte[300_000], "one");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            catalog.ExportOriginalAsync(captured.ItemId, 1,
                Path.Combine(exportRoot, "cancelled.bin"), cancellation.Token));
        Assert.Empty(Directory.EnumerateFileSystemEntries(exportRoot));
        await catalog.VerifyOriginalAsync(captured.ItemId);
    }

    [Fact]
    public async Task EmptyOriginalCanBeExportedAndVerified()
    {
        using var catalog = LocalCatalog.Open(catalogRoot);
        var captured = await Capture(catalog, "empty.txt", [], "one");
        var destination = Path.Combine(exportRoot, "empty.txt");

        var exported = await catalog.ExportOriginalAsync(captured.ItemId, 1, destination);
        Assert.Equal(0, exported.Length);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Array.Empty<byte>())), exported.Sha256);
        Assert.Empty(File.ReadAllBytes(destination));
        await catalog.VerifyOriginalAsync(captured.ItemId);
    }

    private async Task<CapturedOriginal> Capture(
        LocalCatalog catalog, string name, byte[] bytes, string key)
    {
        using var stream = new MemoryStream(bytes);
        return await catalog.CaptureFileAsync(Path.Combine(workspace, "missing", name),
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
