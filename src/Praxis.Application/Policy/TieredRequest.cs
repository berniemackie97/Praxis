using Praxis.Application.Inference;

namespace Praxis.Application.Policy;

/// <summary>
/// One structured model request on behalf of a source, attributable to a processing job. A
/// <paramref name="Deadline"/> stops the call early (recorded as a timeout), such as a local draft
/// that is only worth having within its budget.
/// </summary>
public sealed record TieredRequest(Guid? SourceId, Guid? ItemId, int? Revision, string? ProcessorKey, string Purpose, ChatRequest Request, string? KnownCategory = null,
    IReadOnlyList<Guid>? DisclosureSources = null, Func<bool>? SourceScopeCurrent = null, TimeSpan? Deadline = null);
