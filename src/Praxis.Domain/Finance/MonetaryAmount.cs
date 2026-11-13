namespace Praxis.Domain.Finance;

/// <summary>
/// An exact signed amount in a currency's minor units. Positive values increase the owner's
/// cash position (income, refunds, payments received); negative values decrease it.
/// </summary>
public readonly record struct MonetaryAmount(long Minor, string Currency)
{
    public int Exponent => CurrencyExponents.Of(Currency);

    public decimal ToDecimal() => Minor / Scale(Exponent);

    public MonetaryAmount Negate() => this with { Minor = checked(-Minor) };

    /// <summary>
    /// Converts an exact decimal value; returns false when it carries precision beyond the
    /// currency's minor unit or overflows, so no amount is silently rounded.
    /// </summary>
    public static bool TryFromDecimal(decimal value, string currency, out MonetaryAmount amount)
    {
        amount = default;
        if (!CurrencyExponents.IsValidCode(currency))
        {
            return false;
        }

        decimal scaled;
        try
        {
            scaled = value * Scale(CurrencyExponents.Of(currency));
        }
        catch (OverflowException)
        {
            return false;
        }

        if (decimal.Truncate(scaled) != scaled || scaled is > long.MaxValue or < -long.MaxValue)
        {
            return false;
        }

        amount = new MonetaryAmount((long)scaled, currency);
        return true;
    }

    private static decimal Scale(int exponent) => exponent switch
    {
        0 => 1m,
        2 => 100m,
        3 => 1000m,
        _ => throw new ArgumentOutOfRangeException(nameof(exponent))
    };
}
