namespace Praxis.Application.Documents;

/// <summary>
/// Text, Markdown, subtitles, configuration and source code, decoded as UTF-8/UTF-16 with a
/// Windows-1252 fallback for legacy exports. Also reads extensionless or mislabeled files whose
/// bytes are text. Binary content is never decoded as text.
/// </summary>
public sealed class PlainTextReader : IDocumentReader
{
    private static readonly HashSet<string> MarkdownExtensions = new(StringComparer.Ordinal) { ".md", ".markdown", ".mdown", ".mkd" };
    private static readonly HashSet<string> SubtitleExtensions = new(StringComparer.Ordinal) { ".vtt", ".srt" };
    private static readonly HashSet<string> CodeExtensions = new(StringComparer.Ordinal)
    {
        ".cs", ".csx", ".fs", ".fsx", ".vb", ".c", ".h", ".cc", ".cpp", ".cxx", ".hh", ".hpp", ".hxx", ".m", ".mm", ".swift",
        ".java", ".kt", ".kts", ".scala", ".go", ".rs", ".py", ".rb", ".php", ".pl", ".lua", ".r", ".jl", ".dart",
        ".js", ".mjs", ".cjs", ".ts", ".tsx", ".jsx", ".css", ".scss", ".less", ".sql", ".sh", ".bash", ".zsh", ".fish", ".ps1",
        ".asm", ".s", ".lst", ".gradle", ".cmake", ".mk", ".proto", ".graphql", ".tf", ".hcl", ".nix"
    };
    private static readonly HashSet<string> TextExtensions = new(StringComparer.Ordinal)
    {
        ".txt", ".text", ".log", ".json", ".jsonl", ".ndjson", ".yaml", ".yml", ".toml", ".ini", ".cfg", ".conf", ".env.example",
        ".xml", ".plist", ".rst", ".adoc", ".org", ".tex", ".bib", ".ics", ".vcf", ".csproj", ".props", ".targets", ".sln", ".slnx"
    };

    public IReadOnlyCollection<string> Extensions { get; } =
        MarkdownExtensions.Concat(SubtitleExtensions).Concat(CodeExtensions).Concat(TextExtensions).ToArray();

    public bool CanRead(DocumentInput input)
    {
        if (BinarySignature.Matches(input.Bytes) || BinarySignature.BinaryExtensions.Contains(input.Extension))
        {
            return false;
        }

        if (Extensions.Contains(input.Extension))
        {
            return true;
        }

        ReadOnlySpan<byte> head = input.Bytes.AsSpan(0, Math.Min(input.Bytes.Length, 8_192));
        return head.Length > 0 && TextDecoder.Decode(head) is { } text && TextShape.LooksLikeText(text);
    }

    public Task<ReadOutcome> ReadAsync(DocumentInput input, CancellationToken cancellationToken)
    {
        string? text = TextDecoder.Decode(input.Bytes);
        if (text is null || !TextShape.LooksLikeText(text))
        {
            return Task.FromResult(ReadOutcome.Unreadable("binary_content"));
        }

        string format = MarkdownExtensions.Contains(input.Extension) ? DocumentFormats.Markdown
            : SubtitleExtensions.Contains(input.Extension) ? DocumentFormats.Subtitles
            : CodeExtensions.Contains(input.Extension) ? DocumentFormats.Code
            : DocumentFormats.Text;
        return Task.FromResult(ReadOutcome.Read(new ReadDocument(format, text)));
    }
}
