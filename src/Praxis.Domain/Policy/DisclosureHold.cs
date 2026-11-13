namespace Praxis.Domain.Policy;

/// <summary>An intake step waiting, until <see cref="DeadlineUtc"/>, for the owner to allow a disclosure.</summary>
public sealed record DisclosureHold(
    Guid HoldId, Guid SourceId, string SourceName, Guid ItemId, int Revision, string ProcessorKey, string Category,
    string ProviderId, string Purpose, string Status, DateTimeOffset DeadlineUtc, DateTimeOffset CreatedUtc)
{
    public const string Open = "open";
    public const string ApprovedOnce = "approved_once";
    public const string Declined = "declined";
    public const string Expired = "expired";
}
