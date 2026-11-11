using Praxis.Infrastructure.Persistence.Security.Registry;


namespace Praxis.Infrastructure.Persistence.Recovery;

internal sealed record RecoverySetManifest(int FormatVersion, CatalogSnapshotReport Catalog,
    SecuritySnapshotReport Identity);
