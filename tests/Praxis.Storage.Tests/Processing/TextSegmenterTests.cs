using System.Text;
using Praxis.Storage;
using Xunit;

namespace Praxis.Storage.Tests;

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
        var text = GenerateDocument(new Random(seed));
        var segments = TextSegmenter.Segment(text, markdown);

        AssertExactCoverage(text, segments);
        Assert.All(segments, segment =>
            Assert.InRange(segment.Length, 1, TextSegmenter.MaximumCharacters));
    }

    [Fact]
    public void MarkdownSectionsStartAtHeadingsWithTheirPath()
    {
        var intro = Paragraph('a', 300);
        var runtime = Paragraph('b', 300);
        var text = $"# Architecture\n{intro}\n\n## Runtime\n{runtime}\n\n# Operations\nShort.\n";

        var segments = TextSegmenter.Segment(text, markdown: true);

        Assert.Equal(["Architecture", "Architecture > Runtime", "Operations"],
            segments.Select(segment => segment.HeadingPath));
        Assert.StartsWith("## Runtime", Slice(text, segments[1]), StringComparison.Ordinal);
        AssertExactCoverage(text, segments);
    }

    [Fact]
    public void FencedCodeIsNeitherHeadingsNorSplitAtBlankLines()
    {
        var text = "# Build\n" + Paragraph('x', 250) + "\n\n```sh\n# not a heading\n\nmake all\n```\n";

        var segments = TextSegmenter.Segment(text, markdown: true);

        var segment = Assert.Single(segments);
        Assert.Equal("Build", segment.HeadingPath);
    }

    [Fact]
    public void HashLinesInNonMarkdownTextAreNotHeadings()
    {
        var text = "#region Parsing\n" + Paragraph('c', 300) + "\n#endregion\n";

        var segment = Assert.Single(TextSegmenter.Segment(text, markdown: false));
        Assert.Null(segment.HeadingPath);
    }

    [Fact]
    public void OversizedBlocksSplitAtLineBreaksThenAtTheLimitWithoutBreakingSurrogates()
    {
        var lines = string.Join('\n', Enumerable.Repeat(new string('l', 150), 30));
        var emoji = string.Concat(Enumerable.Repeat("😀", 1_500));
        var text = lines + "\n" + emoji;

        var segments = TextSegmenter.Segment(text, markdown: false);

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
        var builder = new StringBuilder();
        for (var index = 0; index <= TextSegmenter.MaximumSegments; index++)
        {
            builder.Append("# H").Append(index).Append('\n').Append(Paragraph('p', 200)).Append('\n');
        }

        Assert.Throws<InvalidDataException>(() =>
            TextSegmenter.Segment(builder.ToString(), markdown: true));
    }

    private static void AssertExactCoverage(string text, IReadOnlyList<TextSegmentSpan> segments)
    {
        var expected = 0;
        foreach (var segment in segments)
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
        var builder = new StringBuilder();
        var fenced = false;
        for (var line = 0; line < 400; line++)
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
