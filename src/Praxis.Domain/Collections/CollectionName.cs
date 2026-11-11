using System.Text;

namespace Praxis.Domain.Collections;

/// <summary>Owner-authored collection names: display spelling and the case-insensitive uniqueness key.</summary>
public static class CollectionName
{
    public const int MaximumLength = 80;

    public static (string Display, string Key) Normalize(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string display = name.Trim().Normalize(NormalizationForm.FormC);
        string key = display.ToUpperInvariant();
        if (display.Length is < 1 or > MaximumLength || key.Length > MaximumLength || display.Any(char.IsControl))
        {
            throw new ArgumentException("Collection names must be one to eighty printable characters.", nameof(name));
        }

        return (display, key);
    }
}
