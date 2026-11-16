# Timeouts

Two timeouts, and one you switch off.

| Timeout | Where | Cuts off | Default |
|---|---|---|---|
| **Attempt timeout** | innermost strategy | one try at the dependency | 30 s |
| **Total timeout** | outermost strategy | the whole call: attempts, backoff, `Retry-After` waits, limiter queues | computed |
| `HttpClient.Timeout` | `HttpClient` itself | everything, regardless of the pipeline | set to infinite by `AddHttpResilience` |

## Attempt timeout

How long one attempt may take, before the transient retry gets a `TimeoutRejectedException` and decides
whether to try again. Set it just above the dependency's normal worst case — the p99 you see on a good
day, plus margin. Too generous and a hanging dependency holds a connection, a thread and the caller for
the full time on every attempt; too tight and slow-but-fine responses become retries that make the
dependency slower still.

Waiting for a rate-limit permit happens *outside* the attempt timeout: the dependency is not slow
because you were queueing for it.

## Total timeout

The caller's patience, everything included. When `TotalTimeout` is empty, it is computed so that the
retries you configured can actually happen:

```text
total = (T + 1) × AttemptTimeout      every transient attempt may time out
      + T × TransientRetry.MaxDelay   and back off for the longest delay in between
      + R × ThrottleRetry.MaxRetryAfter   and the throttle may wait its maximum R times
```

T is `TransientRetry.MaxRetryAttempts`, R is `ThrottleRetry.MaxRetryAttempts`. With the defaults:
4 × 30 s + 3 × 15 s + 3 × 120 s = **525 s**. Code:
[`GetEffectiveTotalTimeout`](../src/HttpResilience/HttpResilienceOptions.cs).

A budget that leaves out one of the terms is a classic mistake: the transient retries look configured,
but the last one is always cut off by the total timeout and never runs. The formula is a budget for the
realistic case, not the theoretical worst — throttle retries nest inside each transient attempt, so the
true worst case is larger. Whatever happens, the total timeout caps it.

Set `TotalTimeout` explicitly when the caller has a hard deadline — an HTTP request from a user that
the gateway cuts off after 60 seconds gains nothing from a 525-second budget:

```json
{ "AttemptTimeout": "00:00:10", "TotalTimeout": "00:00:45", "ThrottleRetry": { "MaxRetryAfter": "00:00:10" } }
```

Validation rejects a `TotalTimeout` shorter than `AttemptTimeout`.

### One timeout, as before

A client that had a single `HttpClient.Timeout` of five minutes and no retries can keep exactly that:

```csharp
.AddHttpResilience((o, _) =>
{
    o.AttemptTimeout = TimeSpan.FromMinutes(5);
    o.TotalTimeout = TimeSpan.FromMinutes(5);
});
```

Retries now never run after a timeout — there is no time left — but still run after a fast 503.

## `HttpClient.Timeout`

`HttpClient` has its own timeout of 100 seconds, outside every handler. Left at that value it cancels
the pipeline in the middle of a backoff or a `Retry-After` wait, and the caller gets a
`TaskCanceledException` that says nothing about why. `AddHttpResilience` sets it to infinite: the
pipeline owns time. Do not set it back in a later `ConfigureHttpClient`.

## The throttling-only pipeline

`AddOutboundThrottling` is for a client that keeps its own timeouts and retries. It does *not* touch
`HttpClient.Timeout`, because the client's owner may rely on it. Make sure it is longer than the
throttle can wait:

```text
HttpClient.Timeout > ThrottleRetry.MaxRetryAttempts × ThrottleRetry.MaxRetryAfter + the call itself
```

With the defaults that is more than 6 minutes, far beyond the 100-second default. Either raise
`HttpClient.Timeout` or lower the throttle settings for that client:

```csharp
builder.Services.Configure<ThrottleRetryOptions>("legacy", o =>
{
    o.MaxRetryAttempts = 2;
    o.MaxRetryAfter = TimeSpan.FromSeconds(20);
});
builder.Services.AddHttpClient("legacy").AddOutboundThrottling();
```

## The caller's cancellation token

A `CancellationToken` passed to `SendAsync` cancels the pipeline at once, including a wait. The
pipeline reports it as `OperationCanceledException`, not as a timeout, and never retries it. Store and
limiter calls honour the token too: a cancelled request does not count as a store failure.

## Choosing values per dependency

| Dependency | Attempt | Total | Notes |
|---|---|---|---|
| Fast internal API on a user's request path | 2–5 s | ≤ the gateway's timeout | few retries, short `MaxRetryAfter` |
| Public open-data API | 5 s | computed | constant 1 s backoff is enough for a cheap read |
| Partner API from a background worker | p99 + margin | computed, or the message lock duration minus margin | see [layered retries](layered-retries.md) |
| Batch upload that legitimately takes minutes | the upload's worst case | = attempt | retries only after fast failures |

Tested in [TimeoutTests](../tests/HttpResilience.Tests/Pipeline/PipelineTests.cs).
