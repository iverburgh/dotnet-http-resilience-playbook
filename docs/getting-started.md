# Getting started

From nothing to a resilient `HttpClient` in five minutes.

## Prerequisites

- .NET 10 SDK
- Optional: Docker, or any Redis, for the [Redis tests](testing.md#redis-tests)

## 1. Run the tests

```shell
dotnet test HttpResilience.slnx
```

The core tests run on a fake clock: a test that waits two minutes for a `Retry-After` finishes in
milliseconds. Without Docker or `REDIS_CONNECTION_STRING`, the Redis tests that need a server are skipped.
See [testing](testing.md).

## 2. Run the demo

```shell
dotnet run --project samples/PartnerDemo
```

It starts a flaky fake partner in-process and calls it through the pipeline:

```text
GET  /orders/7      503, 503, then 200: retried
     -> 200 OK after 3 call(s), 0.7 s

POST /orders        503: not retried, POST is not idempotent
     -> 503 ServiceUnavailable after 1 call(s), 0.0 s

POST /orders + key  503, then 201: retried, the Idempotency-Key makes it safe
     -> 201 Created after 2 call(s), 0.1 s

GET  /slow          each attempt times out after 1 s
     -> TimeoutRejectedException after 4 call(s), 4.9 s

GET  /catalog       429 with Retry-After: 1, then 200
     -> 200 OK after 2 call(s), 1.1 s
```

The settings are in [appsettings.json](../samples/PartnerDemo/appsettings.json).

## 3. Add it to your app

Reference `src/HttpResilience` (copy the project, or package it). Then:

```csharp
builder.Services.AddHttpClient("partner", c => c.BaseAddress = new Uri("https://api.partner.example/"))
    .AddHttpResilience();
```

That gives every request to `partner` the defaults:

| Setting | Default |
|---|---|
| Attempt timeout | 30 s |
| Transient retries | 3, exponential from 2 s, at most 15 s, with jitter; idempotent requests only |
| Throttle retries | 3, after `Retry-After` (60 s when missing, at most 120 s) |
| Total timeout | computed: 525 s with the defaults |
| Rate limit | none until you add [rules](rate-limit-rules.md) |

Tune per client from configuration:

```csharp
builder.Services.Configure<HttpResilienceOptions>("partner", builder.Configuration.GetSection("Partner:Resilience"));
```

```json
{
  "Partner": {
    "Resilience": {
      "AttemptTimeout": "00:00:10",
      "TransientRetry": { "MaxRetryAttempts": 2 }
    }
  }
}
```

Or in code, where you can use the client's name and services:

```csharp
.AddHttpResilience((options, context) =>
{
    options.TransientRetry.AdditionalPredicate = outcome => outcome.Result?.StatusCode == HttpStatusCode.Conflict;
});
```

Configuration is read first, the callback runs after it. See [configuration](configuration.md).

## 4. Add a rate limit

When a dependency has a published limit, enforce it before it does:

```csharp
builder.Services.AddOutboundThrottlingRules(builder.Configuration.GetSection("OutboundThrottling"));
```

```json
{
  "OutboundThrottling": {
    "Rules": [
      { "Name": "partner", "BaseAddress": "https://api.partner.example", "Algorithm": "SlidingWindow", "PermitLimit": 50, "Window": "00:00:01" }
    ]
  }
}
```

With more than one instance, move the limiters and the 429 state to
[Redis](distributed-throttling-with-redis.md).

## Next

- [Architecture](architecture.md): what each strategy does, and why in this order.
- [Applying it to your solution](applying-to-your-solution.md): a checklist per dependency.
- [Pitfalls](pitfalls.md): read before you go to production.
