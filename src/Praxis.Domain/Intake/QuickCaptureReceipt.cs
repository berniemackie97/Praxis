using Praxis.Domain.Originals;

namespace Praxis.Domain.Intake;

public sealed record QuickCaptureReceipt(string SourcePath, CapturedOriginal Original);
