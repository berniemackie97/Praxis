using Praxis.Domain.Models;

namespace Praxis.Application.Inference;

/// <summary>A model that completes structured chat requests.</summary>
public interface IChatModel
{
    /// <summary>Resolves the exact model build that will serve requests.</summary>
    Task<ModelIdentity> DescribeAsync(CancellationToken cancellationToken);

    Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken cancellationToken);
}
