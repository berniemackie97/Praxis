using System.Globalization;

namespace Praxis.Infrastructure.Persistence;

/// <summary>Canonical text encodings for values persisted in the catalog.</summary>
internal static class CatalogValues
{
    internal static string Timestamp(DateTimeOffset time) => time.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    internal static DateTimeOffset ParseTimestamp(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
