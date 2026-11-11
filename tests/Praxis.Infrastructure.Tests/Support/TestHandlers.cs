using Praxis.Application.Enrichment;
using Praxis.Application.Inference;
using Praxis.Domain.Models;
using Praxis.Infrastructure.Persistence;

namespace Praxis.Infrastructure.Tests.Support;

/// <summary>Processing handlers over an open test catalog with scripted model runtimes.</summary>
internal static class TestHandlers
{
    internal static SemanticExtractionHandler SemanticExtraction(SqliteCatalog catalog, Func<ModelProfile, ModelConnection> connect) =>
        new(catalog.Models, catalog.Text, catalog.Segments, catalog.Proposals, catalog.Knowledge, catalog.Processing,
            new DelegatingChatModelConnector(connect));

    internal static HeadingProposalHandler HeadingProposals(SqliteCatalog catalog) =>
        new(catalog.Text, catalog.Proposals, catalog.Processing);

    private sealed class DelegatingChatModelConnector(Func<ModelProfile, ModelConnection> connect) : IChatModelConnector
    {
        public ModelConnection Connect(ModelProfile profile) => connect(profile);
    }
}
