# Pitfalls

Mistakes that look fine in review and show up in production. Most were made at least once on the way
to this design.

## Timeouts

### A total timeout that forgets a retry

The total budget must cover every attempt *and* every wait. A formula that only counted the throttle
retries cut transient retries short: the third attempt started, and the total timeout killed it halfway.
Let the pipeline [compute it](timeouts.md#total-timeout), or check yours against
`GetEffectiveTotalTimeout()`.

### `HttpClient.Timeout` still at 100 seconds

`HttpClient.Timeout` wraps the whole handler chain. Leave it at 100 seconds and a `Retry-After: 60` plus
one more attempt fails with a `TaskCanceledException` that looks like a timeout of the dependency.
`AddHttpResilience` sets it to infinite. `AddOutboundThrottling` does not — the client keeps its own
timeout, and you must make room for the throttle waits yourself. See
[`HttpClient.Timeout`](timeouts.md#httpclienttimeout).

### A pipeline longer than the message lock

A message handler that calls a client with a nine-minute total timeout under a five-minute lock loses the
lock, and the message is processed twice at once. Keep the pipeline well inside the lock. See
[layered retries](layered-retries.md#rules).

## Retries

### Retrying a `POST` that succeeded

The client saw a timeout; the server created the order. A blind retry creates it again. Non-idempotent
methods are not retried by default — mark the ones that are safe, and send an `Idempotency-Key` where the
dependency supports one. See [idempotency](transient-retries.md#idempotency).

### Making every 4xx permanent

It is tempting to dead-letter all 4xx: "the request is wrong, retrying will not help". Some dependencies
answer `400` or `409` while they are still processing an earlier request. Turning those into permanent
failures filled a dead-letter queue with messages that would have succeeded a minute later. Classify per
dependency, from what it actually returns. See
[when a partner is not idempotent and fails transiently](transient-retries.md#when-a-partner-is-not-idempotent-and-fails-transiently).

### 429 in the transient budget

One retry budget for 5xx and 429 means three quick 429s use up all retries in a few seconds, ignoring
`Retry-After`. Keep them separate: the [throttle retry](throttling-and-retry-after.md) waits as long as
the dependency asks, up to a cap.

### An uncapped `Retry-After`

A dependency that answers `Retry-After: 3600` holds a worker for an hour. `MaxRetryAfter` caps the wait;
beyond it, the pipeline gives up and marks the request as
[throttle-exhausted](throttling-and-retry-after.md#when-throttling-gives-up) so the caller can defer it.

### Treating a final 429 as an ordinary failure

`EnsureSuccessStatusCode()` turns the final 429 into an `HttpRequestException`, and a generic handler
dead-letters it. Check `request.IsThrottleExhausted()` or catch `OutboundThrottleRejectedException`
first, and defer the work.

### Request content that cannot be sent twice

A retry sends the same `HttpRequestMessage` again. `StringContent`, `ByteArrayContent` and JSON content
can be sent twice; a `StreamContent` over a network stream cannot. Buffer the body, or do not retry
that request.

### A predicate that never fires

Generated clients wrap failures in their own exception types *after* the pipeline. A predicate on the
wrapper type never matches. See [exception predicates](migration.md#from-hand-written-polly-policies).

## Handlers

### Two resilience handlers

A standard handler added in `ConfigureHttpClientDefaults` applies to every client and stacks with this
one: retries multiply, and the inner 30-second total timeout cuts the outer `Retry-After` waits. One
pipeline per client. See [migration](migration.md#from-addstandardresiliencehandler).

### The token handler outside the pipeline

A token handler that runs before the pipeline adds the token once; every retry sends the same,
possibly expired, token, and a 401 retry cannot help. Put the pipeline outermost. See
[where the handler sits](architecture.md#where-the-handler-sits).

## Rate limits

### A rule that does not match

Rules match on the host and the path prefix the client actually calls. A rule for `https://api.example`
does not cover `https://api.example:8443`, and a rule for `/v2` does not cover `/v2beta`. Check the
`scope` tag on the metrics: `api.example/` means no rule matched. See
[matching](rate-limit-rules.md#matching).

### A per-instance limit with many instances

In-process limiters count per instance. Ten instances with a limit of 50 send 500 per second. Divide the
limit, or use [Redis](distributed-throttling-with-redis.md).

### Changing rules without a restart

Per-client options reload; rules do not. A new `PermitLimit` in configuration does nothing until the
application restarts. See [reloading](configuration.md#reloading).

### A concurrency lease shorter than the request

With Redis, a concurrency permit expires after `ConcurrencyLeaseTimeout` in case the instance dies. A
request that runs longer loses its permit while still running, and one more request is let in. Keep the
lease above the [total timeout](timeouts.md#total-timeout).

### Expecting limits while Redis is down

The pipeline [fails open](architecture.md#fail-open): without Redis, requests go through unlimited. If
the dependency must never see more than its limit, choose `RejectRequests` — and accept that a Redis
outage then stops the traffic. See [when Redis is down](distributed-throttling-with-redis.md#when-redis-is-down).

## Tests

### Tests that wait for real time

A test that waits for a real backoff takes seconds and fails on a busy build agent. Use a
`FakeTimeProvider` and drive it, and turn retries off where they are not the subject. See
[testing](testing.md).

## More

The circuit breaker playbook lists pitfalls where retries and a breaker meet:
[timeouts and retries](https://github.com/iverburgh/azure-functions-circuit-breaker-playbook/blob/main/docs/pitfalls.md#timeouts-and-retries).
