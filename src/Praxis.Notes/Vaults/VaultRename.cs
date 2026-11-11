namespace Praxis.Notes.Vaults;

public sealed record VaultRename(string FromPath, string ToPath, bool IsFolder = false);
