using Praxis.Application.Desktop.Contracts;
using Praxis.Application.Library;
using Praxis.Application.Processing;
using Praxis.Application.Sources;
using Praxis.Application.Text;
using Praxis.Domain.Processing;
using Praxis.Domain.Text;

namespace Praxis.Application.Desktop.Actions;

/// <summary>Library browsing, text preview, source history, revocation and processing retry.</summary>
public sealed class LibraryActions(
    ILibraryReader library, ITextDerivativeRepository text, ISourceRepository sources, IProcessingQueue queue,
    ProcessingPipeline? processing = null) : IDesktopActionGroup
{
    private const int PreviewCharacters = 12_000;

    public IEnumerable<DesktopAction> Actions =>
    [
        DesktopAction.Of("library", fields => new LibraryResponse(library.List(
            fields.IntegerOrDefault("limit", 100, 1, 200),
            fields.IntegerOrDefault("offset", 0, 0, 1_000_000),
            fields.OptionalGuid("collectionId")))),
        DesktopAction.Of("preview", Preview),
        DesktopAction.Of("sourceMoves", fields => new SourceMovesResponse(sources.ListMovements(
            fields.RequireGuid("sourceId"), fields.IntegerOrDefault("limit", 20, 1, 100)))),
        DesktopAction.Of("revoke", fields => new RevokeResponse(sources.Revoke(fields.RequireGuid("sourceId")))),
        DesktopAction.Of("retry", Retry)
    ];

    private PreviewResponse Preview(DesktopRequestFields fields)
    {
        Guid itemId = fields.RequireGuid("itemId");
        int revision = fields.IntegerOrDefault("revision", 1, 1, int.MaxValue);
        TextDerivative? derivative = text.Read(itemId, revision);
        return derivative is null
            ? new PreviewResponse("unavailable", null, null)
            : new PreviewResponse(derivative.Outcome,
                derivative.Content is { } content ? content[..Math.Min(content.Length, PreviewCharacters)] : null,
                derivative.ReasonCode);
    }

    private RetryResponse Retry(DesktopRequestFields fields)
    {
        bool queued = queue.Retry(fields.RequireGuid("itemId"), fields.IntegerOrDefault("revision", 1, 1, int.MaxValue), ProcessorKeys.SourceText);
        if (queued)
        {
            processing?.Wake();
        }

        return new RetryResponse(queued);
    }
}
