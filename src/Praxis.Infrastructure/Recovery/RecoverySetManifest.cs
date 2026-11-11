using Praxis.Infrastructure.Security;

namespace Praxis.Infrastructure.Recovery;

internal sealed record RecoverySetManifest(int FormatVersion, CatalogSnapshotReport Catalog,
    SecuritySnapshotReport Identity);
