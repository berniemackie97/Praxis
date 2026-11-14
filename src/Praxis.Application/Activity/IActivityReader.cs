namespace Praxis.Application.Activity;

/// <summary>Reads the catalog's record of work in progress for the owner's Activity view, in one bounded read.</summary>
public interface IActivityReader
{
    ActivityFacts Read(DateTimeOffset now, int recentCalls);
}
