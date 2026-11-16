using System.Collections.Concurrent;

namespace HttpResilience.Throttling;

/// <summary>
/// Remembers that a dependency answered 429, so other requests to the same scope wait instead of piling on.
/// The in-memory store covers one process; the Redis store covers every instance.
/// </summary>
public interface IThrottleStateStore
{
    /// <summary>Until when the scope is blocked, or <see langword="null"/> when it is not.</summary>
    ValueTask<DateTimeOffset?> GetBlockedUntilAsync(string scopeKey, CancellationToken cancellationToken);

    /// <summary>Blocks the scope until <paramref name="until"/>; never shortens an existing, later block.</summary>
    ValueTask BlockAsync(string scopeKey, DateTimeOffset until, CancellationToken cancellationToken);
}

/// <summary>Per-process store; the default.</summary>
internal sealed class InMemoryThrottleStateStore(TimeProvider timeProvider) : IThrottleStateStore
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _blocked = new(StringComparer.Ordinal);

    public ValueTask<DateTimeOffset?> GetBlockedUntilAsync(string scopeKey, CancellationToken cancellationToken)
    {
        if (_blocked.TryGetValue(scopeKey, out var until))
        {
            if (until > timeProvider.GetUtcNow())
            {
                return ValueTask.FromResult<DateTimeOffset?>(until);
            }

            // Expired: remove it, but only if no one extended it in the meantime.
            _blocked.TryRemove(KeyValuePair.Create(scopeKey, until));
        }

        return ValueTask.FromResult<DateTimeOffset?>(null);
    }

    public ValueTask BlockAsync(string scopeKey, DateTimeOffset until, CancellationToken cancellationToken)
    {
        _blocked.AddOrUpdate(scopeKey, until, (_, existing) => existing > until ? existing : until);
        return ValueTask.CompletedTask;
    }
}
