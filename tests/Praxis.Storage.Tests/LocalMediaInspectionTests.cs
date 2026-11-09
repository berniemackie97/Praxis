using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Praxis.Storage;
using Xunit;

namespace Praxis.Storage.Tests;

public sealed class LocalMediaInspectionTests : IDisposable
{
    private readonly string workspace = Path.Combine(
        Path.GetTempPath(), "praxis-media-" + Guid.NewGuid().ToString("N"));
    private readonly string dataRoot;

    public LocalMediaInspectionTests()
    {
        dataRoot = Path.Combine(workspace, "data");
        Directory.CreateDirectory(dataRoot);
        if (!OperatingSystem.IsWindows())
        {
            var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.UserExecute;
            File.SetUnixFileMode(workspace, mode);
            File.SetUnixFileMode(dataRoot, mode);
        }
    }

    [Fact]
    public async Task VerifiedMp4HeaderReturnsOriginalIdentityAndExactMovieDurationAfterRestart()
    {
        var contents = FastStartMovie();
        CapturedOriginal original;
        using (var catalog = LocalCatalog.Open(dataRoot))
        {
            original = await Capture(catalog, "tutorial.mp4", contents, "video");
            var inspected = await catalog.InspectMediaAsync(original.SourceId);
            Assert.Equal("header_recognized", inspected.Outcome);
            Assert.Equal("iso-bmff", inspected.ContainerFamily);
            Assert.Equal(12_000L, inspected.DurationMilliseconds);
            Assert.Equal("media-header-v1", inspected.Producer);
            Assert.Equal(contents.LongLength, inspected.OriginalBytes);
            Assert.Equal(original.SourceId, inspected.SourceId);
            Assert.Equal(original.ItemId, inspected.ItemId);
            Assert.Equal(original.Revision, inspected.Revision);
            Assert.Equal(original.Sha256, inspected.SourceSha256);
            Assert.Null(inspected.ReasonCode);
            await catalog.VerifyOriginalAsync(original.ItemId);
        }

        using var reopened = LocalCatalog.Open(dataRoot);
        var repeated = await reopened.InspectMediaAsync(original.SourceId);
        Assert.Equal(original.Sha256, repeated.SourceSha256);
        Assert.Equal(12_000L, repeated.DurationMilliseconds);
    }

