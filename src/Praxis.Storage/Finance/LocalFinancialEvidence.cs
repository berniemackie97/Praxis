using System.Text;

namespace Praxis.Storage;

internal sealed record BudgetSource(
    Guid SourceId, Guid ItemId, int Revision, string Sha256, string Content);

public sealed partial class LocalFinancialEvidence
{
    private readonly LocalSources sources;
    private readonly OriginalFileStore originals;

    internal LocalFinancialEvidence(LocalSources sources, OriginalFileStore originals)
    {
        this.sources = sources;
        this.originals = originals;
    }

    private async Task<BudgetSource> ReadBudgetSourceAsync(
        Guid sourceId, CancellationToken cancellationToken)
    {
        if (sourceId == Guid.Empty)
        {
            throw new ArgumentException("Choose a captured financial CSV source.", nameof(sourceId));
        }

        var source = sources.CurrentFile(sourceId)
            ?? throw new InvalidOperationException("The selected financial source is not active.");
        if (!Path.GetExtension(source.Locator).Equals(".csv", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Budget analysis requires a selected CSV original.", nameof(sourceId));
        }

        var bytes = await originals.ReadVerifiedBytesAsync(
            new OriginalObject(source.Digest, source.Length),
            MaximumBytes, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        string content;
        try
        {
            var offset = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble)
                ? Encoding.UTF8.Preamble.Length : 0;
            content = new UTF8Encoding(false, true).GetString(bytes, offset, bytes.Length - offset);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("The financial CSV is not valid UTF-8.", exception);
        }

        if (content.Any(c => (c < 0x20 && c is not ('\n' or '\r' or '\t')) || c == 0x7f))
        {
            throw new InvalidDataException("The selected financial CSV contains control characters.");
        }

        AssertBudgetSourceCurrent(sourceId, source.ItemId, source.Revision, source.Digest);
        return new BudgetSource(sourceId, source.ItemId, source.Revision, source.Digest, content);
    }

    private void AssertBudgetSourceCurrent(
        Guid sourceId, Guid itemId, int revision, string sha256)
    {
        var current = sources.CurrentFile(sourceId);
        if (current is null || current.ItemId != itemId ||
            current.Revision != revision || current.Digest != sha256)
        {
            throw new InvalidOperationException("The financial source changed or was revoked.");
        }
    }
}
