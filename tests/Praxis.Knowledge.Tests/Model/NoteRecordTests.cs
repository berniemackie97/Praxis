using Praxis.Knowledge.Model;
using Xunit;

namespace Praxis.Knowledge.Tests.Model;

public sealed class NoteRecordTests
{
    [Fact]
    public void RevisionPreservesIdentityAndOriginalSnapshot()
    {
        NoteRecord original = NoteRecord.Create("Notes/first.md", "# First\r\n");
        NoteRecord renamed = original.Revise("Archive/renamed.md", "# Second\n");

        Assert.NotEqual(Guid.Empty, original.Id);
        Assert.Equal(original.Id, renamed.Id);
        Assert.Equal(1, original.Current.Number);
        Assert.Equal("Notes/first.md", original.Current.Path);
        Assert.Equal("# First\r\n", original.Current.Markdown);
        Assert.Equal(2, renamed.Current.Number);
        Assert.Equal("Archive/renamed.md", renamed.Current.Path);
        Assert.Equal("# Second\n", renamed.Current.Markdown);
        Assert.Equal(2, renamed.Revisions.Count);
        Assert.Equal(original.Current, renamed.Revisions[0]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/absolute.md")]
    [InlineData("../secret.md")]
    [InlineData("notes/../secret.md")]
    [InlineData("notes//double.md")]
    [InlineData("notes\\backslash.md")]
    [InlineData("C:/windows.md")]
    [InlineData("notes/CON.md")]
    [InlineData("notes/com9.md")]
    [InlineData("notes/LPT1.md")]
    [InlineData("notes/illegal?.md")]
    [InlineData("notes/trailing /file.md")]
    [InlineData("notes/not-markdown.txt")]
    [InlineData("notes/e\u0301.md")]
    public void InvalidPortablePathsAreRejected(string path)
    {
        Assert.Throws<ArgumentException>(() => NoteRecord.Create(path, "content"));
    }

    [Fact]
    public void EmptyMarkdownIsValid()
    {
        NoteRecord note = NoteRecord.Create("empty.md", "");

        Assert.Empty(note.Current.Markdown);
    }
}
