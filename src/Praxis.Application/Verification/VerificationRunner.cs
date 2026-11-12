using Praxis.Application.Inference;
using Praxis.Domain.Models;
using Praxis.Domain.Verification;

namespace Praxis.Application.Verification;

/// <summary>
/// Background service that plans verification requests from deterministic signals and sends
/// owner-approved requests to their provider. A missing sign-in, exhausted plan quota or
/// unreachable provider returns the request to the queue with a delay instead of failing it;
/// a malformed answer fails only that request.
/// </summary>
public sealed class VerificationRunner(
    IVerificationRepository repository, IVerifierModelFactory models, TimeProvider clock,
    TimeSpan? idlePoll = null, Action<string>? status = null)
{
    private const int MaximumOutputTokens = 4_096;
    private const int ContextTokens = 32_768;
    private static readonly TimeSpan PlanInterval = TimeSpan.FromMinutes(5);
    private readonly TimeSpan idle = idlePoll ?? TimeSpan.FromMinutes(1);
    private readonly Action<string> report = status ?? (_ => { });
    private readonly SemaphoreSlim wakeSignal = new(0, 1);
    private int wakePending;

    public void Wake()
    {
        if (Interlocked.CompareExchange(ref wakePending, 1, 0) == 0)
        {
            wakeSignal.Release();
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset nextPlan = DateTimeOffset.MinValue;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (clock.GetUtcNow() >= nextPlan)
                {
                    int planned = repository.Plan();
                    if (planned != 0)
                    {
                        report($"{planned} verification request(s) are waiting for owner approval.");
                    }

                    nextPlan = clock.GetUtcNow() + PlanInterval;
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
        if (repository.Claim() is not { } claim)
        {
            return false;
        }

        try
        {
            IReadOnlyList<ChatMessage> messages = VerificationPrompt.Messages(claim.Payload);
            if (messages.Sum(message => System.Text.Encoding.UTF8.GetByteCount(message.Content)) >
                ContextTokens - MaximumOutputTokens - 1_024)
            {
                repository.Fail(claim, "model_context_exceeded");
                report("Verification context exceeds its bounded budget; narrow the reviewed items.");
                return true;
            }

            using ModelConnection connection = models.Create(claim.Provider);
            IChatModel model = connection.Model;
            ModelIdentity identity = await model.DescribeAsync(cancellationToken);
            ChatResult result = await model.CompleteAsync(new ChatRequest(
                messages, VerificationPrompt.Schema, MaximumOutputTokens, ContextTokens), cancellationToken);
            if (claim.Provider.IsLocal &&
                (await model.DescribeAsync(cancellationToken)).Digest != identity.Digest)
            {
                throw new InferenceException(InferenceFailure.InvalidResponse,
                    "The local model changed during verification.");
            }

            IReadOnlyList<VerificationVerdict> verdicts = VerdictReader.Read(result.Content, claim.Ordinals);
            if (repository.Complete(claim, identity, result, verdicts))
            {
                report($"Verification completed: {verdicts.Count} of {claim.Ordinals.Count} item(s) judged.");
            }
            else
            {
                report("Verification result discarded because the approval changed.");
            }
        }
        catch (InferenceException exception) when (RetryDelay(exception.Failure) is { } delay)
        {
            if (repository.Release(claim, clock.GetUtcNow() + delay, exception.Code))
            {
                report($"Verification runtime unavailable; retrying after {delay.TotalMinutes:0} min." +
                    (exception.Failure == InferenceFailure.Unauthenticated ? " Sign in to the provider CLI to resume." : ""));
            }
            else
            {
                report("Verification runtime result discarded because the approval changed.");
            }
        }
        catch (InferenceException exception)
        {
            if (repository.Fail(claim, exception.Code))
            {
                report("Verification failed; inspect owner-local verification status.");
            }
            else
            {
                report("Verification runtime result discarded because the approval changed.");
            }
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
