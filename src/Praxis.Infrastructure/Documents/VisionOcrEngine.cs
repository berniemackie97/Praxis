using System.Globalization;
using System.Text.Json;
using Praxis.Application.Documents.Ocr;
using Praxis.Application.Inference;
using Praxis.Infrastructure.Inference.Subscription;

namespace Praxis.Infrastructure.Documents;

/// <summary>
/// On-device OCR through the bundled <c>praxis-vision</c> helper (Apple Vision and PDFKit). The
/// content is written to an owner-only temporary file, the helper runs without a shell with a
/// time budget proportional to the page bound, its output is size-limited JSON, and the file is
/// deleted afterwards. Nothing leaves the Mac.
/// </summary>
public sealed class VisionOcrEngine : IOcrEngine
{
    public const string HelperName = "praxis-vision";
    private static readonly TimeSpan BaseBudget = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PageBudget = TimeSpan.FromSeconds(8);

    private readonly string? helper;

    public VisionOcrEngine(string? helperPath = null)
    {
        string candidate = helperPath ?? Environment.GetEnvironmentVariable("PRAXIS_VISION_HELPER")
            ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", HelperName));
        helper = OperatingSystem.IsMacOS() && File.Exists(candidate) ? candidate : null;
    }

    public bool IsAvailable => helper is not null;

    public async Task<OcrResult> RecognizeAsync(byte[] content, OcrSource source, int maximumPages, CancellationToken cancellationToken)
    {
        if (helper is null || !OperatingSystem.IsMacOS())
        {
            return OcrResult.Failed("ocr_unavailable");
        }

        string directory = Path.Combine(Path.GetTempPath(), "praxis-ocr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            string file = Path.Combine(directory, source == OcrSource.Pdf ? "input.pdf" : "input.image");
            await using (FileStream stream = new(file, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
            }))
            {
                await stream.WriteAsync(content, cancellationToken);
            }

            string[] arguments = ["ocr", "--kind", source == OcrSource.Pdf ? "pdf" : "image", "--max-pages",
                maximumPages.ToString(CultureInfo.InvariantCulture), file];
            CliResult result = await CliProcess.RunAsync(helper, arguments, null, directory, BaseBudget + PageBudget * maximumPages, cancellationToken);
            return Parse(result.Stdout) ?? OcrResult.Failed(result.ExitCode == 0 ? "ocr_invalid_output" : "ocr_failed");
        }
        catch (InferenceException exception)
        {
            return OcrResult.Failed(exception.Failure == InferenceFailure.Timeout ? "ocr_timeout" : "ocr_failed");
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // A leftover owner-only temporary directory is removed by the system later.
            }
        }
    }

    public static OcrResult? Parse(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.String)
            {
                return OcrResult.Failed("ocr_" + error.GetString());
            }

            return root.TryGetProperty("pages", out JsonElement pages) && pages.ValueKind == JsonValueKind.Array
                ? OcrResult.Recognized(pages.EnumerateArray().Select(page => page.GetString() ?? string.Empty).ToArray())
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
