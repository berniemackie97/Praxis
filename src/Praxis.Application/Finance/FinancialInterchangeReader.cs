using System.Globalization;
using System.Text;
using Praxis.Application.Documents;
using Praxis.Domain.Finance;

namespace Praxis.Application.Finance;

/// <summary>
/// Reads the published financial interchange formats (OFX/QFX/QBO and QIF) entirely in code: the
/// typed transactions travel with the document so the transaction profile needs no model to
/// transcribe them, and a compact rendering serves search and citations.
/// </summary>
public sealed class FinancialInterchangeReader : IDocumentReader
{
    public IReadOnlyCollection<string> Extensions { get; } = [".ofx", ".qfx", ".qbo", ".qif"];

    public bool CanRead(DocumentInput input)
    {
        if (BinarySignature.Matches(input.Bytes) || BinarySignature.BinaryExtensions.Contains(input.Extension))
        {
            return false;
        }

        if (Extensions.Contains(input.Extension))
        {
            return true;
        }

        string? head = TextDecoder.Decode(input.Bytes.AsSpan(0, Math.Min(input.Bytes.Length, 4_096)));
        return head is not null && FinancialDocumentReader.PublishedFormat(head) is not null;
    }

    public Task<ReadOutcome> ReadAsync(DocumentInput input, CancellationToken cancellationToken)
    {
        string? text = TextDecoder.Decode(input.Bytes);
        if (text is null || FinancialDocumentReader.PublishedFormat(text) is not { } parser)
        {
            return Task.FromResult(ReadOutcome.Unreadable("financial_format_unrecognized"));
        }

        FinancialParseOutcome parsed = parser.Parse(text, new FinancialInput(input.FileName, input.SourceKey));
        return Task.FromResult(parsed.Document is { } document
            ? ReadOutcome.Read(new ReadDocument(DocumentFormats.FinancialInterchange, Render(document), Financial: document))
            : ReadOutcome.Unreadable(parsed.ReasonCode ?? FinancialParseOutcome.NotFinancial));
    }

    public static string Render(FinancialDocument document)
    {
        StringBuilder builder = new();
        foreach (FinancialAccount account in document.Accounts)
        {
            builder.Append("Account ").Append(account.DisplayName).Append(" (").Append(account.Kind).Append(", ").Append(account.Currency).Append(")\n");
        }

        foreach (ParsedTransaction row in document.Transactions)
        {
            builder.Append(row.PostedOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append("  ")
                .Append(row.Amount.ToDecimal().ToString("F" + row.Amount.Exponent, CultureInfo.InvariantCulture)).Append(' ').Append(row.Amount.Currency)
                .Append("  ").Append(row.Description).Append('\n');
        }

        return builder.ToString();
    }
}
