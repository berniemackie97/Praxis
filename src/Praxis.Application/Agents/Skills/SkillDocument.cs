using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Praxis.Application.Agents.Skills;

/// <summary>A portable, text-only SKILL.md. Optional frontmatter is preserved but cannot grant capabilities.</summary>
public sealed record SkillDocument
{
    public const int MaximumCharacters = 6_000;
    public const int MaximumName = 64;

    public SkillDocument(string name, string description, string markdown, string instructions)
    {
        ValidateName(name);
        if (string.IsNullOrWhiteSpace(description) || description.Length > 1_024)
        {
            throw new ArgumentException("A skill description must contain 1–1,024 characters.", nameof(description));
        }

        if (markdown.Length > MaximumCharacters || markdown.Any(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t'))
        {
            throw new ArgumentException("A skill must be plain text of at most 6,000 characters.", nameof(markdown));
        }

        if (string.IsNullOrWhiteSpace(instructions))
        {
            throw new ArgumentException("Write the steps of the skill after the frontmatter.", nameof(instructions));
        }

        if (JsonEncodedText.Encode(instructions).EncodedUtf8Bytes.Length > 16_000)
        {
            throw new ArgumentException("Shorten the skill text before importing it into Chat.", nameof(instructions));
        }

        Name = name;
        Description = string.Join(' ', description.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        Markdown = markdown;
        Instructions = instructions;
        Digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(markdown)));
    }

    public string Name { get; }
    public string Description { get; }
    public string Markdown { get; }
    public string Instructions { get; }
    public string Digest { get; }

    public static void ValidateName(string name)
    {
        if (name.Length is < 1 or > MaximumName || name[0] == '-' || name[^1] == '-' ||
            name.Contains("--", StringComparison.Ordinal) || !name.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))
        {
            throw new ArgumentException("Use a lowercase skill name with letters, numbers and single hyphens.", nameof(name));
        }
    }
}
