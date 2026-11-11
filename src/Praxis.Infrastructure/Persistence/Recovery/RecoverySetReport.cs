using Praxis.Infrastructure.Persistence.Security.Registry;


namespace Praxis.Infrastructure.Persistence.Recovery;

public sealed record RecoverySetReport(CatalogSnapshotReport Catalog, SecuritySnapshotReport Identity);
