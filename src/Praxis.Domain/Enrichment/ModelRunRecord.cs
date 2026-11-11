using Praxis.Domain.Models;

namespace Praxis.Domain.Enrichment;

/// <summary>Provenance, usage and coverage of a model-produced run.</summary>
public sealed record ModelRunRecord(
    ModelIdentity Model, int InputTokens, int OutputTokens, TimeSpan Duration,
    int SegmentsExamined, int SegmentsTotal, int Rejected, int SegmentsReused);
