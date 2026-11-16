using System.Net.Http.Headers;

namespace HttpResilience.Throttling;

/// <summary>Turns a <c>Retry-After</c> header, or its absence, into a wait.</summary>
internal static class RetryAfter
{
    /// <summary>The smallest jitter range, so short waits are still spread.</summary>
    internal static readonly TimeSpan MinimumJitterRange = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// The wait the server asked for: a delta in seconds or an HTTP date. A date in the past means "now".
    /// No header, or one that cannot be read, gives <paramref name="defaultRetryAfter"/>. Never longer than
    /// <paramref name="maxRetryAfter"/>.
    /// </summary>
    public static TimeSpan Parse(RetryConditionHeaderValue? header, DateTimeOffset now, TimeSpan defaultRetryAfter, TimeSpan maxRetryAfter)
    {
        TimeSpan wait;
        if (header?.Delta is { } delta)
        {
            wait = delta;
        }
        else if (header?.Date is { } date)
        {
            wait = date - now;
        }
        else
        {
            wait = defaultRetryAfter;
        }

        return Clamp(wait, maxRetryAfter);
    }

    public static TimeSpan Clamp(TimeSpan wait, TimeSpan maxRetryAfter)
    {
        if (wait < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return wait > maxRetryAfter ? maxRetryAfter : wait;
    }

    /// <summary>
    /// Adds 0–10% (at least <see cref="MinimumJitterRange"/>) on top of <paramref name="wait"/>. Only ever
    /// adds: a retry before the moment the server named is a wasted request, and often a fresh 429.
    /// </summary>
    /// <param name="wait">The wait the server asked for.</param>
    /// <param name="random">A number in [0, 1).</param>
    public static TimeSpan AddJitter(TimeSpan wait, double random)
    {
        var range = Math.Max(wait.Ticks / 10, MinimumJitterRange.Ticks);
        return wait + TimeSpan.FromTicks((long)(range * random));
    }
}
