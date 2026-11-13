using Praxis.Domain.Finance;

namespace Praxis.Application.Finance;

/// <summary>
/// A verified model reading of a source: either a financial document that passed code
/// verification, or the model's verdict that the source is not a financial document.
/// </summary>
public sealed record FinancialReading(FinancialDocument? Document, bool NotFinancial)
{
    public static FinancialReading Of(FinancialDocument document) => new(document, false);

    public static FinancialReading NotAFinancialDocument { get; } = new(null, true);
}
