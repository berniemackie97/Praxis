namespace Praxis.Domain.Finance;

/// <summary>ISO 4217 minor-unit exponents for currencies whose minor unit is not hundredths.</summary>
public static class CurrencyExponents
{
    private static readonly IReadOnlyDictionary<string, int> Exceptions = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["BIF"] = 0,
        ["CLP"] = 0,
        ["DJF"] = 0,
        ["GNF"] = 0,
        ["ISK"] = 0,
        ["JPY"] = 0,
        ["KMF"] = 0,
        ["KRW"] = 0,
        ["PYG"] = 0,
        ["RWF"] = 0,
        ["UGX"] = 0,
        ["VND"] = 0,
        ["VUV"] = 0,
        ["XAF"] = 0,
        ["XOF"] = 0,
        ["XPF"] = 0,
        ["BHD"] = 3,
        ["IQD"] = 3,
        ["JOD"] = 3,
        ["KWD"] = 3,
        ["LYD"] = 3,
        ["OMR"] = 3,
        ["TND"] = 3
    };

    /// <summary>Validates a three-letter uppercase currency code.</summary>
    public static bool IsValidCode(string? code) =>
        code is { Length: 3 } && code.All(c => c is >= 'A' and <= 'Z');

    public static int Of(string currency)
    {
        if (!IsValidCode(currency))
        {
            throw new ArgumentException("A three-letter ISO 4217 currency code is required.", nameof(currency));
        }

        return Exceptions.TryGetValue(currency, out int exponent) ? exponent : 2;
    }
}
