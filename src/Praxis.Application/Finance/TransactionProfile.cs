using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Praxis.Application.Documents;
using Praxis.Application.Documents.Tables;
using Praxis.Application.Finance.Categorization;
using Praxis.Application.Finance.Delimited;
using Praxis.Application.Finance.Statements;
using Praxis.Application.Inference;
using Praxis.Application.Policy;
using Praxis.Application.Understanding;
using Praxis.Domain.Finance;
using Praxis.Domain.Models;
using Praxis.Domain.Policy;
using Praxis.Domain.Processing;
using Praxis.Domain.Understanding;

namespace Praxis.Application.Finance;

/// <summary>
/// The transaction record profile: domain code that turns any document stating money movements
/// into exact ledger rows with the fewest model tokens. Published interchange formats are read by
/// code alone; tables are mapped once per header layout (cached, so the next export needs no
/// model) and applied in code; statements in prose or scans are transcribed by the strong tier
/// and proven by code (balances reconcile, every amount printed), with the strongest tier on
/// failure. Merchants and spending categories come from one batched call per block of rows.
/// </summary>
public sealed class TransactionProfile(IntakeLadder ladder, IUnderstandingRepository layouts)
{
    private const int MappingOutputTokens = 1_024;
    private const int TranscriptionOutputTokens = 8_192;
    private const int AnnotationOutputTokens = 4_096;
    private const int RequestContextTokens = 32_768;
    private const int MaximumStatementCharacters = 100_000;

    public static bool Applies(DocumentProfile profile, ReadDocument? structure) =>
        structure?.Financial is not null || profile.Category == DataCategories.Financial ||
        profile.RecordKinds.Contains(RecordKinds.Transaction, StringComparer.Ordinal);

    public async Task<TransactionOutcome> ExtractAsync(
        ProcessingLease lease, LeasedUnderstandingSource source, string text, ReadDocument? structure, CancellationToken cancellationToken)
    {
        FinancialInput input = new(source.FileName, source.SourceKey);
        TransactionOutcome read;
        if (structure?.Financial is { } published)
        {
            read = new TransactionOutcome(published, [], LadderResult<FinancialReading>.Accepted, "published_format", null, null, null, 0, 0);
        }
        else if (structure?.Rows is { Count: >= 2 } rows)
        {
            read = await MapAsync(lease, source, rows, input, cancellationToken);
        }
        else if (text.Length <= MaximumStatementCharacters)
        {
            read = await TranscribeAsync(lease, source, text, input, cancellationToken);
        }
        else
        {
            return TransactionOutcome.NotApplicable with { Reason = "statement_too_long" };
        }

        if (read.Document is not { Transactions.Count: > 0 } document)
        {
            return read;
        }

        (IReadOnlyList<TransactionAnnotation> annotations, int annotationInput, int annotationOutput) = await AnnotateAsync(lease, source, document, cancellationToken);
        return read with { Annotations = annotations, InputTokens = read.InputTokens + annotationInput, OutputTokens = read.OutputTokens + annotationOutput };
    }

    private async Task<TransactionOutcome> MapAsync(
        ProcessingLease lease, LeasedUnderstandingSource source, IReadOnlyList<DelimitedRow> rows, FinancialInput input, CancellationToken cancellationToken)
    {
        // Exports often start with a preamble (account, period), so the header may be any early row.
        foreach (DelimitedRow candidate in rows.Take(10))
        {
            if (layouts.ReadLayout(Fingerprint(candidate)) is { } cached && Deserialize(cached) is { IsFinancial: true } known &&
                DelimitedMappingApplier.Apply(known, rows, input, requireProof: false) is { } reused)
            {
                return new TransactionOutcome(reused, [], LadderResult<FinancialReading>.Accepted, "cached_layout", null, null, null, 0, 0);
            }
        }

        int columns = rows.Max(row => row.Fields.Count);
        DelimitedMapping? accepted = null;
        LadderResult<FinancialReading> result = await ladder.RunAsync(Request(lease, source, new ChatRequest(
            DelimitedMappingPrompt.Messages(input.FileName, rows.Take(DelimitedMappingPrompt.SampleRows).ToArray()),
            DelimitedMappingPrompt.Schema, MappingOutputTokens, RequestContextTokens)), (content, tier) =>
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
            accepted = document is null ? null : mapping;
            return document is null ? null : FinancialReading.Of(document);
        }, cancellationToken);
        if (result.Value?.Document is not null && accepted is { } mapping && mapping.HeaderRow >= 0 && mapping.HeaderRow < rows.Count &&
            result.ProviderId is { } provider && result.Tier is { } tier)
        {
            layouts.SaveLayout(Fingerprint(rows[mapping.HeaderRow]), JsonSerializer.Serialize(mapping), tier, provider);
        }

