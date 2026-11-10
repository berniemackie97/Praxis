using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Praxis.Storage.Catalog;
using Praxis.Storage.Desktop;
using Praxis.Storage.Media;
using Praxis.Storage.Originals;
using Praxis.Storage.Tests.Support;
using Xunit;

namespace Praxis.Storage.Tests.Evidence;

public sealed class LocalMediaInspectionTests : IDisposable
{
    private readonly string workspace = Path.Combine(
        TestPaths.Temp, "praxis-media-" + Guid.NewGuid().ToString("N"));
    private readonly string dataRoot;

    public LocalMediaInspectionTests()
    {
        dataRoot = Path.Combine(workspace, "data");
        Directory.CreateDirectory(dataRoot);
        if (!OperatingSystem.IsWindows())
        {
            UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite |
                                UnixFileMode.UserExecute;
            File.SetUnixFileMode(workspace, mode);
            File.SetUnixFileMode(dataRoot, mode);
        }
    }

    [Fact]
    public async Task VerifiedMp4HeaderReturnsOriginalIdentityAndExactMovieDurationAfterRestart()
    {
        byte[] contents = FastStartMovie();
        CapturedOriginal original;
        using (LocalCatalog catalog = LocalCatalog.Open(dataRoot))
        {
            original = await Capture(catalog, "tutorial.mp4", contents, "video");
            LocalMediaInspection inspected = await catalog.Media.InspectAsync(original.SourceId);
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
            await catalog.Originals.VerifyAsync(original.ItemId);
        }

        using LocalCatalog reopened = LocalCatalog.Open(dataRoot);
        LocalMediaInspection repeated = await reopened.Media.InspectAsync(original.SourceId);
        Assert.Equal(original.Sha256, repeated.SourceSha256);
        Assert.Equal(12_000L, repeated.DurationMilliseconds);
    }

