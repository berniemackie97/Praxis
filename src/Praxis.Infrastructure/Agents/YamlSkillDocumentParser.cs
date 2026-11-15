using Praxis.Application.Agents.Skills;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace Praxis.Infrastructure.Agents;

/// <summary>Reads bounded Agent Skills frontmatter; the Markdown body remains plain text.</summary>
internal sealed class YamlSkillDocumentParser : ISkillDocumentParser
{
    public SkillDocument Parse(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        if (markdown.Length > SkillDocument.MaximumCharacters)
        {
            throw new ArgumentException("A skill must be plain text of at most 6,000 characters.", nameof(markdown));
        }

        string normalized = markdown.TrimStart('\uFEFF').ReplaceLineEndings("\n");
        int end = normalized.Length > 4 ? normalized.IndexOf("\n---\n", 4, StringComparison.Ordinal) : -1;
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal) || end is < 4 or > 2_048)
        {
            throw new ArgumentException("Start SKILL.md with YAML frontmatter containing name and description.", nameof(markdown));
        }

        string header = normalized[4..end];
        try
        {
            // Bound nesting and reject aliases before building the representation model.
            Parser parser = new(new StringReader(header));
            int depth = 0;
            while (parser.MoveNext())
            {
                if (parser.Current is AnchorAlias)
                {
                    throw new ArgumentException("Skill frontmatter cannot use YAML aliases.", nameof(markdown));
                }

                depth += parser.Current?.NestingIncrease ?? 0;
                if (depth > 8)
                {
                    throw new ArgumentException("Skill frontmatter is nested too deeply.", nameof(markdown));
                }
            }

            YamlStream yaml = new();
            yaml.Load(new StringReader(header));
            if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode fields)
            {
                throw new ArgumentException("Skill frontmatter must be one mapping.", nameof(markdown));
            }

            return new SkillDocument(Field(fields, "name"), Field(fields, "description"), normalized, normalized[(end + 5)..].Trim());
        }
        catch (YamlException exception)
        {
            throw new ArgumentException("Skill frontmatter is not valid YAML.", nameof(markdown), exception);
        }
    }

    private static string Field(YamlMappingNode fields, string key) =>
        fields.Children.TryGetValue(new YamlScalarNode(key), out YamlNode? node) && node is YamlScalarNode { Value: { } value }
            ? value.Trim()
            : throw new ArgumentException($"Skill frontmatter needs a text '{key}' field.");
}
