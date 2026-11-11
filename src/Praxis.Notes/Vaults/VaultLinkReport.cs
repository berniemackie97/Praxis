using Praxis.Notes.Model;

namespace Praxis.Notes.Vaults;

public sealed record VaultLinkReport(IReadOnlyList<AttachmentReference> References, IReadOnlyList<VaultLinkProblem> Problems);
