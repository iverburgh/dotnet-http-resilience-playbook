# Observability

Polly already emits `resilience.polly.strategy.events`: that a retry happened. The pipeline adds *why*,
for *which dependency*, and whether the caller was refused in the end.

## Metrics

Meter `HttpResilience` (`HttpResilienceTelemetry.MeterName`):

```csharp
builder.Services.AddOpenTelemetry().WithMetrics(m => m
    .AddMeter(HttpResilienceTelemetry.MeterName)
    .AddMeter("Polly"));
```

| Instrument | Type | Counts | Tags |
|---|---|---|---|
| `resilience.http.throttle.attempts` | counter | every attempt refused by the limiter or the 429 gate, or answered 429 | scope, `throttle.source` |
| `resilience.http.throttle.requests` | counter | logical requests throttled at least once, once per source | scope, `throttle.source` |
| `resilience.http.rejected_requests` | counter | requests that reached the caller as a 429 or a refusal, after the throttle retries ran out | scope, `rejection.reason` |
| `resilience.http.retry.delay` | histogram, s | the wait before each retry | scope, `retry.kind` |
| `resilience.http.limiter.available_permits` | gauge | permits each limiter has left | scope |
| `resilience.http.store.failures` | counter | failed calls to the 429 store or limiter backend; the request went through | `store.operation` |

Tag values:

| Tag | Values |
|---|---|
| scope | `server.address` (host), `url.path_prefix`, `throttle.rule` (rule name, `*` for the catch-all, absent when nothing matched) |
| `throttle.source` | `limiter`: refused locally · `server`: the dependency answered 429 · `cached`: the 429 gate refused |
| `rejection.reason` | `rate_limit` · `server_429` |
| `retry.kind` | `transient` · `throttle` |
| `store.operation` | `get`, `block`: the 429 store · `acquire`: the limiter factory · `limiter`: a Redis limiter |

Code: [HttpResilienceMetrics.cs](../src/HttpResilience/HttpResilienceMetrics.cs).

### Attempts versus requests

One request that is refused twice by the limiter and then succeeds adds **2** to `throttle.attempts` and
**1** to `throttle.requests`. Attempts tell you how much throttling *work* happens; requests tell you how
many *callers* noticed. A high attempts-to-requests ratio means requests wait through several rounds —
your limit may be too low for your traffic.

## What to watch

| Question | Query | Act when |
|---|---|---|
| Do callers get refused? | `rate(resilience.http.rejected_requests)` by `server.address`, `rejection.reason` | anything sustained; `server_429` means your limit is above theirs |
| Is the dependency throttling us? | `throttle.attempts{throttle.source="server"}` | it grows: lower your rule, or ask for more quota |
| Is our own limit the bottleneck? | `throttle.attempts{throttle.source="limiter"}` and `available_permits` near 0 | the dependency could take more |
| Did the 429 gate save calls? | `throttle.attempts{throttle.source="cached"}` | — each is a 429 not collected |
| How long do callers wait? | p95 of `retry.delay` by `retry.kind` | throttle waits approach `MaxRetryAfter` |
| Is protection off? | `store.failures` | **any**: alert, especially with Redis `AllowRequests` |

## Logs

Category `HttpResilience.Pipeline` (`HttpResilienceTelemetry.LogCategory`):

| Id | Level | Message |
|---|---|---|
| 1 | Warning | Transient failure calling {Method} {Host}{Path} ({Outcome}); retry {Attempt} in {DelayMs} ms. |
| 2 | Warning | {Host}{Path} is throttled ({Source}); retry {Attempt} in {DelayMs} ms. |
| 3 | Information | Not sending {Method} to {ScopeKey}: it answered 429 and is blocked for another {RemainingMs} ms. |
| 4 | Warning | Gave up on throttling for {ScopeKey} ({Reason}); the caller gets the refusal. |
| 5 | Warning | No rate limiter for {ScopeKey}; the request is sent without a limit. |
| 6 | Warning | Throttle state store failed on {Operation} for {ScopeKey}; continuing without shared 429 state. |
| 7 | Debug | Not retrying {Method} {Host}{Path} after {StatusCode}: the method is not idempotent and the request is not marked safe to retry. |
| 101 | Warning | Redis rate limiter for {Key} failed; failure mode {Mode}. … |

Paths are logged, query strings are not: a query can carry personal data or a key. Code:
[Log.cs](../src/HttpResilience/Log.cs).

Polly logs every strategy event itself under the `Polly` category, which duplicates events 1 and 2
with less context. Turn it down:

```json
{ "Logging": { "LogLevel": { "Polly": "None", "HttpResilience": "Information" } } }
```

Turn event 7 on (Debug) for a while after introducing the [idempotency guard](transient-retries.md#idempotency):
it lists exactly the calls that are no longer retried.

## Traces

The HTTP client span from `System.Net.Http` is created by the primary handler, inside the pipeline, so
**each attempt is its own span**: a request retried twice shows three client spans under the caller's
span. That is usually what you want to see. To group them, start an activity of your own around the
call. Throttle waits and backoff show up as gaps between the spans. No extra setup is needed for the
pipeline itself.

## Health checks

There is no health check, on purpose. A dependency that throttles you is not a reason to mark *your*
instance unhealthy: restarting it changes nothing, and taking it out of the load balancer moves its
traffic to instances that are throttled just the same. Alert on the metrics instead.
