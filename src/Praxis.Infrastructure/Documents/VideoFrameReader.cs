using System.Security.Cryptography;
using System.Text.Json;
using Praxis.Application.Links;
using Praxis.Application.Media;
using Praxis.Infrastructure.Inference.Subscription;

namespace Praxis.Infrastructure.Documents;

public sealed class VideoFrameReader : IVideoFrameReader
{
    private readonly string? helper;
    public VideoFrameReader(string? helperPath = null)
    {
        string path = helperPath ?? Environment.GetEnvironmentVariable("PRAXIS_VISION_HELPER")
            ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", VisionOcrEngine.HelperName));
        helper = OperatingSystem.IsMacOS() && File.Exists(path) ? path : null;
    }
    public bool IsAvailable => helper is not null;
    public async Task<IReadOnlyList<WebVideoFrame>> ReadAsync(string path, int maximumFrames, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumFrames, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumFrames, 12);
        if (helper is null)
        {
            return [];
        }
        CliResult result = await CliProcess.RunAsync(helper, ["video", "--max-frames", maximumFrames.ToString(System.Globalization.CultureInfo.InvariantCulture), path],
                    null, Path.GetDirectoryName(path)!, TimeSpan.FromMinutes(2), cancellationToken);
        if (result.ExitCode != 0)
        {
            return [];
        }
        return Parse(result.Stdout, maximumFrames);
    }

    public static IReadOnlyList<WebVideoFrame> Parse(string json, int maximumFrames)
    {
        try { return ParseFrames(json, maximumFrames); }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException or KeyNotFoundException or OverflowException)
        {
            return [];
        }
    }

    private static IReadOnlyList<WebVideoFrame> ParseFrames(string json, int maximumFrames)
    {
        if (maximumFrames is < 1 or > 12)
        {
            return [];
        }
        using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 5 });
        if (!document.RootElement.TryGetProperty("frames", out JsonElement entries) || entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() > maximumFrames)
        {
            return [];
        }
        List<WebVideoFrame> frames = [];
        foreach (JsonElement entry in entries.EnumerateArray())
        {
            double seconds = entry.GetProperty("seconds").GetDouble();
            string text = entry.GetProperty("text").GetString() ?? "", image = entry.GetProperty("imageBase64").GetString() ?? "";
            if (!double.IsFinite(seconds) || seconds is < 0 or >= 86_400 || text.Length > 8_000 || image.Length is < 4 or > 140_000)
            {
                return [];
            }
            byte[] bytes = Convert.FromBase64String(image);
            if (bytes.Length > 100_000 || bytes.Length < 3 || bytes[0] != 0xff || bytes[1] != 0xd8 || bytes[2] != 0xff)
            {
                return [];
            }
            frames.Add(new WebVideoFrame(seconds, text, image, Convert.ToHexStringLower(SHA256.HashData(bytes))));
        }
        return frames.OrderBy(frame => frame.Seconds).ToArray();
    }
}
