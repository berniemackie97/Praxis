using Praxis.Domain.Models;

namespace Praxis.Application.Inference;

/// <summary>Lists the models installed in a local runtime so the owner can choose one.</summary>
public interface ILocalModelDirectory
{
    Task<IReadOnlyList<InstalledModel>> ListInstalledAsync(string endpoint, CancellationToken cancellationToken);
}
