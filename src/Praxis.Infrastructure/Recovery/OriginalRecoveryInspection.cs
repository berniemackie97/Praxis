namespace Praxis.Infrastructure.Recovery;

public sealed record OriginalRecoveryInspection(int StagedFiles, int PublishedObjects, int ReferencedObjects, int UnreferencedObjects, int MissingObjects, int CorruptObjects);
