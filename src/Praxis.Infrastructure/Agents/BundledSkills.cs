using Praxis.Application.Agents.Skills;

namespace Praxis.Infrastructure.Agents;

internal static class BundledSkills
{
    private const string Prefix = "Praxis.BundledSkills.";

    public static IReadOnlyList<SkillDocument> Documents { get; } = Load();

    private static SkillDocument[] Load()
    {
        YamlSkillDocumentParser parser = new();
        var assembly = typeof(BundledSkills).Assembly;
        return assembly.GetManifestResourceNames().Where(name => name.StartsWith(Prefix, StringComparison.Ordinal)).Select(resource =>
        {
            using Stream stream = assembly.GetManifestResourceStream(resource) ?? throw new InvalidOperationException("Missing bundled skill.");
            using StreamReader reader = new(stream);
            SkillDocument document = parser.Parse(reader.ReadToEnd());
            string folder = resource[Prefix.Length..].Replace('\\', '/').Split('/')[0];
            return folder == document.Name ? document : throw new InvalidOperationException("Bundled skill name must match its folder.");
        }).ToArray();
    }
}
