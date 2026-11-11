namespace Praxis.Domain.Text;

/// <summary>Bounds on extracted text kept in the catalog.</summary>
public static class TextLimits
{
    /// <summary>Largest original, in bytes, decoded as text; also the largest stored text derivative.</summary>
    public const int MaximumTextBytes = 1024 * 1024;
}
