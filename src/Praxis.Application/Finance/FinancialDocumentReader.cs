using Praxis.Application.Finance.Ofx;
using Praxis.Application.Finance.Qif;

namespace Praxis.Application.Finance;

/// <summary>
/// Reads the published interchange formats (OFX/QFX/QBO and QIF) entirely in code. Every other
/// layout — delimited exports and statements — is understood by a model tier and verified.
/// </summary>
public static class FinancialDocumentReader
{
    private static readonly IFinancialFormatParser[] Parsers = [new OfxParser(), new QifParser()];

    public static bool IsPdf(ReadOnlySpan<byte> original) => original.StartsWith("%PDF-"u8);

    /// <summary>The parser for a published format the text is written in, or null.</summary>
    public static IFinancialFormatParser? PublishedFormat(string text) => Parsers.FirstOrDefault(parser => parser.CanParse(text));
}
