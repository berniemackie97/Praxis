using System.Text.Json;
using Praxis.Domain.Projects;

namespace Praxis.Application.Projects;

/// <summary>
/// Checks a model's project description against the project itself: every component path and entry
/// point must exist in the file tree, every build command must be declared by a manifest or written
/// in the README. Failing items are dropped individually and reported for correction.
/// </summary>
public static class ProjectVerifier
{
    public static ProjectDraft? Verify(string json, RepositoryMap map)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            JsonElement root = document.RootElement;
            List<string> problems = [];
            int verified = 0;
            List<ProjectComponent> components = [];
            foreach (JsonElement item in Array(root, "components").Take(16))
            {
                string name = Text(item, "name", 80);
                string path = Normalize(Text(item, "path", 300));
                string role = Text(item, "role", 300);
                if (name.Length > 0 && role.Length > 0 && Exists(map, path))
                {
                    components.Add(new ProjectComponent(name, path, role));
                    verified++;
                }
                else
                {
                    problems.Add($"component '{name}' path '{path}' is not in the file list");
                }
            }

            List<string> entryPoints = [];
            foreach (string path in Array(root, "entryPoints").Take(12).Select(item => Normalize(item.GetString() ?? string.Empty)))
            {
                if (Exists(map, path) && !path.EndsWith('/'))
                {
                    entryPoints.Add(path);
                    verified++;
                }
                else
                {
                    problems.Add($"entry point '{path}' is not a file in the list");
                }
            }

            List<string> commands = [];
            foreach (string command in Array(root, "buildCommands").Take(12).Select(item => (item.GetString() ?? string.Empty).Trim().TrimStart('$', ' ')))
            {
                if (command.Length is > 0 and <= 200 && Declared(map, command))
                {
                    commands.Add(command);
                    verified++;
                }
                else
                {
                    problems.Add($"command '{command}' is not declared by a manifest or the README");
                }
            }

            string purpose = Text(root, "purpose", 400);
            string summary = Text(root, "summary", 1_200);
            return new ProjectDraft(purpose, summary, components, entryPoints, commands, verified, problems.Count, problems);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool Exists(RepositoryMap map, string path) =>
        path.Length > 0 && (map.Paths.Contains(path) || map.Paths.Contains(path.TrimEnd('/') + "/"));

    private static bool Declared(RepositoryMap map, string command) =>
        map.Commands.Contains(command) || (command.Length >= 4 && map.Readme.Contains(command, StringComparison.Ordinal));

    private static string Normalize(string path) => path.Trim().Replace('\\', '/').TrimStart('.', '/');

    private static IEnumerable<JsonElement> Array(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() : [];

    private static string Text(JsonElement element, string name, int limit)
    {
        string text = element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!.Trim()
            : string.Empty;
        return text.Length <= limit ? text : text[..limit];
    }
}
