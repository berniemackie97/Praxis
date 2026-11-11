namespace Praxis.Domain.Text;

public sealed record TextDerivative(
    Guid SourceId, Guid ItemId, int Revision, string ProcessorKey, string SourceSha256,
    string Outcome, string? Content, string? ReasonCode, DateTimeOffset ProducedUtc);
