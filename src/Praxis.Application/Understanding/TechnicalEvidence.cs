using System.Text.Json;
using System.Text.RegularExpressions;
using Praxis.Domain.Enrichment;

namespace Praxis.Application.Understanding;

/// <summary>Recognizes generated technical evidence and extracts explicit, source-bound facts.</summary>
public static partial class TechnicalEvidence
{
    // These are evidence payloads, not prose reports. A generic JSON file outside an evidence
    // folder only takes this path if it has the shape of a disassembly/decompilation export.
    public static bool IsArtifact(string locator, string text)
    {
        string file = Path.GetFileName(locator);
        if (file.EndsWith(".asm.txt", StringComparison.OrdinalIgnoreCase) ||
            file.EndsWith(".pcode", StringComparison.OrdinalIgnoreCase) ||
            file.EndsWith(".hex.txt", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string extension = Path.GetExtension(file).ToLowerInvariant();
        if (extension is not (".json" or ".jsonl" or ".ndjson" or ".xml" or ".csv" or ".tsv" or ".txt" or ".log"))
        {
            return false;
        }

        string prefix = text[..Math.Min(text.Length, 8_192)];
        bool evidenceFolder = (Path.GetDirectoryName(locator) ?? "").Split(Path.DirectorySeparatorChar)
            .Contains("evidence", StringComparer.OrdinalIgnoreCase);
        return (extension is ".json" or ".jsonl" or ".ndjson" or ".xml") && evidenceFolder ||
               extension == ".json" && ExportHeader().IsMatch(prefix) ||
               extension is ".txt" or ".log" && evidenceFolder && GeneratedText().IsMatch(prefix);
    }

    public static IReadOnlyList<ProposalCandidate> Extract(string locator, string text)
    {
        string extension = Path.GetExtension(locator).ToLowerInvariant();
        var result = new List<ProposalCandidate>();
        if (extension is ".md" or ".markdown" or ".mdown" or ".mkd")
        {
            foreach (Match heading in ClassHeading().Matches(text))
            {
                foreach (Match name in Symbol().Matches(heading.Groups["names"].Value).Where(name => name.Value != "and"))
                {
                    Add("entity", "class", name.Value, heading);
                }
            }
        }

        if (extension == ".json" && ExportHeader().IsMatch(text[..Math.Min(text.Length, 8_192)]))
        {
            string name = Path.GetFileNameWithoutExtension(locator);
            // The filename identifies the capture, and the call must occur inside a disassembly
            // payload. Comments, decompiler snippets, registers and unresolved addresses are not calls.
            if (name.Length <= ProposalLimits.MaximumValueLength && FunctionLabel().IsMatch(name) &&
                ReadAssembly(text) is { } assembly)
            {
                foreach (Match call in AssemblyCall().Matches(assembly.Raw))
                {
                    string target = call.Groups["target"].Value;
                    if (!RegisterOrAddress().IsMatch(target) && !name.Equals(target, StringComparison.OrdinalIgnoreCase))
                    {
                        Add("relation", null, "calls", call, name, target, assembly.Offset);
                        Add("entity", "function", name, call, offset: assembly.Offset);
                        Add("entity", "function", target, call, offset: assembly.Offset);
                    }
                }
            }
        }

        // A filename in a report is a module only when it names a binary or asset. A prose
        // occurrence alone gives no basis for assuming a class or a function.
        if (extension is ".md" or ".markdown" or ".mdown" or ".mkd")
        {
            foreach (Match module in BinaryName().Matches(text))
            {
                Add("entity", "binary_module", module.Value, module);
            }

            foreach (Match asset in DataAsset().Matches(text))
            {
                Add("entity", "data_asset", asset.Groups["name"].Value, asset);
            }

            foreach (Match declaration in Inheritance().Matches(text))
            {
                string child = declaration.Groups["child"].Value;
                string parent = declaration.Groups["parent"].Value;
                if (!child.Equals(parent, StringComparison.OrdinalIgnoreCase))
                {
                    Add("relation", null, "inherits_from", declaration, child, parent);
                    Add("entity", "class", child, declaration);
                    Add("entity", "class", parent, declaration);
                }
            }
        }

        var unique = result.DistinctBy(DocumentUnderstander.CandidateKey).ToArray();
        // Include both endpoint mentions for every retained call so scoped graph views can draw it.
        var retained = new List<ProposalCandidate>();
        foreach (ProposalCandidate relation in unique.Where(candidate => candidate.Kind == "relation"))
        {
            string type = relation.Value == "calls" ? "function" : "class";
            var needed = unique.Where(candidate => candidate.Kind == "entity" && candidate.EntityType == type &&
                (candidate.Value == relation.RelationSubject || candidate.Value == relation.RelationObject) && !retained.Contains(candidate)).ToArray();
            if (retained.Count + needed.Length + 1 > ProposalLimits.MaximumPerRun)
            {
                break;
            }

            retained.Add(relation);
            retained.AddRange(needed);
        }

        return retained.Concat(unique.Where(candidate => candidate.Kind == "entity" && !retained.Contains(candidate)))
            .Take(ProposalLimits.MaximumPerRun).ToArray();

        void Add(string kind, string? type, string value, Match span, string? subject = null, string? target = null, int offset = 0)
        {
            if (span.Length is > 0 and <= ProposalLimits.MaximumEvidenceLength && value.Length <= ProposalLimits.MaximumValueLength &&
                !InstructionLikeText.Matches(span.Value, 0, span.Length))
            {
                result.Add(new ProposalCandidate(kind, type, value, offset + span.Index, span.Length, subject, target));
            }
        }
    }

    private static (string Raw, int Offset)? ReadAssembly(string source)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(source, new JsonDocumentOptions { MaxDepth = 24 });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (root.TryGetProperty("asm", out JsonElement nested))
            {
                root = nested;
            }

            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("content", out JsonElement content) ||
                content.ValueKind != JsonValueKind.Array || content.GetArrayLength() != 1)
            {
                return null;
            }

            JsonElement entry = content[0];
            if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("type", out JsonElement type) ||
                type.ValueKind != JsonValueKind.String || type.GetString() != "text" ||
                !entry.TryGetProperty("text", out JsonElement payload) || payload.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            string raw = payload.GetRawText();
            if (!raw.StartsWith("\"File: ", StringComparison.Ordinal) ||
                (!raw.Contains("\\n# Block 1 at ", StringComparison.Ordinal) && !raw.Contains("\\r\\n# Block 1 at ", StringComparison.Ordinal)))
            {
                return null;
            }

            int offset = source.IndexOf(raw, StringComparison.Ordinal);
            return offset < 0 ? null : (raw, offset);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [GeneratedRegex("""(?s)"(?:type|text)"\s*:\s*"text".{0,256}"text"\s*:\s*"File: [^"\r\n]{1,1000}\.bndb(?:\\r)?\\n""", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex ExportHeader();

    [GeneratedRegex("""(?m)^#{1,4}[ \t]+(?<names>`?[A-Za-z_][A-Za-z_0-9]{0,119}`?(?:[ \t]*(?:,|&|and)[ \t]*`?[A-Za-z_][A-Za-z_0-9]{0,119}`?)*)[ \t]+Class(?:es)?\b[^\r\n]{0,100}""", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex ClassHeading();

    [GeneratedRegex("""[A-Za-z_][A-Za-z_0-9]*""", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex Symbol();

    [GeneratedRegex("""^[A-Za-z][A-Za-z0-9]*(?:_[A-Za-z_0-9]+)+$""", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex FunctionLabel();

    [GeneratedRegex("""(?<!\\)\\n[0-9a-fA-F]{6,16}[ \t]+(?:[0-9a-fA-F]{2}[ \t]+){1,15}call[ \t]+(?:dword[ \t]+\[)?(?<target>[A-Za-z_][A-Za-z_0-9]{0,119})(?:\])?(?=[ \t]*(?:;|\\r|\\n|"))""", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex AssemblyCall();

    [GeneratedRegex("""^(?:[er]?(?:ax|bx|cx|dx|si|di|sp|bp)|r(?:[0-9]|1[0-5])[dwb]?|(?:sub|loc|unk)_[0-9a-f]+)$""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex RegisterOrAddress();

    [GeneratedRegex("""\b[A-Za-z_0-9][A-Za-z_0-9.-]{0,100}\.(?:dll|exe|dylib|so)\b(?!\.)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex BinaryName();

    [GeneratedRegex("""`(?<name>[A-Za-z_0-9][A-Za-z_0-9.-]{0,100}\.(?:dat|ani|pul|dmap|wdf|c3))`""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex DataAsset();

    [GeneratedRegex("""(?m)^[ \t]*(?:class|struct)[ \t]+(?<child>[A-Za-z_][A-Za-z_0-9]{0,119})[ \t]*:[ \t]*(?:(?:public|private|protected)[ \t]+)?(?<parent>[A-Za-z_][A-Za-z_0-9]{0,119})[ \t]*\{""", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex Inheritance();

    [GeneratedRegex("""(?mi)^(?:listing archive:|path = .+\.7z|[0-9a-f]{8}:[ \t]+(?:[0-9a-f]{2}[ \t]+){8})""", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex GeneratedText();
}
