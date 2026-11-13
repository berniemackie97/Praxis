using System.Text.Json;
using Praxis.Application.Inference;
using Praxis.Application.Knowledge;
using Praxis.Application.Models;
using Praxis.Application.Policy;
using Praxis.Application.Search;
using Praxis.Domain.Agents;
using Praxis.Domain.Models;
using Praxis.Domain.Policy;
using Praxis.Domain.Search;

namespace Praxis.Application.Agents.Tools;

/// <summary>
/// Ranked search across everything captured: hybrid keyword + semantic when a local embedding
/// model is enabled, keyword otherwise. Filters by kind, data category, collection or entity.
/// Results are current, active sources the session may see; withheld results are counted.
/// </summary>
public sealed class KnowledgeSearchTool(
    ITextSearchIndex lexical, ISemanticVectorStore vectors, IModelProfileRepository profiles, IEmbeddingModelConnector embeddings,
    IKnowledgeRepository knowledge, IPolicyRepository policy) : IAgentTool
{
    private static readonly IReadOnlyList<string> Kinds = ["any", "note", "document", "code", "media", "financial", "data"];

    public AgentToolDefinition Definition { get; } = new("knowledge_search",
        """
        Search everything in the owner's second brain. Returns ranked passages with sourceId/itemId/revision for knowledge_open and a citation number.
        Use specific keywords; run several searches for multi-part questions. Filters: kind (note|document|code|media|financial|data), category
        (financial|correspondence|health|identity|personal|documents|media|code), collectionId, entityId (only sources mentioning that entity).
        detail=full adds longer snippets. Results withheld by the owner's disclosure policy are counted, never shown.
        """,
        ToolArguments.Schema("""
            {"type":"object","properties":{
              "query":{"type":"string"},"kind":{"type":"string","enum":["any","note","document","code","media","financial","data"]},
              "category":{"type":"string"},"collectionId":{"type":"string"},"entityId":{"type":"string"},
              "limit":{"type":"integer","minimum":1,"maximum":25},"offset":{"type":"integer","minimum":0,"maximum":200},
              "detail":{"type":"string","enum":["concise","full"]}},"required":["query"]}
            """), ToolTiers.Read);

    public async Task<AgentToolOutput> InvokeAsync(JsonElement arguments, AgentToolContext context, CancellationToken cancellationToken)
    {
        ToolArguments args = new(arguments);
        string query = args.RequireText("query", 256);
        string kind = args.Choice("kind", Kinds, "any");
        string? category = args.Text("category", 32);
        if (category is not null && !DataCategories.IsValid(category))
        {
            throw new ArgumentException($"'category' must be one of: {string.Join(", ", DataCategories.All)}.");
        }

        Guid? collection = args.Guid("collectionId");
        Guid? entity = args.Guid("entityId");
        int limit = args.Integer("limit", 8, 1, 25);
        int offset = args.Integer("offset", 0, 0, 200);
        bool full = args.Choice("detail", ["concise", "full"], "concise") == "full";

        (IReadOnlyList<SearchHit> hits, string mode) = await SearchAsync(query, collection, cancellationToken);
        HashSet<Guid>? entitySources = entity is { } id
            ? (knowledge.GetEntity(id) ?? throw new ArgumentException("No entity with that entityId.")).Mentions.Select(mention => mention.SourceId).ToHashSet()
            : null;
        SearchHit[] filtered = hits.Where(hit => KindMatches(kind, hit.SourceLocator) && (entitySources?.Contains(hit.SourceId) ?? true) &&
                                                 (category is null || (policy.ReadCategory(hit.SourceId)?.Category ?? DataCategories.Unclassified) == category))
            .ToArray();
        SearchHit[] visible = filtered.Where(hit => context.MayDisclose(hit.SourceId)).ToArray();
        SearchHit[] page = visible.Skip(offset).Take(limit).ToArray();
        int withheld = filtered.Length - visible.Length;
        return AgentToolOutput.Of(new
        {
            mode,
            results = page.Select(hit => new
            {
                hit.SourceId,
                hit.ItemId,
                hit.Revision,
                title = hit.Title,
                source = Path.GetFileName(hit.SourceLocator),
                snippet = full || hit.Snippet.Length <= 280 ? hit.Snippet : hit.Snippet[..280] + "…"
            }),
            nextOffset = offset + page.Length < visible.Length ? offset + page.Length : (int?)null
        },
        page.Select(hit => new AgentCitation(hit.SourceId, hit.ItemId, hit.Revision, "search", Path.GetFileName(hit.SourceLocator))).ToArray(),
        withheld > 0 ? [new WithheldNotice(withheld, WithheldNotice.DisclosurePolicy)] : null);
    }

    private async Task<(IReadOnlyList<SearchHit>, string)> SearchAsync(string query, Guid? collection, CancellationToken cancellationToken)
    {
        if (profiles.Get(ModelProfile.LocalEmbedding) is { Enabled: true } profile)
        {
            try
            {
                using EmbeddingModelConnection connection = embeddings.Connect(profile, TimeSpan.FromSeconds(20));
                HybridSearchResults hybrid = await new HybridSearch(lexical, new SemanticRetriever(vectors)).QueryAsync(query, connection.Model, 100, cancellationToken, collection);
                return (hybrid.Hits, hybrid.UsedEmbeddings ? "hybrid" : "keyword");
            }
            catch (InferenceException) when (!cancellationToken.IsCancellationRequested)
            {
                // Fall back to keyword search; the mode tells the agent.
            }
        }

        return (lexical.RetainCurrent(lexical.Query(query, 100, collection), collection), "keyword");
    }

    private static bool KindMatches(string kind, string locator)
    {
        string extension = Path.GetExtension(locator).ToLowerInvariant();
        return kind switch
        {
            "note" => extension is ".md" or ".markdown" or ".txt",
            "document" => extension is ".pdf" or ".docx" or ".rtf" or ".html",
            "code" => extension is ".cs" or ".fs" or ".c" or ".h" or ".cpp" or ".hpp" or ".asm" or ".s" or ".lst" or ".swift" or ".py" or ".js" or ".ts",
            "media" => extension is ".vtt" or ".srt" or ".mp4" or ".mov" or ".mp3" or ".m4a" or ".wav",
            "financial" => extension is ".ofx" or ".qfx" or ".qbo" or ".qif",
            "data" => extension is ".csv" or ".tsv" or ".json",
            _ => true
        };
    }
}
