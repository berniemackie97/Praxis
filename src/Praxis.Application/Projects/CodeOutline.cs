using System.Text.RegularExpressions;

namespace Praxis.Application.Projects;

/// <summary>
/// The declarations of a source file (types, functions, members), found by language-aware
/// patterns in code. A cheap stand-in for reading the file: it costs no tokens and names what the
/// file defines.
/// </summary>
public static partial class CodeOutline
{
    public const int MaximumSymbols = 40;

    public static IReadOnlyList<string> Extract(string text, string extension)
    {
        Regex? pattern = extension.ToLowerInvariant() switch
        {
            ".cs" or ".fs" or ".java" or ".kt" or ".kts" or ".scala" => CLike(),
            ".swift" => Swift(),
            ".ts" or ".tsx" or ".js" or ".jsx" or ".mjs" or ".cjs" => Script(),
            ".py" => Python(),
            ".go" => Go(),
            ".rs" => Rust(),
            ".rb" => Ruby(),
            ".php" => Php(),
            ".c" or ".h" or ".cpp" or ".hpp" or ".cc" or ".m" or ".mm" => Native(),
            _ => null
        };
        if (pattern is null)
        {
            return [];
        }

        List<string> symbols = [];
        foreach (Match match in pattern.Matches(text))
        {
            string symbol = Regex.Replace(match.Value.Trim(), @"\s+", " ", RegexOptions.None, TimeSpan.FromSeconds(1)).TrimEnd('{', ':', ' ', '(');
            if (symbol.Length is > 2 and <= 160 && !symbols.Contains(symbol))
            {
                symbols.Add(symbol);
            }

            if (symbols.Count == MaximumSymbols)
            {
                break;
            }
        }

        return symbols;
    }

    [GeneratedRegex(@"^[ \t]*(?:(?:public|internal|private|protected|static|sealed|abstract|partial|async|override|virtual|data|open|final|record)\s+)*(?:class|interface|struct|record|enum|object|fun|trait)\s+\w+[^\n{=]{0,80}", RegexOptions.Multiline, 1_000)]
    private static partial Regex CLike();

    [GeneratedRegex(@"^[ \t]*(?:(?:public|internal|private|fileprivate|open|static|final|@MainActor|@\w+)\s+)*(?:class|struct|enum|protocol|actor|extension|func)\s+[\w.]+[^\n{]{0,80}", RegexOptions.Multiline, 1_000)]
    private static partial Regex Swift();

    [GeneratedRegex(@"^[ \t]*(?:export\s+)?(?:default\s+)?(?:async\s+)?(?:function\*?|class|interface|type|enum)\s+\w+[^\n{=]{0,80}|^[ \t]*export\s+const\s+\w+", RegexOptions.Multiline, 1_000)]
    private static partial Regex Script();

    [GeneratedRegex(@"^(?:class|def|async def)\s+\w+[^\n:]{0,80}|^    (?:async )?def\s+\w+", RegexOptions.Multiline, 1_000)]
    private static partial Regex Python();

    [GeneratedRegex(@"^(?:func|type)\s+(?:\([^)]{0,60}\)\s*)?\w+[^\n{]{0,80}", RegexOptions.Multiline, 1_000)]
    private static partial Regex Go();

    [GeneratedRegex(@"^[ \t]*(?:pub(?:\([^)]*\))?\s+)?(?:async\s+)?(?:fn|struct|enum|trait|impl|mod)\s+[\w<>]+[^\n{;]{0,80}", RegexOptions.Multiline, 1_000)]
    private static partial Regex Rust();

    [GeneratedRegex(@"^[ \t]*(?:class|module|def)\s+[\w.:]+", RegexOptions.Multiline, 1_000)]
    private static partial Regex Ruby();

    [GeneratedRegex(@"^[ \t]*(?:(?:abstract|final|public|private|protected|static)\s+)*(?:class|interface|trait|function)\s+\w+", RegexOptions.Multiline, 1_000)]
    private static partial Regex Php();

    [GeneratedRegex(@"^(?:class|struct|@interface|@implementation)\s+\w+|^[A-Za-z_][\w \*&:<>]{1,60}\s+\**\w+\s*\([^;{]{0,80}\)\s*(?=\{|$)", RegexOptions.Multiline, 1_000)]
    private static partial Regex Native();
}
