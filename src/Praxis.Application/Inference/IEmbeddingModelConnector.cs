using Praxis.Domain.Models;

namespace Praxis.Application.Inference;

/// <summary>Opens the local runtime that serves an owner-configured embedding profile.</summary>
public interface IEmbeddingModelConnector
{
    EmbeddingModelConnection Connect(ModelProfile profile, TimeSpan? requestTimeout = null);
}
