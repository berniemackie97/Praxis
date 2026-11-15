namespace Praxis.Application.Desktop.Contracts;

public sealed record VerificationBatchApprovalResponse(IReadOnlyList<Guid> Queued, IReadOnlyList<Guid> Skipped);
