namespace Praxis.Domain.Search;

/// <summary>Validation and normalization of untrusted embedding model output.</summary>
public static class EmbeddingVectors
{
    public const int MaximumDimensions = 4_096;

    /// <summary>Scales a finite, nonzero vector to unit length so dot products are cosine similarities.</summary>
    public static float[] Normalize(float[]? values)
    {
        if (values is null || values.Length is < 1 or > MaximumDimensions || values.Any(value => !float.IsFinite(value)))
        {
            throw new InvalidDataException("The embedding model produced invalid vector values.");
        }

        double squared = values.Sum(value => (double)value * value);
        if (!double.IsFinite(squared) || squared <= 0)
        {
            throw new InvalidDataException("The embedding model returned a zero-length vector.");
        }

        float scale = (float)(1d / Math.Sqrt(squared));
        return values.Select(value => value * scale).ToArray();
    }

    /// <summary>Rejects a missing, oversized or control-character model digest.</summary>
    public static string ValidateModelDigest(string digest) =>
        string.IsNullOrWhiteSpace(digest) || digest.Length > 256 || digest.Any(char.IsControl)
            ? throw new InvalidDataException("The embedding model identity is invalid.")
            : digest;
}
