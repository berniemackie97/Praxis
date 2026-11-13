using Praxis.Domain.Finance;

namespace Praxis.Application.Finance;

/// <summary>A recognized document, or the bounded reason a source is not (or not usably) financial.</summary>
public sealed record FinancialParseOutcome(FinancialDocument? Document, string? ReasonCode)
{
    public const string NotFinancial = "not_financial";

    public static FinancialParseOutcome Recognized(FinancialDocument document) => new(document, null);

    public static FinancialParseOutcome Rejected(string reasonCode) => new(null, reasonCode);
}
