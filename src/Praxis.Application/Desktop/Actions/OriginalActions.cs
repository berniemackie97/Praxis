using Praxis.Application.Desktop.Contracts;
using Praxis.Application.Originals;

namespace Praxis.Application.Desktop.Actions;

/// <summary>Verification and owner-directed export of immutable originals.</summary>
public sealed class OriginalActions(IOriginalStore originals) : IDesktopActionGroup
{
    public IEnumerable<DesktopAction> Actions =>
    [
        DesktopAction.OfAsync("verify", async (fields, cancellationToken) =>
        {
            await originals.VerifyAsync(fields.RequireGuid("itemId"), cancellationToken);
            return new OriginalVerificationResponse(true);
        }),
        DesktopAction.OfAsync("exportOriginal", async (fields, cancellationToken) =>
            new OriginalExportResponse(await originals.ExportAsync(fields.RequireGuid("itemId"),
                fields.IntegerOrDefault("revision", 1, 1, int.MaxValue),
                fields.RequireString("destinationPath", 4_096), cancellationToken)))
    ];
}
