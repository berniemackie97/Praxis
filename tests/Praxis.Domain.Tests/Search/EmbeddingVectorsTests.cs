using Praxis.Domain.Search;
using Xunit;

namespace Praxis.Domain.Tests.Search;

public sealed class EmbeddingVectorsTests
{
    [Fact]
    public void NormalizesToUnitLength()
    {
        float[] unit = EmbeddingVectors.Normalize([3, 4]);
        Assert.Equal(0.6f, unit[0], 5);
        Assert.Equal(0.8f, unit[1], 5);
    }

    [Theory]
    [InlineData(new float[0])]
    [InlineData(new[] { 0f, 0f })]
    [InlineData(new[] { 1f, float.NaN })]
    public void RejectsEmptyZeroOrNonFiniteVectors(float[] values) =>
        Assert.Throws<InvalidDataException>(() => EmbeddingVectors.Normalize(values));

    [Fact]
    public void RejectsMissingOrControlCharacterModelDigests()
    {
        Assert.Equal("sha256:abc", EmbeddingVectors.ValidateModelDigest("sha256:abc"));
        Assert.Throws<InvalidDataException>(() => EmbeddingVectors.ValidateModelDigest(" "));
        Assert.Throws<InvalidDataException>(() => EmbeddingVectors.ValidateModelDigest("digest\n"));
    }
}
