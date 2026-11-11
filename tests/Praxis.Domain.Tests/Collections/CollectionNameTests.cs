using Praxis.Domain.Collections;
using Xunit;

namespace Praxis.Domain.Tests.Collections;

public sealed class CollectionNameTests
{
    [Fact]
    public void KeysAreCaseInsensitiveAndDisplayNamesAreTrimmed() =>
        Assert.Equal(("Research Notes", "RESEARCH NOTES"), CollectionName.Normalize("  Research Notes "));

    [Theory]
    [InlineData(" ")]
    [InlineData("tab\tname")]
    public void RejectsBlankOrControlCharacterNames(string name) =>
        Assert.ThrowsAny<ArgumentException>(() => CollectionName.Normalize(name));

    [Fact]
    public void RejectsNamesLongerThanTheLimit() =>
        Assert.Throws<ArgumentException>(() => CollectionName.Normalize(new string('a', CollectionName.MaximumLength + 1)));
}
