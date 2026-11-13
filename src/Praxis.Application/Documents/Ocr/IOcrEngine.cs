namespace Praxis.Application.Documents.Ocr;

/// <summary>
/// On-device text recognition for images and image-only PDF pages. Implementations run locally,
/// never send content off the machine, and bound pages, output and time.
/// </summary>
public interface IOcrEngine
{
    bool IsAvailable { get; }

    Task<OcrResult> RecognizeAsync(byte[] content, OcrSource source, int maximumPages, CancellationToken cancellationToken);
}
