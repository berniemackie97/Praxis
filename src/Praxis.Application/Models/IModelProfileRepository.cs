using Praxis.Domain.Models;

namespace Praxis.Application.Models;

/// <summary>The owner's model choices, kept in the private catalog so they travel with backups.</summary>
public interface IModelProfileRepository
{
    ModelProfile? Get(string role);

    /// <summary>Validates, normalizes and stores a profile; returns the stored form.</summary>
    ModelProfile Save(ModelProfile profile);
}
