using Praxis.Domain.Processing;

namespace Praxis.Application.Processing;

public interface IProcessingControls
{
    bool IsPaused(string processorKey);
    void SetPaused(string processorKey, bool paused);
    ProcessingControl Read(string processorKey);
}
