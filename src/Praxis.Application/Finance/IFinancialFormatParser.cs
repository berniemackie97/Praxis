namespace Praxis.Application.Finance;

/// <summary>
/// Recognizes and parses one textual financial format. <see cref="CanParse"/> inspects content
/// only; a parser that recognizes a document but cannot read it returns a rejection reason
/// rather than a partial document.
/// </summary>
public interface IFinancialFormatParser
{
    string Format { get; }

    bool CanParse(string text);

    FinancialParseOutcome Parse(string text, FinancialInput input);
}