    [Fact]
    public async Task InspectionRefusesMalformedMediaWithoutGuessingAPlayableContainer()
    {
        using var catalog = LocalCatalog.Open(dataRoot);
        var fakeVideo = await Capture(catalog, "fake.mp4",
            Encoding.UTF8.GetBytes("not an mp4 video"), "fake");
        var unsupported = await catalog.InspectMediaAsync(fakeVideo.SourceId);
        Assert.Equal("unsupported", unsupported.Outcome);
        Assert.Null(unsupported.ContainerFamily);
        Assert.Null(unsupported.DurationMilliseconds);
        Assert.Equal("unsupported_signature", unsupported.ReasonCode);
        Assert.Equal(fakeVideo.Sha256, unsupported.SourceSha256);

        var ebml = await Capture(catalog, "source.webm",
            [0x1a, 0x45, 0xdf, 0xa3, 0x80, 0x00], "ebml");
        var recognized = await catalog.InspectMediaAsync(ebml.SourceId);
        Assert.Equal("header_recognized", recognized.Outcome);
        Assert.Equal("ebml", recognized.ContainerFamily);
        Assert.Null(recognized.DurationMilliseconds);

        var mismatched = await Capture(catalog, "forged.webm", FastStartMovie(), "mismatch");
        Assert.Equal("unsupported", (await catalog.InspectMediaAsync(
            mismatched.SourceId)).Outcome);

        var text = await Capture(catalog, "not-video.txt", FastStartMovie(), "text");
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await catalog.InspectMediaAsync(text.SourceId));
    }

    [Fact]
    public async Task InspectionRejectsCorruptRevokedAndSupersededOriginals()
    {
        using var catalog = LocalCatalog.Open(dataRoot);
        var original = await Capture(catalog, "changed.mp4", FastStartMovie(), "original");
        var first = await catalog.InspectMediaAsync(original.SourceId);
        var changed = await Capture(catalog, "changed.mp4",
            Encoding.ASCII.GetBytes("no longer valid media"), "changed");
        Assert.Equal(original.ItemId, changed.ItemId);
        Assert.Equal(original.Revision + 1, changed.Revision);
        var latest = await catalog.InspectMediaAsync(original.SourceId);
        Assert.Equal(changed.Sha256, latest.SourceSha256);
        Assert.Equal(changed.Revision, latest.Revision);
        Assert.Equal("unsupported", latest.Outcome);
        Assert.NotEqual(first.SourceSha256, latest.SourceSha256);

        Assert.True(catalog.RevokeSource(original.SourceId));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await catalog.InspectMediaAsync(original.SourceId));

        var corrupt = await Capture(catalog, "damaged.mp4", FastStartMovie(), "corrupt");
        var objectPath = Path.Combine(dataRoot, "objects", corrupt.Sha256[..2],
            corrupt.Sha256[2..4], corrupt.Sha256);
        File.WriteAllBytes(objectPath, Encoding.ASCII.GetBytes("tampered"));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await catalog.InspectMediaAsync(corrupt.SourceId));
    }

    [Fact]
    public async Task DesktopInspectionReturnsGroundedMetadataAndDeniesInvalidRequests()
    {
        using var catalog = LocalCatalog.Open(dataRoot);
        var video = await Capture(catalog, "lesson.mp4", FastStartMovie(), "video");
        var protocol = new LocalDesktopProtocol(catalog);
        using (var response = JsonDocument.Parse(await protocol.HandleAsync($$"""
            {"requestId":"inspect","action":"mediaInspection","sourceId":"{{video.SourceId:D}}"}
            """)))
        {
            Assert.True(response.RootElement.GetProperty("ok").GetBoolean());
            var media = response.RootElement.GetProperty("data").GetProperty("media");
            Assert.Equal(video.Sha256, media.GetProperty("sourceSha256").GetString());
            Assert.Equal("header_recognized", media.GetProperty("outcome").GetString());
            Assert.Equal(12_000L, media.GetProperty("durationMilliseconds").GetInt64());
            Assert.Equal(video.SourceId.ToString("D"), media.GetProperty("sourceId").GetString());
        }

        using (var badId = JsonDocument.Parse(await protocol.HandleAsync(
            """{"requestId":"bad","action":"mediaInspection","sourceId":"wrong"}""")))
        {
            Assert.False(badId.RootElement.GetProperty("ok").GetBoolean());
        }

        Assert.True(catalog.RevokeSource(video.SourceId));
        using var denied = JsonDocument.Parse(await protocol.HandleAsync($$"""
            {"requestId":"revoked","action":"mediaInspection","sourceId":"{{video.SourceId:D}}"}
            """));
        Assert.False(denied.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task CancelledInspectionDoesNotExposeIncompleteHeaderEvidence()
    {
        using var catalog = LocalCatalog.Open(dataRoot);
        var video = await Capture(catalog, "lesson.mp4", FastStartMovie(), "video");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            catalog.InspectMediaAsync(video.SourceId, cancelled.Token));
    }

    [Fact]
    public async Task MetadataVerificationReadsBeyondTheBoundedHeaderAndHonorsByteCeilings()
    {
        using var catalog = LocalCatalog.Open(dataRoot);
        var bytes = new byte[70_000];
        FastStartMovie().CopyTo(bytes, 0);
        var video = await Capture(catalog, "large-fixture.mp4", bytes, "large");
        var store = new OriginalFileStore(dataRoot);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.ReadVerifiedPrefixAsync(
                new OriginalObject(video.Sha256, bytes.Length), 64,
                bytes.Length - 1, CancellationToken.None));

        var first = await catalog.InspectMediaAsync(video.SourceId);
        Assert.Equal("header_recognized", first.Outcome);
        Assert.Equal(12_000L, first.DurationMilliseconds);

        var objectPath = Path.Combine(dataRoot, "objects", video.Sha256[..2],
            video.Sha256[2..4], video.Sha256);
        bytes[66_000] ^= 0xff;
        File.WriteAllBytes(objectPath, bytes);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await catalog.InspectMediaAsync(video.SourceId));
    }

    [Fact]
    public void ContainerParserDoesNotInventMovieDurationFromIncompleteHeaders()
    {
        var header = FastStartMovie();
        var wrongTimescale = (byte[])header.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(wrongTimescale.AsSpan(44, 4), 0);
        Assert.Null(LocalMediaHeaderParser.Read(wrongTimescale, ".mp4").DurationMilliseconds);

        var unsupported = (byte[])header.Clone();
        unsupported[4] = 0xff;
        Assert.Null(LocalMediaHeaderParser.Read(unsupported, ".mp4").Container);

        var truncated = header.AsSpan(0, 16);
        var recognized = LocalMediaHeaderParser.Read(truncated, ".mp4");
        Assert.Equal("iso-bmff", recognized.Container);
        Assert.Null(recognized.DurationMilliseconds);
    }

    private async Task<CapturedOriginal> Capture(
        LocalCatalog catalog, string filename, byte[] content, string requestKey)
    {
        using var source = new MemoryStream(content);
        return await catalog.CaptureFileAsync(
            Path.Combine(workspace, "incoming", filename), source,
            content.Length + 1, requestKey);
    }

    private static byte[] FastStartMovie()
    {
        var bytes = new byte[52];
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0, 4), 16);
        "ftyp"u8.CopyTo(bytes.AsSpan(4, 4));
        "isom"u8.CopyTo(bytes.AsSpan(8, 4));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16, 4), 36);
        "moov"u8.CopyTo(bytes.AsSpan(20, 4));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(24, 4), 28);
        "mvhd"u8.CopyTo(bytes.AsSpan(28, 4));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(44, 4), 1_000);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(48, 4), 12_000);
        return bytes;
    }

    public void Dispose()
    {
        if (Directory.Exists(workspace))
        {
            Directory.Delete(workspace, recursive: true);
        }
    }
}