        return Outcome(result);
    }

    private async Task<TransactionOutcome> TranscribeAsync(
        ProcessingLease lease, LeasedUnderstandingSource source, string text, FinancialInput input, CancellationToken cancellationToken)
    {
        LadderResult<FinancialReading> result = await ladder.RunAsync(Request(lease, source, new ChatRequest(
                StatementExtractionPrompt.Messages(text), StatementExtractionPrompt.Schema, TranscriptionOutputTokens, RequestContextTokens)),
            (content, _) => ModelStatementReader.Read(content, text, input, StatementExtractionPrompt.Version), cancellationToken);
        return Outcome(result);
    }

    private async Task<(IReadOnlyList<TransactionAnnotation>, int, int)> AnnotateAsync(
        ProcessingLease lease, LeasedUnderstandingSource source, FinancialDocument document, CancellationToken cancellationToken)
    {
        List<TransactionAnnotation> annotations = [];
        int input = 0;
        int output = 0;
        foreach (ParsedTransaction[] block in document.Transactions.Chunk(AnnotationPrompt.MaximumRows))
        {
            cancellationToken.ThrowIfCancellationRequested();
            LadderResult<IReadOnlyList<(int Ordinal, string Merchant, string Category)>> result = await ladder.RunAsync(
                Request(lease, source, new ChatRequest(AnnotationPrompt.Messages(block), AnnotationPrompt.Schema, AnnotationOutputTokens, RequestContextTokens)),
                (content, _) => AnnotationReader.Read(content, block) is { Count: > 0 } read ? read : null, cancellationToken);
            input += result.InputTokens;
            output += result.OutputTokens;
            if (result.Value is not { } values)
            {
                break;
            }

            annotations.AddRange(values.Select(item =>
                new TransactionAnnotation(item.Ordinal, item.Merchant, item.Category, result.Tier!, result.ProviderId!, result.Identity?.Model)));
        }

        return (annotations, input, output);
    }

    private static TransactionOutcome Outcome(LadderResult<FinancialReading> result) => result.Value switch
    {
        { Document: { } document } => new TransactionOutcome(document, [], result.Status, result.Reason, result.Tier, result.ProviderId, result.Identity,
            result.InputTokens, result.OutputTokens),
        { NotFinancial: true } => TransactionOutcome.NotApplicable with { InputTokens = result.InputTokens, OutputTokens = result.OutputTokens },
        _ => new TransactionOutcome(null, [], result.Status, result.Reason, null, result.ProviderId, null, result.InputTokens, result.OutputTokens)
    };

    /// <summary>A stable identity of a table layout: its normalized header cells.</summary>
    public static string Fingerprint(DelimitedRow header) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\u001f", header.Fields.Select(field => field.Trim().ToLowerInvariant())))));

    private static DelimitedMapping? Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<DelimitedMapping>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static TieredRequest Request(ProcessingLease lease, LeasedUnderstandingSource source, ChatRequest request) =>
        new(source.SourceId, lease.ItemId, lease.Revision, lease.ProcessorKey, DisclosurePurposes.Intake, request, DataCategories.Financial);
}
