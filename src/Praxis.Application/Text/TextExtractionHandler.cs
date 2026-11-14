using Praxis.Application.Documents;
using Praxis.Application.Media;
using Praxis.Application.Originals;
using Praxis.Application.Processing;
using Praxis.Domain.Media;
using Praxis.Domain.Processing;
using Praxis.Domain.Understanding;

namespace Praxis.Application.Text;

/// <summary>
/// The read stage for every captured file: the reader registry turns the verified original into
/// normalized text (and records how it was read) for search, segments, citations and the intake
/// stages. Audio and video are transcribed on device, and the transcript's timing is stored with
/// its text. Only files no reader recognizes close as unsupported, and they are reopened as soon as
/// a reader for their extension exists.
/// </summary>
public sealed class TextExtractionHandler(
    ITextDerivativeRepository derivatives, IOriginalStore originals, IProcessingQueue processing, DocumentReaders readers,
    ITranscriptionEngine? transcriber = null) : IProcessingHandler
{
    private static readonly TimeSpan RenewalInterval = TimeSpan.FromMinutes(3);

    public string ProcessorKey => ProcessorKeys.SourceText;
    public TimeSpan LeaseDuration => TimeSpan.FromMinutes(10);
    public int MaxConcurrency => 2;

    public bool TryReopenWork() => derivatives.RequeueNewlySupported(transcriber is { IsAvailable: true }
        ? [.. readers.Extensions, .. MediaFormats.TranscribableExtensions]
        : readers.Extensions);

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

        string extension = Path.GetExtension(source.Locator).ToLowerInvariant();
        if (transcriber is { IsAvailable: true } engine && MediaFormats.IsTranscribable(extension))
        {
            await TranscribeAsync(lease, source, extension, engine, cancellationToken);
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

    /// <summary>
    /// The time budget for a file of this size. Recognition runs much faster than real time; the
    /// budget assumes a low 32 kbit/s bitrate so long recordings are never cut short.
    /// </summary>
    public static TimeSpan TranscriptionBudget(long bytes) =>
        TimeSpan.FromSeconds(Math.Clamp((bytes / 4_000.0 / 2) + 300, 300, 4 * 3_600));

    private async Task TranscribeAsync(ProcessingLease lease, LeasedTextSource source, string extension, ITranscriptionEngine engine,
        CancellationToken cancellationToken)
    {
        string path;
        try
        {
            path = await originals.MaterializeAsync(source.Original, extension, cancellationToken);
        }
        catch (InvalidDataException)
        {
            processing.Finish(lease, ProcessingCompletion.Failed, "corrupt_original");
            return;
        }
        catch (IOException)
        {
            processing.Finish(lease, ProcessingCompletion.Failed, "source_io");
            return;
        }

        TranscriptionResult result;
        using (CancellationTokenSource leaseLost = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            // Long recordings outlive one lease; keep it while the transcriber runs and stop if it is lost.
            Task renewal = KeepLeaseAsync(lease, leaseLost);
            try
            {
                result = await engine.TranscribeAsync(path, TranscriptionBudget(source.Original.Length), leaseLost.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return;
            }
            finally
            {
                await leaseLost.CancelAsync();
                await renewal;
                originals.ReleaseMaterialized(path);
            }
        }

        if (processing.Renew(lease, LeaseDuration) is null)
        {
            return;
        }

        switch (result)
        {
            case { Segments: { Count: > 0 } segments }:
                (string text, IReadOnlyList<TimedSpan> timings) = TranscriptText.Compose(segments);
                derivatives.CommitResult(lease, text, null, new DocumentFacts(MediaFormats.Transcript, null, null, false), timings);
                break;
            case { Segments: not null }:
                derivatives.CommitResult(lease, null, "no_speech");
                break;
            case { ReasonCode: "transcription_speech_model_unavailable" or "transcription_unavailable" }:
                throw new ProcessorUnavailableException("transcription_unavailable");
            case { ReasonCode: "transcription_media_unreadable" or "transcription_unsupported_locale" }:
                derivatives.CommitResult(lease, null, result.ReasonCode);
                break;
            default:
                processing.Finish(lease, ProcessingCompletion.Failed, result.ReasonCode ?? "transcription_failed");
                break;
        }
    }

    private async Task KeepLeaseAsync(ProcessingLease lease, CancellationTokenSource leaseLost)
    {
        try
        {
            while (!leaseLost.IsCancellationRequested)
            {
                await Task.Delay(RenewalInterval, leaseLost.Token);
                if (processing.Renew(lease, LeaseDuration) is null)
                {
                    await leaseLost.CancelAsync();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Transcription finished or the worker is stopping.
        }
    }
}
