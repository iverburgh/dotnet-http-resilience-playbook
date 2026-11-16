# Transient retries

A transient failure is one that will probably not happen on the next try: a dropped connection, a
timeout, a server restarting. Retrying it turns a failed call into a slow success. Retrying the wrong
thing turns one order into two.

## What is retried

| Outcome | Retried | Why |
|---|---|---|
| `HttpRequestException` (connection refused, reset, DNS) | yes | the network, not the request |
| `TimeoutRejectedException` from the attempt timeout | yes | the attempt was slow, the next may not be |
| 5xx | yes | the server failed; it may succeed next time |
| 408 Request Timeout | yes | the server gave up waiting for the request |
| 429 Too Many Requests | **no** | belongs to the [throttle retry](throttling-and-retry-after.md), which waits for `Retry-After` |
| `OutboundThrottleRejectedException` | **no** | a local refusal; the throttle retry already handled it |
| 4xx other than 408 and 429 | no | the request is wrong; sending it again changes nothing |
| the caller's cancellation | no | the caller left |

The backoff is exponential with jitter by default: 2 s, 4 s, 8 s, each capped at `MaxDelay` (15 s),
each spread randomly so that callers that failed at the same moment do not retry at the same moment.
`BackoffType` can be `Constant` or `Linear` for a cheap, fast dependency.

Code: [TransientRetry.cs](../src/HttpResilience/Strategies/TransientRetry.cs).

## Idempotency

A request that timed out may have been processed. The response was lost, not the request. Sending a
`GET` twice is harmless; sending `POST /payments` twice may charge twice. So the transient retry only
retries a request that is **safe to send twice**:

| The request | Safe |
|---|---|
| `GET`, `HEAD`, `OPTIONS`, `TRACE`, `PUT`, `DELETE` | yes: idempotent by definition (RFC 9110) |
| any method with an `Idempotency-Key` header | yes: the server deduplicates |
| marked with `request.MarkRetrySafe()` | yes: you know it is, for example a `POST` that only searches |
| client configured with `RetryNonIdempotentMethods = true` | yes, for every request of that client |
| `POST`, `PATCH` otherwise | **no** |

```csharp
// The server deduplicates on the key: a retry cannot create a second order.
var request = new HttpRequestMessage(HttpMethod.Post, "orders") { Content = JsonContent.Create(order) };
request.Headers.Add("Idempotency-Key", order.Id.ToString());

// A search that happens to be a POST.
var search = new HttpRequestMessage(HttpMethod.Post, "orders/search") { Content = JsonContent.Create(query) }
    .MarkRetrySafe();
```

A request that is not retried for this reason is logged at Debug (event 7), so you can find the calls
that would have been retried. The guard also applies to outcomes added with `AdditionalPredicate`.

### Why on by default

Most retry libraries retry every method. That is the right default for reads and the wrong default for
writes, and the cost is not symmetric: a missing retry is a visible error you can handle; a duplicate
payment is an invisible one you discover in a reconciliation. Make the safe call explicit.

### When a partner is not idempotent and fails transiently

Some dependencies return transient errors on writes — a 409 because of a lock, a 400 that succeeds a
second later — and offer no idempotency key. There is no safe automatic answer:

1. **Ask for idempotency.** An `Idempotency-Key` header, or a client-generated id the server rejects
   as duplicate, makes every other problem go away.
2. **Check before retrying.** Query whether the write happened, then decide. Slow, but correct.
3. **Accept duplicates** where the business can: opt in with `RetryNonIdempotentMethods` for that client
   only, and write down why.

Making every 4xx non-retryable on such a dependency moves the problem: the transient 409s that used to
be retried now end up as failed messages. Classify per dependency; see
[layered retries](layered-retries.md).

## Adding outcomes

`AdditionalPredicate` adds outcomes on top of the built-in set:

```csharp
.AddHttpResilience((o, _) =>
{
    // This partner answers 404 for a few seconds after a create, until its replicas catch up.
    o.TransientRetry.AdditionalPredicate = outcome => outcome.Result?.StatusCode == HttpStatusCode.NotFound;
});
```

It never sees a 429 or a throttle rejection, so it cannot accidentally pull throttling into the
transient retry. For network errors wrapped in another exception type by an inner handler, walk the
chain:

```csharp
o.TransientRetry.AdditionalPredicate = outcome =>
{
    for (var e = outcome.Exception; e is not null; e = e.InnerException)
    {
        if (e is SocketException or IOException) return true;
    }
    return false;
};
```

Keep these predicates few and documented: each one is a statement about a specific dependency's
behaviour.

## Retrying with a fresh token

A handler added *after* `AddHttpResilience` sits inside the pipeline, and runs again on each retry:

```csharp
builder.Services.AddHttpClient("vendor")
    .AddHttpResilience((o, context) =>
    {
        var tokens = context.ServiceProvider.GetRequiredService<VendorTokenCache>();
        o.TransientRetry.AdditionalPredicate = outcome => outcome.Result?.StatusCode == HttpStatusCode.Unauthorized;
        o.TransientRetry.OnRetry = args =>
        {
            if (args.Outcome.Result?.StatusCode == HttpStatusCode.Unauthorized)
            {
                tokens.Invalidate();
            }
            return ValueTask.CompletedTask;
        };
    })
    .AddHttpMessageHandler<VendorTokenHandler>();   // inside: a retry fetches a new token
```

A 401 after a token expired early becomes one retry with a fresh token instead of an error. Order
matters: add the token handler *before* `AddHttpResilience` and the retry reuses the old header.
[`Retry_reruns_handlers_added_after_the_pipeline`](../tests/HttpResilience.Tests/Pipeline/PipelineTests.cs)
shows it.

## Turning retries off

`MaxRetryAttempts = 0` leaves the strategy out. Use it in tests (together with `BaseDelay = 0` where
a strategy remains), and for a client whose caller retries anyway — see
[layered retries](layered-retries.md).

Tested in [TransientRetryTests](../tests/HttpResilience.Tests/Pipeline/PipelineTests.cs) and
[RetrySafetyTests](../tests/HttpResilience.Tests/BuildingBlockTests.cs).
