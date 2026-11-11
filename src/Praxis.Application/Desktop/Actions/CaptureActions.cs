using Praxis.Application.Desktop.Contracts;
using Praxis.Application.Intake;
using Praxis.Application.Processing;
using Praxis.Domain.Intake;

namespace Praxis.Application.Desktop.Actions;

/// <summary>Quick notes typed by the owner, captured into the primary intake folder.</summary>
public sealed class CaptureActions(
    IQuickNoteCapture? quickCapture = null, IIntakeSupervisor? intake = null, ProcessingPipeline? processing = null) : IDesktopActionGroup
{
    public IEnumerable<DesktopAction> Actions =>
    [
        DesktopAction.OfAsync("noteCapture", CaptureNoteAsync)
    ];

    private async Task<NoteCaptureResponse> CaptureNoteAsync(DesktopRequestFields fields, CancellationToken cancellationToken)
    {
        intake?.EnsurePrimaryRootApproved();
        QuickCaptureReceipt capture = await (quickCapture ?? throw new InvalidOperationException("Quick capture is not configured."))
            .CaptureAsync(fields.RequireGuid("captureId"), fields.OptionalText("title", 80, "The note title is invalid."),
                fields.RequireString("text", 8_000), cancellationToken);
        processing?.Wake();
        return new NoteCaptureResponse(capture);
    }
}
