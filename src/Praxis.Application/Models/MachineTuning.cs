using Praxis.Application.Policy;
using Praxis.Domain.Models;

namespace Praxis.Application.Models;

/// <summary>
/// Picks local models and limits that fit the machine. On 16 GB a single small model stays loaded
/// (switching models costs minutes when they must be reloaded), drafts run only when measured to
/// pay off, and background enrichment stays off. Larger machines get larger models and more local work.
/// </summary>
public static class MachineTuning
{
    private const long GiB = 1L << 30;

    public static MachinePlan Plan(long memoryBytes, IReadOnlyList<InstalledModel> installed)
    {
        (string profile, long modelBudget, string draftMode, int draftBudget, bool enrichment) = memoryBytes switch
        {
            <= 18 * GiB => (MachinePlan.Efficient, 4L * GiB, LocalDraftModes.Auto, 20, false),
            <= 36 * GiB => (MachinePlan.Balanced, 7L * GiB, LocalDraftModes.Auto, 40, false),
            _ => (MachinePlan.Performance, 16L * GiB, LocalDraftModes.Always, 120, true)
        };
        InstalledModel[] generative = installed.Where(model => model.SupportsChat).ToArray();
        // The largest model that fits the budget gives the best quality the machine can keep loaded;
        // when none fits, the smallest is the least harmful.
        InstalledModel? local = generative.Where(model => model.SizeBytes <= modelBudget).MaxBy(model => model.SizeBytes)
                                ?? generative.MinBy(model => model.SizeBytes);
        InstalledModel? embedding = installed.Where(model => model.SupportsEmbedding).MinBy(model => model.SizeBytes);
        List<string> notes = [];
        if (local is not null && local.SizeBytes > modelBudget)
        {
            notes.Add($"No installed model is small enough to stay loaded comfortably; {local.Name} is the smallest. A 3–4 B model would be faster.");
        }

        if (local is null)
        {
            notes.Add("No local chat model is installed; triage uses format rules only.");
        }

        if (!enrichment)
        {
            notes.Add("Background enrichment is off: the Reading model already does this work, and a second local job slows this Mac.");
        }

        return new MachinePlan(profile, memoryBytes, local?.Name, embedding?.Name, draftMode, draftBudget, enrichment, notes);
    }
}
