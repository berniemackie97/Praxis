using System.Text.Json;
using System.Text.Json.Nodes;
using Praxis.Infrastructure.Inference.Subscription;
using Xunit;

namespace Praxis.Infrastructure.Tests.Inference;

/// <summary>Codex runs structured outputs in strict mode, which rejects schemas that leave properties optional.</summary>
public sealed class StrictSchemaTests
{
    [Fact]
    public void EveryObjectIsClosedAndOptionalPropertiesBecomeNullable()
    {
        using JsonDocument schema = JsonDocument.Parse("""
            {"type":"object","required":["summary"],"properties":{
              "summary":{"type":"string"},
              "kind":{"type":"string","enum":["note","code"]},
              "items":{"type":"array","items":{"type":"object","required":["name"],"properties":{
                "name":{"type":"string"},"score":{"type":["number"]}}}}}}
            """);

        JsonObject strict = JsonNode.Parse(StrictSchema.Convert(schema.RootElement))!.AsObject();

        Assert.False(strict["additionalProperties"]!.GetValue<bool>());
        Assert.Equal(["summary", "kind", "items"], strict["required"]!.AsArray().Select(name => name!.GetValue<string>()));
        Assert.Equal("string", strict["properties"]!["summary"]!["type"]!.GetValue<string>());
        JsonObject kind = strict["properties"]!["kind"]!.AsObject();
        Assert.Equal(["string", "null"], kind["type"]!.AsArray().Select(type => type!.GetValue<string>()));
        Assert.Contains(kind["enum"]!.AsArray(), value => value is null);
        JsonObject item = strict["properties"]!["items"]!["items"]!.AsObject();
        Assert.False(item["additionalProperties"]!.GetValue<bool>());
        Assert.Equal(["name", "score"], item["required"]!.AsArray().Select(name => name!.GetValue<string>()));
        Assert.Equal(["number", "null"], item["properties"]!["score"]!["type"]!.AsArray().Select(type => type!.GetValue<string>()));
    }
}
