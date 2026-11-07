using System.Text;

namespace Praxis.Knowledge;

internal static class NotePath
{
    public static void Validate(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (path.Length is 0 or > 1024 ||
            !path.IsNormalized(NormalizationForm.FormC) ||
            path.StartsWith('/') ||
            path.Contains('\\') ||
            !path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("A note path must be a normalized, relative Markdown path.", nameof(path));
        }

        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 ||
                segment is "." or ".." ||
                segment.EndsWith(' ') ||
                segment.EndsWith('.') ||
                segment.Any(character => character < ' ' ||
                    character == '\u007f' ||
                    "<>:\"|?*".Contains(character)))
            {
                throw new ArgumentException("The note path contains an invalid segment.", nameof(path));
            }

            var stem = segment.Split('.')[0];
            if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
                (stem.Length == 4 && stem.AsSpan(0, 3).Equals("COM", StringComparison.OrdinalIgnoreCase) && stem[3] is >= '1' and <= '9') ||
                (stem.Length == 4 && stem.AsSpan(0, 3).Equals("LPT", StringComparison.OrdinalIgnoreCase) && stem[3] is >= '1' and <= '9'))
            {
                throw new ArgumentException("The note path contains a reserved filename.", nameof(path));
            }
        }
    }
}
