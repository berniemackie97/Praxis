using Praxis.Infrastructure.Persistence.Security.Registry;


namespace Praxis.Infrastructure.Recovery;

public sealed record RecoverySetReport(CatalogSnapshotReport Catalog, SecuritySnapshotReport Identity);
