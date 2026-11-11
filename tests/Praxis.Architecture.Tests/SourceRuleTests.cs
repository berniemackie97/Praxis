using System.Text.RegularExpressions;
using Xunit;

namespace Praxis.Architecture.Tests;

/// <summary>Source-level rules for production code that project references alone cannot express.</summary>
public sealed partial class SourceRuleTests
{
    [Fact]
    public void SqlProviderIsUsedOnlyByThePersistenceLayer() =>
        Assert.Empty(SourceTree.ProductionFiles()
            .Where(file => !file.Relative.StartsWith("Praxis.Infrastructure/Persistence/", StringComparison.Ordinal))
            .Where(file => File.ReadAllText(file.Path).Contains("Microsoft.Data.Sqlite", StringComparison.Ordinal))
            .Select(file => file.Relative));

    [Fact]
    public void TimeComesFromAnInjectedTimeProvider() =>
        Assert.Empty(SourceTree.ProductionFiles()
            .Where(file => WallClock().IsMatch(File.ReadAllText(file.Path)))
            .Select(file => file.Relative));

    [Fact]
    public void EachFileDeclaresOneTopLevelTypeNamedAfterIt() =>
        Assert.Empty(SourceTree.ProductionFiles()
            .Select(file => (file.Relative, Types: TopLevelType().Matches(File.ReadAllText(file.Path)).Select(match => match.Groups["name"].Value).ToArray()))
            .Where(file => file.Types.Length > 1 ||
                (file.Types.Length == 1 && file.Types[0] != Path.GetFileName(file.Relative).Split('.')[0]))
            .Select(file => $"{file.Relative}: {string.Join(", ", file.Types)}"));

    [Fact]
    public void NamespacesFollowTheFolderStructure() =>
        Assert.Empty(SourceTree.ProductionFiles()
            .Select(file => (file.Relative, Declared: FileNamespace().Match(File.ReadAllText(file.Path))))
            .Where(file => file.Declared.Success)
            .Where(file => file.Declared.Groups["name"].Value != ExpectedNamespace(file.Relative))
            .Select(file => $"{file.Relative}: {file.Declared.Groups["name"].Value}"));

    private static string ExpectedNamespace(string relative) =>
        string.Join('.', relative.Split('/')[..^1]);

    [GeneratedRegex(@"\bDateTime(Offset)?\.(UtcNow|Now)\b")]
    private static partial Regex WallClock();

    [GeneratedRegex(@"^(public|internal)\s+((sealed|static|abstract|readonly|partial)\s+)*(record\s+struct|record|class|struct|enum|interface)\s+(?<name>\w+)", RegexOptions.Multiline)]
    private static partial Regex TopLevelType();

    [GeneratedRegex(@"^namespace\s+(?<name>[\w.]+)\s*;", RegexOptions.Multiline)]
    private static partial Regex FileNamespace();
}