    [Fact]
    public async Task InspectionRefusesMalformedMediaWithoutGuessingAPlayableContainer()
    {
        using LocalCatalog catalog = LocalCatalog.Open(dataRoot);
        CapturedOriginal fakeVideo = await Capture(catalog, "fake.mp4",
            Encoding.UTF8.GetBytes("not an mp4 video"), "fake");
        LocalMediaInspection unsupported = await catalog.Media.InspectAsync(fakeVideo.SourceId);
        Assert.Equal("unsupported", unsupported.Outcome);
        Assert.Null(unsupported.ContainerFamily);
        Assert.Null(unsupported.DurationMilliseconds);
        Assert.Equal("unsupported_signature", unsupported.ReasonCode);
        Assert.Equal(fakeVideo.Sha256, unsupported.SourceSha256);

        CapturedOriginal ebml = await Capture(catalog, "source.webm",
            [0x1a, 0x45, 0xdf, 0xa3, 0x87, 0x42, 0x82, 0x84,
                0x77, 0x65, 0x62, 0x6d], "ebml");
        LocalMediaInspection recognized = await catalog.Media.InspectAsync(ebml.SourceId);
        Assert.Equal("header_recognized", recognized.Outcome);
        Assert.Equal("ebml", recognized.ContainerFamily);
        Assert.Null(recognized.DurationMilliseconds);

        CapturedOriginal invalidEbml = await Capture(catalog, "false.webm",
            [0x1a, 0x45, 0xdf, 0xa3, 0x80, 0x00], "false-ebml");
        Assert.Equal("unsupported",
            (await catalog.Media.InspectAsync(invalidEbml.SourceId)).Outcome);

        CapturedOriginal mismatched = await Capture(catalog, "forged.webm", FastStartMovie(), "mismatch");
        Assert.Equal("unsupported", (await catalog.Media.InspectAsync(
            mismatched.SourceId)).Outcome);

        CapturedOriginal text = await Capture(catalog, "not-video.txt", FastStartMovie(), "text");
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await catalog.Media.InspectAsync(text.SourceId));
    }

    [Fact]
    public async Task InspectionRejectsCorruptRevokedAndSupersededOriginals()
    {
        using LocalCatalog catalog = LocalCatalog.Open(dataRoot);
        CapturedOriginal original = await Capture(catalog, "changed.mp4", FastStartMovie(), "original");
        LocalMediaInspection first = await catalog.Media.InspectAsync(original.SourceId);
        CapturedOriginal changed = await Capture(catalog, "changed.mp4",
            Encoding.ASCII.GetBytes("no longer valid media"), "changed");
        Assert.Equal(original.ItemId, changed.ItemId);
        Assert.Equal(original.Revision + 1, changed.Revision);
        LocalMediaInspection latest = await catalog.Media.InspectAsync(original.SourceId);
        Assert.Equal(changed.Sha256, latest.SourceSha256);
        Assert.Equal(changed.Revision, latest.Revision);
        Assert.Equal("unsupported", latest.Outcome);
        Assert.NotEqual(first.SourceSha256, latest.SourceSha256);

        Assert.True(catalog.Sources.Revoke(original.SourceId));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await catalog.Media.InspectAsync(original.SourceId));

        CapturedOriginal corrupt = await Capture(catalog, "damaged.mp4", FastStartMovie(), "corrupt");
        string objectPath = Path.Combine(dataRoot, "objects", corrupt.Sha256[..2],
            corrupt.Sha256[2..4], corrupt.Sha256);
        File.WriteAllBytes(objectPath, Encoding.ASCII.GetBytes("tampered"));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await catalog.Media.InspectAsync(corrupt.SourceId));
    }

    [Fact]
    public async Task DesktopInspectionReturnsGroundedMetadataAndDeniesInvalidRequests()
    {
        using LocalCatalog catalog = LocalCatalog.Open(dataRoot);
        CapturedOriginal video = await Capture(catalog, "lesson.mp4", FastStartMovie(), "video");
        LocalDesktopProtocol protocol = new(catalog);
        using (JsonDocument response = JsonDocument.Parse(await protocol.HandleAsync($$"""
                     {"requestId":"inspect","action":"mediaInspection","sourceId":"{{video.SourceId:D}}"}
                     """)))
        {
            Assert.True(response.RootElement.GetProperty("ok").GetBoolean());
            JsonElement media = response.RootElement.GetProperty("data").GetProperty("media");
            Assert.Equal(video.Sha256, media.GetProperty("sourceSha256").GetString());
            Assert.Equal("header_recognized", media.GetProperty("outcome").GetString());
            Assert.Equal(12_000L, media.GetProperty("durationMilliseconds").GetInt64());
            Assert.Equal(video.SourceId.ToString("D"), media.GetProperty("sourceId").GetString());
        }

        using (JsonDocument badId = JsonDocument.Parse(await protocol.HandleAsync(
            """{"requestId":"bad","action":"mediaInspection","sourceId":"wrong"}""")))
        {
            Assert.False(badId.RootElement.GetProperty("ok").GetBoolean());
        }

        Assert.True(catalog.Sources.Revoke(video.SourceId));
        using JsonDocument denied = JsonDocument.Parse(await protocol.HandleAsync($$"""
              {"requestId":"revoked","action":"mediaInspection","sourceId":"{{video.SourceId:D}}"}
              """));
        Assert.False(denied.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task DesktopInspectionRejectsStaleAndInvalidRevisions()
    {
        using LocalCatalog catalog = LocalCatalog.Open(dataRoot);
        CapturedOriginal first = await Capture(catalog, "lesson.mp4", FastStartMovie(), "initial");
        byte[] updatedMovie = FastStartMovie();
        BinaryPrimitives.WriteUInt32BigEndian(updatedMovie.AsSpan(48, 4), 2_000);
        CapturedOriginal revised = await Capture(catalog, "lesson.mp4", updatedMovie, "revision");
        Assert.Equal(first.SourceId, revised.SourceId);
        Assert.Equal(2, revised.Revision);

        LocalDesktopProtocol protocol = new(catalog);
        using JsonDocument stale = JsonDocument.Parse(await protocol.HandleAsync($$"""
              {"requestId":"stale","action":"mediaInspection","sourceId":"{{first.SourceId:D}}","revision":1}
              """));
        Assert.False(stale.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("no longer current", stale.RootElement.GetProperty("error").GetString());

        using JsonDocument invalid = JsonDocument.Parse(await protocol.HandleAsync($$"""
              {"requestId":"invalid","action":"mediaInspection","sourceId":"{{first.SourceId:D}}","revision":0}
              """));
        Assert.False(invalid.RootElement.GetProperty("ok").GetBoolean());

        using JsonDocument current = JsonDocument.Parse(await protocol.HandleAsync($$"""
              {"requestId":"current","action":"mediaInspection","sourceId":"{{first.SourceId:D}}","revision":2}
              """));
        Assert.True(current.RootElement.GetProperty("ok").GetBoolean());
        JsonElement media = current.RootElement.GetProperty("data").GetProperty("media");
        Assert.Equal(2, media.GetProperty("revision").GetInt32());
        Assert.Equal(revised.Sha256, media.GetProperty("sourceSha256").GetString());
    }

    [Fact]
    public async Task CancelledInspectionDoesNotExposeIncompleteHeaderEvidence()
    {
        using LocalCatalog catalog = LocalCatalog.Open(dataRoot);
        CapturedOriginal video = await Capture(catalog, "lesson.mp4", FastStartMovie(), "video");
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            catalog.Media.InspectAsync(video.SourceId, cancelled.Token));
    }

    [Fact]
    public async Task MetadataVerificationReadsBeyondTheBoundedHeaderAndHonorsByteCeilings()
    {
        using LocalCatalog catalog = LocalCatalog.Open(dataRoot);
        byte[] bytes = new byte[70_000];
        FastStartMovie().CopyTo(bytes, 0);
        CapturedOriginal video = await Capture(catalog, "large-fixture.mp4", bytes, "large");
        OriginalFileStore store = new(dataRoot);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.ReadVerifiedPrefixAsync(
                new OriginalObject(video.Sha256, bytes.Length), 64,
                bytes.Length - 1, CancellationToken.None));

        LocalMediaInspection first = await catalog.Media.InspectAsync(video.SourceId);
        Assert.Equal("header_recognized", first.Outcome);
        Assert.Equal(12_000L, first.DurationMilliseconds);

        string objectPath = Path.Combine(dataRoot, "objects", video.Sha256[..2],
            video.Sha256[2..4], video.Sha256);
        bytes[66_000] ^= 0xff;
        File.WriteAllBytes(objectPath, bytes);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await catalog.Media.InspectAsync(video.SourceId));
    }

    [Fact]
    public void ContainerParserDoesNotInventMovieDurationFromIncompleteHeaders()
    {
        byte[] header = FastStartMovie();
        byte[] wrongTimescale = (byte[])header.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(wrongTimescale.AsSpan(44, 4), 0);
        Assert.Null(LocalMediaHeaderParser.Read(wrongTimescale, ".mp4").DurationMilliseconds);

        byte[] unknownDuration = (byte[])header.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(unknownDuration.AsSpan(48, 4),
            uint.MaxValue);
        Assert.Null(LocalMediaHeaderParser.Read(unknownDuration, ".mp4")
            .DurationMilliseconds);

        byte[] unsupported = (byte[])header.Clone();
        unsupported[4] = 0xff;
        Assert.Null(LocalMediaHeaderParser.Read(unsupported, ".mp4").Container);

        Span<byte> truncated = header.AsSpan(0, 16);
        (string? Container, long? DurationMilliseconds) recognized = LocalMediaHeaderParser.Read(truncated, ".mp4");
        Assert.Equal("iso-bmff", recognized.Container);
        Assert.Null(recognized.DurationMilliseconds);
    }

    [Fact]
    public void VersionOneIsoMovieHeaderUsesItsSixtyFourBitTimebaseFields()
    {
        byte[] media = new byte[64];
        FastStartMovie().AsSpan(0, 16).CopyTo(media);
        BinaryPrimitives.WriteUInt32BigEndian(media.AsSpan(16, 4), 48);
        "moov"u8.CopyTo(media.AsSpan(20, 4));
        BinaryPrimitives.WriteUInt32BigEndian(media.AsSpan(24, 4), 40);
        "mvhd"u8.CopyTo(media.AsSpan(28, 4));
        media[32] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(media.AsSpan(52, 4), 1_000);
        BinaryPrimitives.WriteUInt64BigEndian(media.AsSpan(56, 8), 25_000);
        Assert.Equal(25_000L, LocalMediaHeaderParser.Read(media, ".mp4")
            .DurationMilliseconds);

        BinaryPrimitives.WriteUInt64BigEndian(media.AsSpan(56, 8),
            ulong.MaxValue);
        Assert.Null(LocalMediaHeaderParser.Read(media, ".mp4").DurationMilliseconds);
    }

    private async Task<CapturedOriginal> Capture(
        LocalCatalog catalog, string filename, byte[] content, string requestKey)
    {
        using MemoryStream source = new(content);
        return await catalog.Originals.CaptureFileAsync(
            Path.Combine(workspace, "incoming", filename), source,
            content.Length + 1, requestKey);
    }

    private static byte[] FastStartMovie()
    {
        byte[] bytes = new byte[52];
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
