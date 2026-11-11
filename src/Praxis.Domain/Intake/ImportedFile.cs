using Praxis.Domain.Originals;

namespace Praxis.Domain.Intake;

public sealed record ImportedFile(string SourcePath, CapturedOriginal Original);
