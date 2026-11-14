namespace Praxis.Application.Models;

/// <summary>Finds and inspects the model tools installed on this Mac, without sending anything anywhere.</summary>
public interface IModelToolDetector
{
    /// <summary>Every supported tool, found or not, inspected in parallel within a short budget.</summary>
    Task<IReadOnlyList<ModelTool>> DetectAsync(CancellationToken cancellationToken);

    /// <summary>Inspects one tool at an owner-chosen path.</summary>
    Task<ModelTool> InspectAsync(string toolId, string path, CancellationToken cancellationToken);
}
