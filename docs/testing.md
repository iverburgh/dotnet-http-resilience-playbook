# Testing

A resilience pipeline is about time: backoff, `Retry-After`, timeouts, windows. Tests that wait for real
time are slow and flaky. These tests run on a fake clock and a scripted dependency, and finish in
seconds.

## The pattern

[TestHost.cs](../tests/HttpResilience.Tests/Pipeline/TestHost.cs) builds a real service provider with a
real `IHttpClientFactory` and the real pipeline. Only two things are fake:

| Fake | Replaces | Why |
|---|---|---|
| `FakeTimeProvider` (`Microsoft.Extensions.TimeProvider.Testing`) | `TimeProvider` | Polly reads the `TimeProvider` from DI for every delay and timeout |
| `FakePartner : HttpMessageHandler` | the primary handler | answers each call from a script and counts the calls |

```csharp
[Test]
public async Task Too_many_requests_waits_for_Retry_After()
{
    var partner = new FakePartner((_, call, _) => Task.FromResult(call == 0
        ? FakePartner.TooManyRequests(TimeSpan.FromSeconds(20))
        : new HttpResponseMessage(HttpStatusCode.OK)));
    using var host = TestHost.Create(partner);
    var started = host.Clock.GetUtcNow();

    using var response = await host.GetAsync("/catalog");

    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    // 20 s from Retry-After, plus up to 10% jitter, plus the clock's 1 s steps.
    Assert.That(host.Clock.GetUtcNow() - started, Is.InRange(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(24)));
}
```

`host.GetAsync` starts the request and then **drives** the clock: it advances one second at a time
until the request completes, with a short real delay between steps so continuations can run. A test
that waits 120 simulated seconds takes 120 small steps — well under two seconds of real time. If the pipeline never completes — because
something waits on real time — `DriveAsync` fails the test after 5,000 steps instead of hanging.

Assert on *simulated* elapsed time to prove a wait happened, and on `partner.Calls` to prove the
dependency was or was not called.

### Other fakes

| Fake | For |
|---|---|
| `ScriptedLimiter` | a limiter that refuses on cue, with or without a retry hint, so limiter tests do not depend on the clock |
| `ScriptedLimiterFactory` | returns the scripted limiter, records the scopes it was asked for, and can throw to test fail-open |
| `CountingHandler` | shows whether a retry re-runs a handler added after the pipeline |
| `BrokenStore` | a 429 store that throws |
| `MetricCollector<T>` (`Microsoft.Extensions.Diagnostics.Testing`) | asserts on counters and tags |

## What is covered

| Area | Tests |
|---|---|
| Transient retry: which outcomes, the idempotency guard, `AdditionalPredicate`, `OnRetry`, handler re-runs | `TransientRetryTests` |
| Attempt and total timeouts, `HttpClient.Timeout` | `TimeoutTests` |
| Throttle retry: `Retry-After`, cap, `POST`, exhaustion marker, metrics, the throttling-only pipeline | `ThrottleRetryTests` |
| The shared 429 gate: blocks, expiry, separate scopes, a broken store | `SharedThrottleGateTests` |
| The rate limiter: hints, exhaustion, matching, fail-open, the gauge | `RateLimiterTests` |
| Named options, validation, binding rules | `ConfigurationTests` |
| `Retry-After` parsing and jitter, the matcher, validation, the in-memory store, limiter creation, retry safety | [BuildingBlockTests.cs](../tests/HttpResilience.Tests/BuildingBlockTests.cs) |

Pipeline tests are in [PipelineTests.cs](../tests/HttpResilience.Tests/Pipeline/PipelineTests.cs).

## Redis tests

[RedisServer.cs](../tests/HttpResilience.Redis.Tests/RedisServer.cs) finds a Redis once per test run:

1. `REDIS_CONNECTION_STRING`, when set — for a local Redis or a CI service container;
2. otherwise a [Testcontainers](https://dotnet.testcontainers.org/) `redis:7.4-alpine` container, when
   Docker is available;
3. otherwise the tests that need Redis are **skipped**, and the rest (registration, validation, failure
   modes) still run. Set `REDIS_TESTS_REQUIRED=true` to make a missing Redis a failure — CI does this on
   Linux, where Docker is always there.

```shell
# Against a Redis you already run:
REDIS_CONNECTION_STRING=localhost:6379 dotnet test tests/HttpResilience.Redis.Tests
```

Each test uses a unique key prefix, so tests run in parallel against one Redis without seeing each
other. Two service providers on the same Redis play two instances: one instance's 429 holds back the
other, and the rate limit holds across both. Failure modes run against `localhost:1` with short
timeouts — no Redis needed.

These tests use the real clock, because the Lua scripts read the Redis clock. Windows are kept short
(hundreds of milliseconds) to keep them fast.

## Testing your own code

Your code calls a client with the pipeline; you want to test your code, not the pipeline.

- **Turn retries off** for most tests: `MaxRetryAttempts = 0` for both retries, or `BaseDelay = 0`
  when the retry is part of what you test. A test that fails on a 503 should not wait 2 + 4 + 8 seconds
  first.
- **Register `FakeTimeProvider`** when the timing matters, and drive it as above.
- **Replace the primary handler** with `ConfigurePrimaryHttpMessageHandler`, not the `HttpClient`: then
  the pipeline your production code relies on is part of the test.
- **Assert on `IsThrottleExhausted()`** or `OutboundThrottleRejectedException` where your code must
  defer work after throttling.
