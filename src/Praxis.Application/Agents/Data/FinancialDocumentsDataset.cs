using Praxis.Application.Finance;
using Praxis.Domain.Agents;

namespace Praxis.Application.Agents.Data;

/// <summary>Financial files Praxis recognized and what happened to each (completed, awaiting approval, unverified, index-only).</summary>
public sealed class FinancialDocumentsDataset(FinancialLedger ledger) : IDataset
{
    public string Name => "financial_documents";

    public string Description => "Recognized financial files and their intake outcome; use it to explain missing data.";

    public IReadOnlyList<DataField> Fields { get; } =
    [
        new("source", DataField.Text, "File name", false),
        new("outcome", DataField.Text, "completed or unsupported", true),
        new("reason", DataField.Text, "Why not used (awaiting_disclosure_approval, verification_failed, index_only, ...)", true),
        new("format", DataField.Text, "ofx, qif, delimited, pdf_statement", true),
        new("transactions", DataField.Integer, "Rows recorded", false),
        new("recorded", DataField.Text, "When processed (UTC)", false)
    ];

    public DatasetSnapshot Snapshot(AgentToolContext context) => new(ledger.Documents(500, context.MayDisclose).Select(document => new DataRow(
        new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["source"] = document.SourceName,
            ["outcome"] = document.Outcome,
            ["reason"] = document.ReasonCode,
            ["format"] = document.Format,
            ["transactions"] = document.TransactionCount,
            ["recorded"] = document.RecordedUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
        },
        [new AgentCitation(document.SourceId, document.ItemId, document.Revision, "document", document.SourceName)])).ToArray(), 0, []);
}
