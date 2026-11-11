namespace Praxis.Infrastructure.Persistence.Recovery;

public sealed record OriginalRecoveryInspection(int StagedFiles, int PublishedObjects, int ReferencedObjects, int UnreferencedObjects, int MissingObjects, int CorruptObjects);
