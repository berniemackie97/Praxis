using System.Text.Json.Serialization;

namespace Praxis.Application.Links;

/// <summary>One published caption of an online video, in seconds.</summary>
public sealed record WebVideoCue(
    [property: JsonPropertyName("start")] double Start,
    [property: JsonPropertyName("end")] double End,
    [property: JsonPropertyName("text")] string Text);
