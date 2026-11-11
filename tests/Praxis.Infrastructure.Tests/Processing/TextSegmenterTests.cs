using System.Text;
using Praxis.Infrastructure;
using Praxis.Infrastructure.Processing;
using Xunit;

namespace Praxis.Infrastructure.Tests.Processing;

public sealed class TextSegmenterTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   \n\t\r\n  ")]
    public void TextWithoutContentProducesNoSegments(string text) =>
        Assert.Empty(TextSegmenter.Segment(text, markdown: true));

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    [InlineData(5, true)]
    public void SegmentsAreContiguousBoundedAndCoverGeneratedText(int seed, bool markdown)
    {
        string text = GenerateDocument(new Random(seed));
        IReadOnlyList<TextSegmentSpan> segments = TextSegmenter.Segment(text, markdown);

        AssertExactCoverage(text, segments);
        Assert.All(segments, segment =>
            Assert.InRange(segment.Length, 1, TextSegmenter.MaximumCharacters));
    }

    [Fact]
    public void MarkdownSectionsStartAtHeadingsWithTheirPath()
    {
        string intro = Paragraph('a', 300);
        string runtime = Paragraph('b', 300);
        string text = $"# Architecture\n{intro}\n\n## Runtime\n{runtime}\n\n# Operations\nShort.\n";

        IReadOnlyList<TextSegmentSpan> segments = TextSegmenter.Segment(text, markdown: true);

        Assert.Equal(["Architecture", "Architecture > Runtime", "Operations"],
            segments.Select(segment => segment.HeadingPath));
        Assert.StartsWith("## Runtime", Slice(text, segments[1]), StringComparison.Ordinal);
        AssertExactCoverage(text, segments);
    }

    [Fact]
    public void FencedCodeIsNeitherHeadingsNorSplitAtBlankLines()
    {
        string text = "# Build\n" + Paragraph('x', 250) + "\n\n```sh\n# not a heading\n\nmake all\n```\n";

        IReadOnlyList<TextSegmentSpan> segments = TextSegmenter.Segment(text, markdown: true);

        TextSegmentSpan segment = Assert.Single(segments);
        Assert.Equal("Build", segment.HeadingPath);
    }

    [Fact]
    public void HashLinesInNonMarkdownTextAreNotHeadings()
    {
        string text = "#region Parsing\n" + Paragraph('c', 300) + "\n#endregion\n";

        TextSegmentSpan segment = Assert.Single(TextSegmenter.Segment(text, markdown: false));
        Assert.Null(segment.HeadingPath);
    }

    [Fact]
    public void OversizedBlocksSplitAtLineBreaksThenAtTheLimitWithoutBreakingSurrogates()
    {
        string lines = string.Join('\n', Enumerable.Repeat(new string('l', 150), 30));
        string emoji = string.Concat(Enumerable.Repeat("😀", 1_500));
        string text = lines + "\n" + emoji;

        IReadOnlyList<TextSegmentSpan> segments = TextSegmenter.Segment(text, markdown: false);

        AssertExactCoverage(text, segments);
        Assert.All(segments, segment =>
        {
            Assert.InRange(segment.Length, 1, TextSegmenter.MaximumCharacters);
            Assert.False(char.IsLowSurrogate(text[segment.Start]));
        });
        Assert.True(segments.Take(2).All(segment => text[segment.Start + segment.Length - 1] == '\n'));
    }

    [Fact]
    public void PathologicalSegmentCountIsRejected()
    {
        StringBuilder builder = new StringBuilder();
        for (int index = 0; index <= TextSegmenter.MaximumSegments; index++)
        {
            builder.Append("# H").Append(index).Append('\n').Append(Paragraph('p', 200)).Append('\n');
        }

        Assert.Throws<InvalidDataException>(() =>
            TextSegmenter.Segment(builder.ToString(), markdown: true));
    }

    private static void AssertExactCoverage(string text, IReadOnlyList<TextSegmentSpan> segments)
    {
        int expected = 0;
        foreach (TextSegmentSpan segment in segments)
        {
            Assert.Equal(expected, segment.Start);
            expected += segment.Length;
        }

        Assert.Equal(text.Length, expected);
    }

    private static string Slice(string text, TextSegmentSpan segment) =>
        text.Substring(segment.Start, segment.Length);

    private static string Paragraph(char fill, int length) => new(fill, length);

    private static string GenerateDocument(Random random)
    {
        StringBuilder builder = new StringBuilder();
        bool fenced = false;
        for (int line = 0; line < 400; line++)
        {
            switch (random.Next(10))
            {
                case 0:
                    builder.Append(new string('#', random.Next(1, 7))).Append(" Heading ").Append(line);
                    break;
                case 1:
                    builder.Append(fenced ? "```" : "```cs");
                    fenced = !fenced;
                    break;
                case 2:
                    break;
                case 3:
                    builder.Append(new string('w', random.Next(1_000, 5_000)));
                    break;
                default:
                    builder.Append(new string('t', random.Next(1, 160)));
                    break;
            }

            builder.Append(random.Next(4) == 0 ? "\r\n" : "\n");
        }

        return builder.ToString();
    }
}
