using System.Text.Json;
using Praxis.Application.Agents;
using Praxis.Application.Agents.Tools;
using Praxis.Application.Knowledge;
using Praxis.Domain.Policy;
using Praxis.Infrastructure.Persistence;
using Xunit;

namespace Praxis.Infrastructure.Tests.Knowledge;

public sealed partial class GraphProjectionTests
{
    [Fact]
    public async Task AgentGraphReadsTheDesktopProjectionAndPagesItsMembers()
    {
        using SqliteCatalog catalog = Open();
        string inbox = Path.Combine(workspace, "Inbox");
        var source = await Enrich(catalog, "alpha/one.md", "Client NodeOne NodeTwo.", inbox);
        await Accept(catalog, source);
        KnowledgeGraphTool tool = new(catalog.Graph, catalog.Knowledge);
        AgentToolContext context = new(Guid.NewGuid(), "local", true, ToolPermissions.ReadOnly, _ => true);
        using JsonDocument empty = JsonDocument.Parse("{}");
        using JsonDocument overview = JsonDocument.Parse((await tool.InvokeAsync(empty.RootElement, context, CancellationToken.None)).Content);
        GraphRegionMap map = catalog.Graph.KnowledgeRegions(new GraphQuery());
        Assert.Equal(map.Entities, overview.RootElement.GetProperty("entities").GetInt32());
        string region = overview.RootElement.GetProperty("regions")[0].GetProperty("regionId").GetString()!;
        using JsonDocument request = JsonDocument.Parse(JsonSerializer.Serialize(new { regionId = region, limit = 1 }));
        using JsonDocument first = JsonDocument.Parse((await tool.InvokeAsync(request.RootElement, context, CancellationToken.None)).Content);
        Assert.Equal(1, first.RootElement.GetProperty("nodes").GetArrayLength());
        string cursor = first.RootElement.GetProperty("nextCursor").GetString()!;
        using JsonDocument next = JsonDocument.Parse(JsonSerializer.Serialize(new { regionId = region, cursor, limit = 1 }));
        using JsonDocument second = JsonDocument.Parse((await tool.InvokeAsync(next.RootElement, context, CancellationToken.None)).Content);
        Assert.NotEqual(first.RootElement.GetProperty("nodes")[0].GetProperty("entityId").GetString(),
            second.RootElement.GetProperty("nodes")[0].GetProperty("entityId").GetString());
    }

    [Fact]
    public async Task RemoteGraphRequiresCurrentSourceDisclosureAndDoesNotExposeOtherRegions()
    {
        using SqliteCatalog catalog = Open();
        string inbox = Path.Combine(workspace, "Inbox");
        var visible = await Enrich(catalog, "alpha/one.md", "Client NodeOne.", inbox);
        var hidden = await Enrich(catalog, "private/two.md", "SecretOnly.", inbox);
        await Accept(catalog, visible);
        await Accept(catalog, hidden);
        KnowledgeGraphTool tool = new(catalog.Graph, catalog.Knowledge);
        bool permitted = true;
        AgentToolContext context = new(Guid.NewGuid(), "remote", false, ToolPermissions.ReadOnly, id => permitted && id == visible.SourceId);
        using JsonDocument empty = JsonDocument.Parse("{}");
        Assert.Contains("evidence_withheld", (await tool.InvokeAsync(empty.RootElement, context, CancellationToken.None)).Content, StringComparison.Ordinal);
        using JsonDocument scope = JsonDocument.Parse(JsonSerializer.Serialize(new { sourceId = visible.SourceId }));
        string result = (await tool.InvokeAsync(scope.RootElement, context, CancellationToken.None)).Content;
        Assert.Contains("Alpha", result, StringComparison.Ordinal);
        Assert.DoesNotContain("SecretOnly", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Private", result, StringComparison.Ordinal);
        permitted = false;
        Assert.Contains("evidence_withheld", (await tool.InvokeAsync(scope.RootElement, context, CancellationToken.None)).Content, StringComparison.Ordinal);
    }
}
