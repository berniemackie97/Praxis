using Praxis.Infrastructure.Security;

namespace Praxis.Infrastructure.Recovery;

public sealed record RecoverySetReport(CatalogSnapshotReport Catalog, SecuritySnapshotReport Identity);
