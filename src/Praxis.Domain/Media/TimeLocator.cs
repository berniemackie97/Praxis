using System.Globalization;

namespace Praxis.Domain.Media;

/// <summary>Citation locators for media time ranges: <c>time:12.5-47.25</c> (seconds, millisecond precision).</summary>
public static class TimeLocator
{
    public const string Prefix = "time:";

    public static string Format(long startMilliseconds, long endMilliseconds) =>
        string.Create(CultureInfo.InvariantCulture, $"{Prefix}{startMilliseconds / 1000.0:0.###}-{endMilliseconds / 1000.0:0.###}");

    public static bool TryParse(string? locator, out long startMilliseconds, out long endMilliseconds)
    {
        startMilliseconds = endMilliseconds = 0;
        if (locator is null || !locator.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        string[] parts = locator[Prefix.Length..].Split('-');
        if (parts.Length != 2 ||
            !double.TryParse(parts[0], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double start) ||
            !double.TryParse(parts[1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double end) ||
            start < 0 || end < start || end > 86_400)
        {
            return false;
        }

        startMilliseconds = (long)Math.Round(start * 1000);
        endMilliseconds = (long)Math.Round(end * 1000);
        return true;
    }
}
