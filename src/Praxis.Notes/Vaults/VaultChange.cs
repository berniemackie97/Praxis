namespace Praxis.Notes.Vaults;

public sealed record VaultChange(Guid RecordId, string? PreviousPath, string CurrentPath, bool IsAttachment, bool ContentChanged, bool PathChanged);
