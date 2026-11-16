# Rate-limit rules

Reacting to 429s is the minimum. When a dependency publishes its limit — or falls over long before it
answers 429 — enforce the limit on your side, before the request leaves.

## A rule

```json
{
  "OutboundThrottling": {
    "Rules": [
      {
        "Name": "partner-orders",
        "BaseAddress": "https://api.partner.example/orders",
        "Algorithm": "SlidingWindow",
        "PermitLimit": 20,
        "Window": "00:00:01"
      },
      {
        "Name": "partner",
        "BaseAddress": "https://api.partner.example",
        "Algorithm": "TokenBucket",
        "PermitLimit": 100,
        "TokensPerPeriod": 50,
        "ReplenishmentPeriod": "00:00:01"
      }
    ],
    "CatchAll": { "Algorithm": "Concurrency", "PermitLimit": 32, "QueueLimit": 64 }
  }
}
```

```csharp
builder.Services.AddOutboundThrottlingRules(builder.Configuration.GetSection(OutboundThrottlingOptions.SectionName));
```

Rules belong to the **application**, not to a client: two clients that call the same API share its
limit. Each rule gets one limiter, shared by every `HttpClient` in the process. Rules are read once;
changing them needs a restart.

## Matching

A request is matched on host and path:

| Rule | Request | Match |
|---|---|---|
| `https://api.partner.example/orders` | `/orders`, `/orders/7`, `/ORDERS/7` | yes |
| `https://api.partner.example/orders` | `/orders-archive` | no: segment boundary |
| `https://api.partner.example/orders` | `http://api.partner.example/orders` | yes: the scheme is ignored |
| `https://api.partner.example:8443` | `https://api.partner.example/` | no: a non-default port is part of the host |

The longest matching prefix wins, so `/orders` above takes precedence over the host-wide `partner` rule.
A request no rule matches falls to the **catch-all**, which applies *per host*: every host gets its own
limiter with the catch-all's settings. Without a catch-all, unmatched requests are not rate limited —
the [429 gate](throttling-and-retry-after.md#the-shared-429-gate) still protects them.

The match is a **throttle scope** with a key like `api.partner.example/orders`. The limiter, the 429
gate, the metrics (`server.address`, `url.path_prefix`, `throttle.rule`) and the Redis keys all use it.
Code: [ThrottleRuleMatcher.cs](../src/HttpResilience/Throttling/ThrottleRuleMatcher.cs).

## Algorithms

| Algorithm | Limits | Retry hint | Use for |
|---|---|---|---|
| `SlidingWindow` (default) | `PermitLimit` per rolling `Window` | yes | a published "N requests per second" limit |
| `FixedWindow` | `PermitLimit` per fixed `Window` | yes | a limit the dependency counts per calendar window; allows a burst at each edge |
| `TokenBucket` | bursts up to `PermitLimit`, refilled with `TokensPerPeriod` per `ReplenishmentPeriod` | yes | a dependency that tolerates bursts but not a sustained rate |
| `Concurrency` | `PermitLimit` requests in flight | no | a slow dependency: a bulkhead |

In-process they are the limiters from `System.Threading.RateLimiting`
([RateLimiterFactory.cs](../src/HttpResilience/Throttling/RateLimiterFactory.cs)).

## What happens without a permit

The attempt is refused locally with `OutboundThrottleRejectedException` (`Reason = RateLimit`), and the
[throttle retry](throttling-and-retry-after.md) waits for the limiter's retry hint — the moment a permit
frees up — plus jitter. A refusal without a hint (concurrency) waits `DefaultRetryAfter`, which is far
too long for a bulkhead. Use a queue instead:

### Concurrency with a queue: the bulkhead

```json
{ "Algorithm": "Concurrency", "PermitLimit": 8, "QueueLimit": 32 }
```

At most 8 requests are in flight; up to 32 more wait in line for a free slot, in order, without a
retry; the 41st is refused. This is the answer to a slow dependency that eats your workers: when it
takes 13 seconds per call, an unbounded worker pool ends up with every worker waiting on it, and work
for healthy dependencies starves. A concurrency limit per dependency keeps the damage in one lane.

The queue wait counts against the [total timeout](timeouts.md), not the attempt timeout.

## Several instances

In-process limiters count per instance. Three instances with `PermitLimit: 20` send up to 60 requests per
second. Either divide the limit by the instance count — and accept that scaling out changes it — or
share the limiters through [Redis](distributed-throttling-with-redis.md).

## Validation

Mistakes fail at startup, not as silent non-limits:

- `Name` is required; `BaseAddress` must be absolute.
- Two rules with the same host and prefix fail with "another rule already covers".
- `PermitLimit` must be positive, `QueueLimit` not negative.
- Window algorithms need a positive `Window` (and `SegmentsPerWindow` for the sliding window); a token
  bucket needs positive `TokensPerPeriod` and `ReplenishmentPeriod`.

Code: [OutboundThrottlingOptionsValidator.cs](../src/HttpResilience/Throttling/OutboundThrottlingOptionsValidator.cs).

## When the limiter breaks

A limiter that throws — a custom factory, or Redis that is down — lets the request through, logs
event 5 and counts `resilience.http.store.failures{store.operation="acquire"}`. See
[fail open](architecture.md#fail-open).

## Choosing a limit

- Start from the dependency's documented limit, minus a margin for other consumers of the same quota.
- No documented limit? Measure: the rate at which latency starts to climb is a better limit than the
  rate at which 429s start.
- Watch `resilience.http.limiter.available_permits` and `resilience.http.throttle.attempts{throttle.source="limiter"}`
  after go-live. A limiter that never refuses is either right or too generous; one that refuses
  constantly is throttling your own throughput. See [observability](observability.md).

Tested in [RateLimiterTests](../tests/HttpResilience.Tests/Pipeline/PipelineTests.cs) and
[ThrottleRuleMatcherTests](../tests/HttpResilience.Tests/BuildingBlockTests.cs).
