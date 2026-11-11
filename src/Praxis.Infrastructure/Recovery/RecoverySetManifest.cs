using Praxis.Infrastructure.Persistence.Security.Registry;


namespace Praxis.Infrastructure.Recovery;

internal sealed record RecoverySetManifest(int FormatVersion, CatalogSnapshotReport Catalog,
    SecuritySnapshotReport Identity);
