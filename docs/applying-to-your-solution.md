# Applying it to your solution

A checklist to go through once per dependency. Write the answers down next to the client registration;
they are the reasons behind the numbers.

## 1. Know the dependency

- [ ] What does it normally take? Look at the p99 latency, not the average.
- [ ] Does it publish a rate limit? Per what: API key, IP address, path?
- [ ] Does it send `Retry-After` with a 429? As seconds or as a date?
- [ ] Which calls are safe to repeat? Does it accept an `Idempotency-Key`?
- [ ] Which 4xx does it return *transiently* (while busy, while processing an earlier request)?
- [ ] How many instances of your application call it, and from how many clients?

## 2. Choose the pipeline

| Situation | Use |
|---|---|
| Ordinary request/response calls | `AddHttpResilience()` |
| The client must keep its own timeout and retries (uploads, legacy retry loops) | `AddOutboundThrottling()`: rate limits and 429 handling only |
| A call that must never be repeated, whatever happens | `AddHttpResilience` with `TransientRetry.MaxRetryAttempts = 0` |

## 3. Set the numbers

- [ ] **Attempt timeout**: p99 plus margin. Shorter for a dependency that is either fast or broken.
- [ ] **Transient retries**: the defaults (3, from 2 s, at most 15 s) suit most. Fewer for interactive
      callers; `0` where the caller retries anyway.
- [ ] **Throttle retries**: is `MaxRetryAfter` (120 s) longer than your caller can wait? Lower it, and
      let the caller defer instead.
- [ ] **Total timeout**: leave it computed, or set it to the caller's real budget — below the message
      lock duration for a message handler.
- [ ] Which non-GET calls are safe? `MarkRetrySafe()` them, or send an `Idempotency-Key`.
- [ ] Extra outcomes to retry? Add them in `AdditionalPredicate`, never 429.

See [timeouts](timeouts.md), [transient retries](transient-retries.md) and
[throttling](throttling-and-retry-after.md).

## 4. Add a rate limit (when it publishes one)

- [ ] A rule per dependency, with the same `BaseAddress` the clients use.
- [ ] `PermitLimit` below the published limit, divided by the instance count unless you use Redis.
- [ ] A concurrency rule for a dependency that is slow rather than limited.
- [ ] More than one instance and a hard limit? Add [Redis](distributed-throttling-with-redis.md) and
      choose the failure mode.

See [rate-limit rules](rate-limit-rules.md).

## 5. Handle the end of the line

- [ ] What does the caller do when the pipeline gives up? Check `request.IsThrottleExhausted()` or catch
      `OutboundThrottleRejectedException` and defer; never dead-letter throttling.
- [ ] Which errors are permanent, and dead-letter at once? Which go back to the broker?
- [ ] Is there a circuit breaker for when the dependency is down altogether?

See [layered retries](layered-retries.md).

## 6. Make it visible

- [ ] Export the `HttpResilience` meter.
- [ ] Alert on `resilience.http.rejected_requests` and on `resilience.http.store.failures`.
- [ ] Dashboard `resilience.http.throttle.requests` by `throttle.source`: `server` means your limit is too
      high.

See [observability](observability.md).

## 7. Test it

- [ ] Tests of your own code run with retries off or on a fake clock.
- [ ] One test per dependency proves the behaviour you rely on: the 429 is deferred, the `POST` is not
      repeated.

See [testing](testing.md).

## Before production

Read [pitfalls](pitfalls.md).
