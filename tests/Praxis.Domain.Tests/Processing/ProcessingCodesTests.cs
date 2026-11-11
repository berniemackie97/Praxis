using Praxis.Domain.Processing;
using Xunit;

namespace Praxis.Domain.Tests.Processing;

public sealed class ProcessingCodesTests
{
    [Theory]
    [InlineData("invalid_utf8", true)]
    [InlineData("model_timeout", true)]
    [InlineData("", false)]
    [InlineData("Upper_Case", false)]
    [InlineData("/private/path", false)]
    public void AcceptsOnlyBoundedLowercaseIdentifiers(string code, bool valid) =>
        Assert.Equal(valid, ProcessingCodes.IsValid(code));

    [Fact]
    public void SchedulesDependentStagesAfterTheirInputs()
    {
        Assert.Equal([ProcessorKeys.MarkdownHeadings, ProcessorKeys.TextSegments], ProcessorKeys.DependentsOf(ProcessorKeys.SourceText));
        Assert.Equal([ProcessorKeys.SemanticExtraction], ProcessorKeys.DependentsOf(ProcessorKeys.TextSegments));
        Assert.Empty(ProcessorKeys.DependentsOf(ProcessorKeys.SemanticExtraction));
    }
}
