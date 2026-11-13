using System.Text;
using Praxis.Application.Finance.Delimited;
using Praxis.Application.Finance.Statements;
using Praxis.Application.Inference;
using Praxis.Application.Originals;
using Praxis.Application.Policy;
using Praxis.Application.Processing;
using Praxis.Domain.Finance;
using Praxis.Domain.Models;
using Praxis.Domain.Policy;
using Praxis.Domain.Processing;

namespace Praxis.Application.Finance;

/// <summary>
/// Financial intake for candidate files. Published formats are read by code. Delimited exports
/// and PDF statements go up the model ladder: the strong tier returns a column mapping or a
/// statement transcription, code applies and verifies it (rows parse; running balances or
/// statement balances reconcile; every transcribed amount is printed), the strongest tier is
/// asked only when verification fails, and the source category's fallback chain governs when
/// disclosure is not allowed. The local tier's answer is kept only with an arithmetic proof.
/// </summary>
public sealed class FinancialRecordsHandler(
    IFinancialLedgerRepository ledger, IOriginalStore originals, IProcessingQueue processing, IPdfTextReader pdf,
    IntakeLadder ladder, IPolicyRepository policy, TimeProvider clock) : IProcessingHandler
{
    private const int MaximumStatementCharacters = 48_000;
    private const int TranscriptionOutputTokens = 8_192;
    private const int MappingOutputTokens = 1_024;
    private const int RequestContextTokens = 32_768;

    public string ProcessorKey => ProcessorKeys.FinancialRecords;
    public TimeSpan LeaseDuration => TimeSpan.FromMinutes(15);
    public int MaxConcurrency => 1;

    /// <summary>When idle, expire overdue disclosure holds so their jobs continue down the fallback chain.</summary>
    public bool TryReopenWork() => policy.ExpireHolds(clock.GetUtcNow()) > 0;

    public async Task ProcessAsync(ProcessingLease lease, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LeasedFinancialSource? source = ledger.ReadLeasedSource(lease);
        if (source is null)
        {
            return;
        }

        string fileName = Path.GetFileName(source.Locator);
        if (source.SourceKind != "file" ||
            !FinancialFormats.CandidateExtensions.Contains(Path.GetExtension(fileName).ToLowerInvariant(), StringComparer.Ordinal))
        {
            ledger.CommitUnsupported(lease, "unsupported_source");
            return;
        }

        if (source.Original.Length > FinanceLimits.MaximumDocumentBytes)
        {
            ledger.CommitUnsupported(lease, "document_too_large");
            return;
        }

        byte[]? original = await ReadAsync(lease, source, cancellationToken);
        if (original is null)
        {
            return;
        }

        FinancialInput input = new(fileName, "src-" + source.SourceId.ToString("N")[..12]);
        if (FinancialDocumentReader.IsPdf(original))
        {
            await StatementAsync(lease, source, original, input, cancellationToken);
            return;
        }

        string? text = FinancialTextDecoder.Decode(original);
        if (text is null)
        {
            ledger.CommitUnsupported(lease, FinancialParseOutcome.NotFinancial);
            return;
        }

        if (FinancialDocumentReader.PublishedFormat(text) is { } parser)
        {
            FinancialParseOutcome parsed = parser.Parse(text, input);
            if (parsed.Document is { } document)
            {
                Categorize(source.SourceId, SourceCategory.HintOrigin);
                ledger.Commit(lease, document, null, null);
            }
            else
            {
                ledger.CommitUnsupported(lease, parsed.ReasonCode ?? FinancialParseOutcome.NotFinancial);
            }

            return;
        }

        await DelimitedAsync(lease, source, text, input, cancellationToken);
    }

    private async Task StatementAsync(ProcessingLease lease, LeasedFinancialSource source, byte[] original, FinancialInput input, CancellationToken cancellationToken)
    {
        PdfTextResult read = pdf.Read(original, FinanceLimits.MaximumPdfPages);
        if (read.Lines is null)
        {
            ledger.CommitUnsupported(lease, read.ReasonCode ?? "pdf_unreadable");
            return;
        }

        string text = PageText(read.Lines);
        await ladder.TriageAsync(source.SourceId, lease.ItemId, lease.Revision, lease.ProcessorKey, input.FileName, text, cancellationToken);
        TieredRequest request = Request(lease, source, new ChatRequest(StatementExtractionPrompt.Messages(text), StatementExtractionPrompt.Schema,
            TranscriptionOutputTokens, RequestContextTokens));
        LadderResult<FinancialReading> result = await ladder.RunAsync(request,
            (content, _) => ModelStatementReader.Read(content, text, input, StatementExtractionPrompt.Version), cancellationToken);
        Finish(lease, source, result);
    }

    private async Task DelimitedAsync(ProcessingLease lease, LeasedFinancialSource source, string text, FinancialInput input, CancellationToken cancellationToken)
    {
        int limit = FinanceLimits.MaximumTransactionsPerDocument + 32;
        IReadOnlyList<DelimitedRow>? rows = DelimitedReader.DetectDelimiter(text) is { } delimiter ? DelimitedReader.Read(text, delimiter, limit) : null;
        if (rows is null || rows.Count < 2)
        {
            ledger.CommitUnsupported(lease, FinancialParseOutcome.NotFinancial);
            return;
        }

        if (rows.Count >= limit)
        {
            ledger.CommitUnsupported(lease, "too_many_transactions");
            return;
        }

        await ladder.TriageAsync(source.SourceId, lease.ItemId, lease.Revision, lease.ProcessorKey, input.FileName, text, cancellationToken);
        int columns = rows.Max(row => row.Fields.Count);
        TieredRequest request = Request(lease, source, new ChatRequest(
            DelimitedMappingPrompt.Messages(input.FileName, rows.Take(DelimitedMappingPrompt.SampleRows).ToArray()),
            DelimitedMappingPrompt.Schema, MappingOutputTokens, RequestContextTokens));
        LadderResult<FinancialReading> result = await ladder.RunAsync(request, (content, tier) =>
        {
            DelimitedMapping? mapping = DelimitedMappingReader.Read(content, columns, rows.Count);
            if (mapping is null)
            {
                return null;
            }

            if (!mapping.IsFinancial)
            {
                return FinancialReading.NotAFinancialDocument;
            }

            FinancialDocument? document = DelimitedMappingApplier.Apply(mapping, rows, input, requireProof: tier == ModelTiers.Local);
            return document is null ? null : FinancialReading.Of(document);
        }, cancellationToken);
        Finish(lease, source, result);
    }

    private void Finish(ProcessingLease lease, LeasedFinancialSource source, LadderResult<FinancialReading> result)
    {
        if (processing.Renew(lease, LeaseDuration) is null)
        {
            return;
        }

        switch (result.Status)
        {
            case LadderResult<FinancialReading>.Accepted when result.Value!.Document is { } document:
                Categorize(source.SourceId, SourceCategory.ModelOrigin);
                ledger.Commit(lease, document, result.Identity, result.Tier);
                break;
            case LadderResult<FinancialReading>.Accepted:
                ledger.CommitUnsupported(lease, FinancialParseOutcome.NotFinancial);
                break;
            case LadderResult<FinancialReading>.Held:
                ledger.CommitUnsupported(lease, "awaiting_disclosure_approval");
                break;
            case LadderResult<FinancialReading>.Unverified:
                ledger.CommitUnsupported(lease, "verification_failed");
                break;
            case LadderResult<FinancialReading>.Skipped:
                ledger.CommitUnsupported(lease, "skipped_by_policy");
                break;
            default:
                ledger.CommitUnsupported(lease, "index_only");
                break;
        }
    }

    /// <summary>A recognized financial document fixes the source's category unless the owner chose one.</summary>
    private void Categorize(Guid sourceId, string origin)
    {
        if (policy.ReadCategory(sourceId) is not { Origin: SourceCategory.OwnerOrigin })
        {
            policy.SaveCategory(new SourceCategory(sourceId, DataCategories.Financial, origin, policy.ReadCategory(sourceId)?.LocalOnly ?? false));
        }
    }

    private TieredRequest Request(ProcessingLease lease, LeasedFinancialSource source, ChatRequest request) =>
        new(source.SourceId, lease.ItemId, lease.Revision, lease.ProcessorKey, DisclosurePurposes.Intake, request);

    private static string PageText(IReadOnlyList<PdfTextLine> lines)
    {
        StringBuilder text = new();
        foreach (PdfTextLine line in lines)
        {
            if (text.Length + line.Text.Length + 1 > MaximumStatementCharacters)
            {
                break;
            }

            text.Append(line.Text).Append('\n');
        }

        return text.ToString();
    }

    private async Task<byte[]?> ReadAsync(ProcessingLease lease, LeasedFinancialSource source, CancellationToken cancellationToken)
    {
        try
        {
            return await originals.ReadVerifiedBytesAsync(source.Original, FinanceLimits.MaximumDocumentBytes, cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException)
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

        return null;
    }
}
