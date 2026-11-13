namespace Praxis.Domain.Finance;

/// <summary>Which extractor produced a row. Model rows are accepted only after arithmetic verification.</summary>
public static class TransactionOrigins
{
    public const string Parser = "parser";
    public const string Model = "model";
}
