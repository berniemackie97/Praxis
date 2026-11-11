using Praxis.Domain.Models;

namespace Praxis.Application.Inference;

/// <summary>Opens the local runtime that serves an owner-configured model profile.</summary>
public interface IChatModelConnector
{
    ModelConnection Connect(ModelProfile profile);
}
