using Praxis.Domain.Intake;

namespace Praxis.Application.Desktop.Contracts;

/// <summary>The receipt of a captured quick note.</summary>
public sealed record NoteCaptureResponse(QuickCaptureReceipt Capture);
