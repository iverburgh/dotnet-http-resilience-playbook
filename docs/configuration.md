# Configuration

Every setting can come from configuration, from code, or both. Configuration first, code last.

## Order

For a client named `partner`, the pipeline gets its options like this:

1. `new HttpResilienceOptions()` — the defaults;
2. every `Configure<HttpResilienceOptions>("partner", …)` and `Bind`, in registration order;
3. the callback passed to `AddHttpResilience`;
4. validation — invalid options throw `OptionsValidationException` when the client is first created.

```csharp
builder.Services.Configure<HttpResilienceOptions>("partner", builder.Configuration.GetSection("Partner:Resilience"));

builder.Services.AddHttpClient("partner")
    .AddHttpResilience((options, context) =>
    {
        // Runs after the bound section: code wins for what code sets.
        options.TransientRetry.AdditionalPredicate = o => o.Result?.StatusCode == HttpStatusCode.Conflict;
    });
```

Use configuration for numbers an operator may want to change — timeouts, retry counts — and code for
behaviour: predicates, `OnRetry`, anything that needs a service.

Options are resolved per client name with `IOptionsFactory`, so named options, `PostConfigure` and
your own `IValidateOptions<HttpResilienceOptions>` all apply.

## Reloading

The pipeline is rebuilt when the named options change (`EnableReloads`), so a new
`AttemptTimeout` in a reloadable source — a JSON file with `reloadOnChange`, or a configuration store
with refresh — applies to new requests without a restart. Requests in flight finish with the old pipeline.

Rate-limit **rules** are read once at startup: limiters hold state, and replacing them on the fly would
reset every budget. Changing rules needs a restart.

## Reference

### `HttpResilienceOptions` (per client)

| Key | Default | Notes |
|---|---|---|
| `AttemptTimeout` | `00:00:30` | positive |
| `TotalTimeout` | computed | ≥ `AttemptTimeout`; see [timeouts](timeouts.md#total-timeout) |
| `TransientRetry:MaxRetryAttempts` | `3` | 0 disables |
| `TransientRetry:BaseDelay` | `00:00:02` | not negative |
| `TransientRetry:MaxDelay` | `00:00:15` | ≥ `BaseDelay` |
| `TransientRetry:BackoffType` | `Exponential` | `Constant`, `Linear`, `Exponential` |
| `TransientRetry:UseJitter` | `true` | |
| `TransientRetry:RetryNonIdempotentMethods` | `false` | see [idempotency](transient-retries.md#idempotency) |
| `TransientRetry:AdditionalPredicate` | — | code only |
| `TransientRetry:OnRetry` | — | code only |
| `ThrottleRetry:MaxRetryAttempts` | `3` | 0 returns the first 429 |
| `ThrottleRetry:DefaultRetryAfter` | `00:01:00` | ≤ `MaxRetryAfter` |
| `ThrottleRetry:MaxRetryAfter` | `00:02:00` | positive |

For `AddOutboundThrottling`, the same `ThrottleRetry` settings are a named `ThrottleRetryOptions` at the
top level: `Configure<ThrottleRetryOptions>("legacy", section)`.

### `OutboundThrottlingOptions` (application-wide, section `OutboundThrottling`)

| Key | Default | Notes |
|---|---|---|
| `Rules[i]:Name` | — | required; used in logs and metrics |
| `Rules[i]:BaseAddress` | — | absolute; host and optional path prefix |
| `Rules[i]:Algorithm`, `CatchAll:Algorithm` | `SlidingWindow` | `SlidingWindow`, `FixedWindow`, `TokenBucket`, `Concurrency` |
| `…:PermitLimit` | — | positive |
| `…:Window` | `00:00:01` | window algorithms |
| `…:SegmentsPerWindow` | `4` | in-process sliding window only |
| `…:TokensPerPeriod` | `1` | token bucket |
| `…:ReplenishmentPeriod` | `00:00:01` | token bucket |
| `…:QueueLimit` | `0` | in-process only |

See [rate-limit rules](rate-limit-rules.md).

### `RedisThrottlingOptions` (section `HttpResilience:Redis`)

| Key | Default | Notes |
|---|---|---|
| `ConnectionString` | — | or `Endpoint`, not both |
| `Endpoint` | — | Entra ID sign-in |
| `Credential` | `DefaultAzureCredential` | code only |
| `KeyPrefix` | `http-resilience` | |
| `LimiterFailureMode` | `AllowRequests` | or `RejectRequests` |
| `ConcurrencyLeaseTimeout` | `00:05:00` | above your longest request |

See [distributed throttling with Redis](distributed-throttling-with-redis.md).

## Per environment

The same keys work in any configuration source. A typical split:

| Setting | Development | Production |
|---|---|---|
| Timeouts and retries | defaults, or shorter to see failures quickly | tuned per dependency |
| Rules | none, or the production rules with a lower `PermitLimit` against a sandbox | the dependency's limit minus margin |
| Redis | none: in-process | when you run more than one instance |

Keep the numbers next to the client they belong to, in source control, and change them through the same
review as code. The [App Configuration playbook](https://github.com/iverburgh/azure-app-configuration-playbook)
shows one way to deploy them per environment.
