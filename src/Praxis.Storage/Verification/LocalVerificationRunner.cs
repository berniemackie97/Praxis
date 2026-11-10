using Praxis.Inference;
using Praxis.Storage.Catalog;

namespace Praxis.Storage.Verification;

/// <summary>
/// Background service that plans verification requests from deterministic signals and sends
/// owner-approved requests to their provider. A missing sign-in, exhausted plan quota or
/// unreachable provider returns the request to the queue with a delay instead of failing it;
/// a malformed answer fails only that request.
/// </summary>
public sealed class LocalVerificationRunner(
    LocalCatalog catalog, Func<VerificationProvider, IChatModel> models,
    TimeSpan? idlePoll = null, Action<string>? status = null)
{
    private const int MaximumOutputTokens = 4_096;
    private const int ContextTokens = 32_768;
    private static readonly TimeSpan PlanInterval = TimeSpan.FromMinutes(5);
    private readonly TimeSpan idle = idlePoll ?? TimeSpan.FromMinutes(1);
    private readonly Action<string> report = status ?? (_ => { });
    private readonly SemaphoreSlim wakeSignal = new(0, 1);
    private int wakePending;

    /// <summary>The adapters for owner-configured subscription CLIs.</summary>
    public static LocalVerificationRunner CreateDefault(LocalCatalog catalog, Action<string>? status = null) =>
        new(catalog, ProviderModels.Create, status: status);

    public void Wake()
    {
        if (Interlocked.CompareExchange(ref wakePending, 1, 0) == 0)
        {
            wakeSignal.Release();
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var nextPlan = DateTimeOffset.MinValue;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (DateTimeOffset.UtcNow >= nextPlan)
                {
                    var planned = catalog.Verification.Plan();
                    if (planned != 0)
                    {
                        report($"{planned} verification request(s) are waiting for owner approval.");
                    }

                    nextPlan = DateTimeOffset.UtcNow + PlanInterval;
                }

                if (!await ProcessNextAsync(cancellationToken))
                {
                    await wakeSignal.WaitAsync(idle, cancellationToken);
                    Interlocked.Exchange(ref wakePending, 0);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Sends one approved request; returns false when none is ready.</summary>
    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken = default)
    {
        if (catalog.Verification.Claim(DateTimeOffset.UtcNow) is not { } claim)
        {
            return false;
        }

        try
        {
            var model = models(claim.Provider);
            var identity = await model.DescribeAsync(cancellationToken);
            var result = await model.CompleteAsync(new ChatRequest(
                VerificationPrompt.Messages(claim.Payload), VerificationPrompt.Schema,
                MaximumOutputTokens, ContextTokens), cancellationToken);
            var verdicts = VerdictReader.Read(result.Content, claim.Ordinals);
            catalog.Verification.Complete(claim, identity, result, verdicts);
            report($"Verification {claim.RequestId:D} completed by {claim.Provider.ProviderId}: " +
                $"{verdicts.Count} of {claim.Ordinals.Count} item(s) judged.");
        }
        catch (InferenceException exception) when (RetryDelay(exception.Failure) is { } delay)
        {
            catalog.Verification.Release(claim, DateTimeOffset.UtcNow + delay, exception.Code);
            report($"Verification with {claim.Provider.ProviderId} is waiting ({exception.Code}); " +
                $"retrying after {delay.TotalMinutes:0} min." +
                (exception.Failure == InferenceFailure.Unauthenticated ? " Sign in to the provider CLI to resume." : ""));
        }
        catch (InferenceException exception)
        {
            catalog.Verification.Fail(claim, exception.Code);
            report($"Verification {claim.RequestId:D} failed: {exception.Code}.");
        }

        return true;
    }

    private static TimeSpan? RetryDelay(InferenceFailure failure) => failure switch
    {
        InferenceFailure.Unauthenticated => TimeSpan.FromHours(6),
        InferenceFailure.QuotaExhausted => TimeSpan.FromHours(1),
        InferenceFailure.Unavailable or InferenceFailure.Timeout or InferenceFailure.ServerError => TimeSpan.FromMinutes(10),
        _ => null
    };
}
