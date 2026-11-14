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
        Assert.Equal([ProcessorKeys.TextSegments, ProcessorKeys.IntakeTriage, ProcessorKeys.LinkFetch, ProcessorKeys.TechnicalEvidence],
            ProcessorKeys.DependentsOf(ProcessorKeys.SourceText));
        Assert.Equal("link", ProcessorKeys.RequiredFormat(ProcessorKeys.LinkFetch));
        Assert.Null(ProcessorKeys.RequiredFormat(ProcessorKeys.IntakeTriage));
        Assert.Equal([ProcessorKeys.SemanticExtraction], ProcessorKeys.DependentsOf(ProcessorKeys.TextSegments));
        Assert.Equal([ProcessorKeys.IntakeUnderstand], ProcessorKeys.DependentsOf(ProcessorKeys.IntakeTriage));
        Assert.Empty(ProcessorKeys.DependentsOf(ProcessorKeys.SemanticExtraction));
        Assert.Empty(ProcessorKeys.DependentsOf(ProcessorKeys.IntakeUnderstand));
    }
}
