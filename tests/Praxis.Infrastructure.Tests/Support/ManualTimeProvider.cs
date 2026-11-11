namespace Praxis.Infrastructure.Tests.Support;

/// <summary>A clock that stays at the time a test sets, for exercising lease expiry and day boundaries.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => UtcNow;
}
