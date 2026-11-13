namespace Praxis.Application.Documents.Ocr;

/// <summary>Recognized text per page, or the bounded reason recognition did not run or failed.</summary>
public sealed record OcrResult(IReadOnlyList<string>? Pages, string? ReasonCode)
{
    public static OcrResult Recognized(IReadOnlyList<string> pages) => new(pages, null);

    public static OcrResult Failed(string reasonCode) => new(null, reasonCode);
}
