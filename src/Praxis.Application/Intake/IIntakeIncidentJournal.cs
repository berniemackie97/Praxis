using Praxis.Domain.Intake;

namespace Praxis.Application.Intake;

/// <summary>Persist and inspect watched-folder faults without writing into source folders.</summary>
public interface IIntakeIncidentJournal
{
    void RecordState(string rootPath, string state);

    IReadOnlyList<IntakeIncident> Recent(int limit);
}
