using Praxis.Domain.Originals;

namespace Praxis.Application.Desktop.Contracts;

/// <summary>A verified original copied to an owner-chosen destination.</summary>
public sealed record OriginalExportResponse(OriginalExport Exported);
