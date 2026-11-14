using Praxis.Application.Inference;

namespace Praxis.Application.Policy;

/// <summary>One structured model request on behalf of a source, attributable to a processing job.</summary>
public sealed record TieredRequest(Guid? SourceId, Guid? ItemId, int? Revision, string? ProcessorKey, string Purpose, ChatRequest Request, string? KnownCategory = null,
    IReadOnlyList<Guid>? DisclosureSources = null, Func<bool>? SourceScopeCurrent = null);
