using Praxis.Domain.Text;

namespace Praxis.Application.Documents;

/// <summary>Bounds every reader enforces so a hostile or enormous file cannot exhaust the worker.</summary>
public static class DocumentLimits
{
    public const int MaximumOriginalBytes = 64 * 1024 * 1024;
    public const int MaximumTextBytes = TextLimits.MaximumTextBytes;
    public const int MaximumPdfPages = 500;
    public const int MaximumOcrPages = 60;
    public const int MaximumTableRows = 50_000;
    public const int MaximumArchiveEntries = 10_000;
    public const long MaximumArchiveBytes = 256L * 1024 * 1024;
    public const int MaximumCompressionRatio = 200;
    public const int MaximumMimeDepth = 8;
    public const int MaximumMimeParts = 500;
}
