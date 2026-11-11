namespace Praxis.Domain.Verification;

/// <summary>Bounds that keep verification requests reviewable by the owner before anything is sent.</summary>
public static class VerificationLimits
{
    /// <summary>Items one request may carry; also the largest ordinal an owner can exclude.</summary>
    public const int MaximumItemsPerRequest = 12;

    /// <summary>Requests that may wait for owner approval before planning stops creating new ones.</summary>
    public const int MaximumAwaitingRequests = 20;
}
