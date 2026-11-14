namespace Praxis.Application.Policy;

/// <summary>
/// How fast the local tier actually is on this machine, learned from its own calls: seconds per
/// thousand characters read plus a fixed cost per call (model load, first token). Used to decide
/// whether a local draft can finish within the owner's budget before asking for one.
/// </summary>
public sealed class LocalPace
{
    private const double Smoothing = 0.3;
    private readonly Lock gate = new();
    private double? secondsPerThousand;
    private int samples;

    /// <summary>Records one local call over <paramref name="characters"/> of input that took <paramref name="duration"/>.</summary>
    public void Observe(int characters, TimeSpan duration)
    {
        if (characters <= 0 || duration <= TimeSpan.Zero)
        {
            return;
        }

        double rate = duration.TotalSeconds / (characters / 1000.0);
        lock (gate)
        {
            secondsPerThousand = secondsPerThousand is { } current ? current + (Smoothing * (rate - current)) : rate;
            samples++;
        }
    }

    /// <summary>A local call failed by timing out: count it as slow.</summary>
    public void ObserveTimeout(int characters, TimeSpan budget) => Observe(characters, budget * 2);

    /// <summary>The estimated time to read <paramref name="characters"/>, or null before any measurement.</summary>
    public TimeSpan? Estimate(int characters)
    {
        lock (gate)
        {
            return secondsPerThousand is { } rate ? TimeSpan.FromSeconds(rate * characters / 1000.0) : null;
        }
    }

    /// <summary>Seconds per thousand characters measured so far, for the owner, or null.</summary>
    public double? SecondsPerThousandCharacters
    {
        get
        {
            lock (gate)
            {
                return samples == 0 ? null : secondsPerThousand;
            }
        }
    }
}
