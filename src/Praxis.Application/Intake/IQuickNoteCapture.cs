using Praxis.Domain.Intake;

namespace Praxis.Application.Intake;

/// <summary>Captures a typed note as a Markdown original in the owner's intake folder.</summary>
public interface IQuickNoteCapture
{
    /// <summary>Writes and captures the note once per <paramref name="captureId"/>; retries return the same receipt.</summary>
    Task<QuickCaptureReceipt> CaptureAsync(Guid captureId, string? title, string text, CancellationToken cancellationToken);
}
