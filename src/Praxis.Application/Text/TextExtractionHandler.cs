using Praxis.Application.Documents;
using Praxis.Application.Originals;
using Praxis.Application.Processing;
using Praxis.Domain.Processing;
using Praxis.Domain.Understanding;

namespace Praxis.Application.Text;

/// <summary>
/// The read stage for every captured file: the reader registry turns the verified original into
/// normalized text (and records how it was read) for search, segments, citations and the intake
/// stages. Only files no reader recognizes close as unsupported, and they are reopened as soon as
/// a reader for their extension exists.
/// </summary>
public sealed class TextExtractionHandler(
    ITextDerivativeRepository derivatives, IOriginalStore originals, IProcessingQueue processing, DocumentReaders readers) : IProcessingHandler
{
    public string ProcessorKey => ProcessorKeys.SourceText;
    public TimeSpan LeaseDuration => TimeSpan.FromMinutes(10);
    public int MaxConcurrency => 2;

    public bool TryReopenWork() => derivatives.RequeueNewlySupported(readers.Extensions);

    public async Task ProcessAsync(ProcessingLease lease, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LeasedTextSource? source = derivatives.ReadLeasedSource(lease);
        if (source is null)
        {
            return;
        }

        if (source.SourceKind != "file")
        {
            derivatives.CommitResult(lease, null, "unsupported_source");
            return;
        }

        if (source.Original.Length > DocumentLimits.MaximumOriginalBytes)
        {
            derivatives.CommitResult(lease, null, "document_too_large");
            return;
        }

        try
        {
            byte[] original = await originals.ReadVerifiedBytesAsync(source.Original, DocumentLimits.MaximumOriginalBytes, cancellationToken);
            ReadOutcome outcome = await readers.ReadAsync(new DocumentInput(Path.GetFileName(source.Locator), original, "src"), cancellationToken);
            if (processing.Renew(lease, LeaseDuration) is null)
            {
                return;
            }

            if (outcome.Document is not { } document)
            {
                derivatives.CommitResult(lease, null, outcome.ReasonCode ?? "unsupported_format");
                return;
            }

            string? title = document.Title is { Length: > 0 } value
                ? value.Length <= DocumentFacts.MaximumTitleLength ? value : value[..DocumentFacts.MaximumTitleLength]
                : null;
            derivatives.CommitResult(lease, document.Text, null, new DocumentFacts(document.Format, document.Pages, title, document.Truncated));
        }
        catch (InvalidDataException)
        {
            processing.Finish(lease, ProcessingCompletion.Failed, "corrupt_original");
        }
        catch (EndOfStreamException)
        {
            processing.Finish(lease, ProcessingCompletion.Failed, "corrupt_original");
        }
        catch (IOException)
        {
            processing.Finish(lease, ProcessingCompletion.Failed, "source_io");
        }
        catch (UnauthorizedAccessException)
        {
            processing.Finish(lease, ProcessingCompletion.Failed, "source_access");
        }
    }
}
