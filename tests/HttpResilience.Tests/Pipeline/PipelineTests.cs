using System.Net;
using System.Net.Http.Headers;
using HttpResilience.Throttling;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Options;
using Polly.Timeout;

namespace HttpResilience.Tests.Pipeline;

public class TransientRetryTests
{
    [Test]
    public async Task Get_that_fails_with_503_is_retried()
    {
        var partner = new FakePartner(HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);
        using var host = TestHost.Create(partner);

        using var response = await host.GetAsync("/orders/1");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(partner.Calls, Is.EqualTo(2));
    }

    [Test]
    public async Task Network_error_is_retried()
    {
        var partner = new FakePartner((_, call, _) => call == 0
            ? Task.FromException<HttpResponseMessage>(new HttpRequestException("connection reset"))
            : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        using var host = TestHost.Create(partner);

        using var response = await host.GetAsync("/orders/1");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(partner.Calls, Is.EqualTo(2));
    }

    [Test]
    public async Task Retries_stop_after_MaxRetryAttempts()
    {
        var partner = new FakePartner(HttpStatusCode.BadGateway);
        using var host = TestHost.Create(partner, (o, _) => o.TransientRetry.MaxRetryAttempts = 2);

        using var response = await host.GetAsync("/orders/1");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadGateway));
        Assert.That(partner.Calls, Is.EqualTo(3));
    }

    [Test]
    public async Task Post_is_not_retried_by_default()
    {
        var partner = new FakePartner(HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);
        using var host = TestHost.Create(partner);

        using var response = await host.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/orders") { Content = new StringContent("{}") });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        Assert.That(partner.Calls, Is.EqualTo(1));
    }

    [Test]
    public async Task Post_with_an_idempotency_key_is_retried()
    {
        var partner = new FakePartner(HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);
        using var host = TestHost.Create(partner);
        var request = new HttpRequestMessage(HttpMethod.Post, "/orders") { Content = new StringContent("{}") };
        request.Headers.Add("Idempotency-Key", "order-42");

        using var response = await host.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(partner.Calls, Is.EqualTo(2));
    }

    [Test]
    public async Task Post_marked_retry_safe_is_retried()
    {
        var partner = new FakePartner(HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);
        using var host = TestHost.Create(partner);

        using var response = await host.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/search").MarkRetrySafe());

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(partner.Calls, Is.EqualTo(2));
    }

    [Test]
    public async Task Post_is_retried_when_the_client_opts_in()
    {
        var partner = new FakePartner(HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);
        using var host = TestHost.Create(partner, (o, _) => o.TransientRetry.RetryNonIdempotentMethods = true);

        using var response = await host.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/orders"));

        Assert.That(partner.Calls, Is.EqualTo(2));
    }

    [Test]
    public async Task Bad_request_is_not_retried()
    {
        var partner = new FakePartner(HttpStatusCode.BadRequest, HttpStatusCode.OK);
        using var host = TestHost.Create(partner);

        using var response = await host.GetAsync("/orders/1");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(partner.Calls, Is.EqualTo(1));
    }

    [Test]
    public async Task Additional_predicate_adds_a_status_to_retry()
    {
        var partner = new FakePartner(HttpStatusCode.Conflict, HttpStatusCode.OK);
        using var host = TestHost.Create(partner, (o, _) =>
            o.TransientRetry.AdditionalPredicate = outcome => outcome.Result?.StatusCode == HttpStatusCode.Conflict);

        using var response = await host.GetAsync("/orders/1");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(partner.Calls, Is.EqualTo(2));
    }

    [Test]
    public async Task Additional_predicate_does_not_bypass_the_idempotency_guard()
    {
        var partner = new FakePartner(HttpStatusCode.Conflict, HttpStatusCode.OK);
        using var host = TestHost.Create(partner, (o, _) =>
            o.TransientRetry.AdditionalPredicate = outcome => outcome.Result?.StatusCode == HttpStatusCode.Conflict);

        using var response = await host.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/orders"));

        Assert.That(partner.Calls, Is.EqualTo(1));
    }

    [Test]
    public async Task OnRetry_runs_before_each_retry()
    {
        var partner = new FakePartner(HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.OK);
        var resets = 0;
        using var host = TestHost.Create(partner, (o, _) =>
        {
            o.TransientRetry.AdditionalPredicate = outcome => outcome.Result?.StatusCode == HttpStatusCode.Unauthorized;
            o.TransientRetry.OnRetry = _ =>
            {
                resets++;
                return ValueTask.CompletedTask;
            };
        });

        using var response = await host.GetAsync("/orders/1");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(resets, Is.EqualTo(2));
    }

    [Test]
    public async Task Retry_reruns_handlers_added_after_the_pipeline()
    {
        var partner = new FakePartner(HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);
        var inner = new CountingHandler();
        using var host = TestHost.Create(partner, client: c => c.AddHttpMessageHandler(() => inner));

        using var response = await host.GetAsync("/orders/1");

        Assert.That(inner.Calls, Is.EqualTo(2));
    }

    [Test]
    public async Task Zero_retries_sends_once()
    {
        var partner = new FakePartner(HttpStatusCode.ServiceUnavailable);
        using var host = TestHost.Create(partner, (o, _) =>
        {
            o.TransientRetry.MaxRetryAttempts = 0;
            o.ThrottleRetry.MaxRetryAttempts = 0;
        });

        using var response = await host.GetAsync("/orders/1");

        Assert.That(partner.Calls, Is.EqualTo(1));
    }
}

public class TimeoutTests
{
    [Test]
    public void HttpClient_timeout_is_infinite()
    {
        using var host = TestHost.Create(new FakePartner(HttpStatusCode.OK));

        Assert.That(host.CreateClient().Timeout, Is.EqualTo(Timeout.InfiniteTimeSpan));
    }

    [Test]
    public async Task Slow_attempt_times_out_and_is_retried()
    {
        TestHost? host = null;
        var partner = new FakePartner(async (_, call, ct) =>
        {
            if (call == 0)
            {
                await Task.Delay(TimeSpan.FromMinutes(5), host!.Clock, ct);
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var created = host = TestHost.Create(partner, (o, _) => o.AttemptTimeout = TimeSpan.FromSeconds(5));
        var started = host.Clock.GetUtcNow();

        using var response = await host.GetAsync("/orders/1");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(partner.Calls, Is.EqualTo(2));
        Assert.That(host.Clock.GetUtcNow() - started, Is.LessThan(TimeSpan.FromSeconds(30)));
    }

    [Test]
    public void Total_timeout_ends_the_retries()
    {
        var partner = new FakePartner(HttpStatusCode.ServiceUnavailable);
        using var host = TestHost.Create(partner, (o, _) =>
        {
            o.TransientRetry.MaxRetryAttempts = 50;
            o.TransientRetry.BaseDelay = TimeSpan.FromSeconds(2);
            o.TransientRetry.BackoffType = Polly.DelayBackoffType.Constant;
            o.AttemptTimeout = TimeSpan.FromSeconds(5);
            o.TotalTimeout = TimeSpan.FromSeconds(9);
        });

        Assert.ThrowsAsync<TimeoutRejectedException>(() => host.GetAsync("/orders/1"));
        Assert.That(partner.Calls, Is.InRange(3, 6));
    }
}

public class ThrottleRetryTests
{
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
        Assert.That(host.Clock.GetUtcNow() - started, Is.InRange(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(24)));
    }

    [Test]
    public async Task Retry_After_is_capped_at_MaxRetryAfter()
    {
        var partner = new FakePartner((_, call, _) => Task.FromResult(call == 0
            ? FakePartner.TooManyRequests(TimeSpan.FromHours(1))
            : new HttpResponseMessage(HttpStatusCode.OK)));
        using var host = TestHost.Create(partner, (o, _) =>
        {
            o.ThrottleRetry.MaxRetryAfter = TimeSpan.FromSeconds(10);
            o.ThrottleRetry.DefaultRetryAfter = TimeSpan.FromSeconds(5);
        });
        var started = host.Clock.GetUtcNow();

        using var response = await host.GetAsync("/catalog");

        Assert.That(host.Clock.GetUtcNow() - started, Is.InRange(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(13)));
    }

    [Test]
    public async Task Too_many_requests_is_retried_even_for_post()
    {
        var partner = new FakePartner((_, call, _) => Task.FromResult(call == 0
            ? FakePartner.TooManyRequests(TimeSpan.FromSeconds(1))
            : new HttpResponseMessage(HttpStatusCode.Created)));
        using var host = TestHost.Create(partner);

        using var response = await host.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/orders"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        Assert.That(partner.Calls, Is.EqualTo(2));
    }

    [Test]
    public async Task Exhausted_throttling_returns_the_429_and_marks_the_request()
    {
        var partner = new FakePartner((_, _, _) => Task.FromResult(FakePartner.TooManyRequests(TimeSpan.FromSeconds(1))));
        using var host = TestHost.Create(partner, (o, _) => o.ThrottleRetry.MaxRetryAttempts = 2);
        using var rejected = new MetricCollector<long>(host.Get<System.Diagnostics.Metrics.IMeterFactory>(), HttpResilienceTelemetry.MeterName, HttpResilienceMetrics.RejectedRequests);

        using var response = await host.GetAsync("/catalog");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
        Assert.That(partner.Calls, Is.EqualTo(3));
        Assert.That(response.RequestMessage!.IsThrottleExhausted(), Is.True);
        Assert.That(rejected.GetMeasurementSnapshot().Single().Tags["rejection.reason"], Is.EqualTo("server_429"));
    }

    [Test]
    public async Task Successful_request_is_not_marked_exhausted()
    {
        using var host = TestHost.Create(new FakePartner(HttpStatusCode.OK));

        using var response = await host.GetAsync("/catalog");

        Assert.That(response.RequestMessage!.IsThrottleExhausted(), Is.False);
    }

    [Test]
    public async Task Zero_throttle_retries_returns_the_first_429()
    {
        var partner = new FakePartner(HttpStatusCode.TooManyRequests);
        using var host = TestHost.Create(partner, (o, _) => o.ThrottleRetry.MaxRetryAttempts = 0);

        using var response = await host.GetAsync("/catalog");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
        Assert.That(partner.Calls, Is.EqualTo(1));
        Assert.That(response.RequestMessage!.IsThrottleExhausted(), Is.True);
    }

    [Test]
    public async Task Transient_retry_does_not_retry_a_429()
    {
        var partner = new FakePartner(HttpStatusCode.TooManyRequests);
        using var host = TestHost.Create(partner, (o, _) =>
        {
            o.ThrottleRetry.MaxRetryAttempts = 0;
            o.TransientRetry.MaxRetryAttempts = 5;
        });

        using var response = await host.GetAsync("/catalog");

        Assert.That(partner.Calls, Is.EqualTo(1));
    }

    [Test]
    public async Task Attempts_are_counted_per_attempt_and_requests_once()
    {
        var partner = new FakePartner((_, call, _) => Task.FromResult(call < 3
            ? FakePartner.TooManyRequests(TimeSpan.FromSeconds(1))
            : new HttpResponseMessage(HttpStatusCode.OK)));
        using var host = TestHost.Create(partner);
        var meters = host.Get<System.Diagnostics.Metrics.IMeterFactory>();
        using var attempts = new MetricCollector<long>(meters, HttpResilienceTelemetry.MeterName, HttpResilienceMetrics.ThrottledAttempts);
        using var requests = new MetricCollector<long>(meters, HttpResilienceTelemetry.MeterName, HttpResilienceMetrics.ThrottledRequests);
        using var delays = new MetricCollector<double>(meters, HttpResilienceTelemetry.MeterName, HttpResilienceMetrics.RetryDelay);

        using var response = await host.GetAsync("/catalog");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(attempts.GetMeasurementSnapshot().Sum(m => m.Value), Is.EqualTo(3));
        Assert.That(requests.GetMeasurementSnapshot().Sum(m => m.Value), Is.EqualTo(1));
        Assert.That(delays.GetMeasurementSnapshot().Select(m => m.Tags["retry.kind"]), Is.All.EqualTo("throttle"));
        Assert.That(attempts.GetMeasurementSnapshot()[0].Tags["server.address"], Is.EqualTo("api.partner.example"));
    }

    [Test]
    public async Task Throttling_only_pipeline_retries_a_post_429_but_not_a_503()
    {
        var partner = new FakePartner((request, call, _) => Task.FromResult(request.RequestUri!.AbsolutePath == "/orders" && call == 0
            ? FakePartner.TooManyRequests(TimeSpan.FromSeconds(1))
            : new HttpResponseMessage(request.RequestUri.AbsolutePath == "/fail" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)));
        using var host = TestHost.Create(partner, throttlingOnly: true);

        using var ok = await host.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/orders"));
        using var failed = await host.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/fail"));

        Assert.That(ok.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(failed.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        Assert.That(partner.Calls, Is.EqualTo(3));
        Assert.That(host.CreateClient().Timeout, Is.EqualTo(TimeSpan.FromSeconds(100)));
    }
}

public class SharedThrottleGateTests
{
    [Test]
    public async Task A_429_blocks_other_requests_to_the_same_host_without_calling_it()
    {
        var partner = new FakePartner((_, _, _) => Task.FromResult(FakePartner.TooManyRequests(TimeSpan.FromSeconds(30))));
        using var host = TestHost.Create(partner, (o, _) => o.ThrottleRetry.MaxRetryAttempts = 0);

        using var first = await host.GetAsync("/catalog");
        var second = new HttpRequestMessage(HttpMethod.Get, "/orders/1");
        var ex = Assert.ThrowsAsync<OutboundThrottleRejectedException>(() => host.SendAsync(second));

        Assert.That(partner.Calls, Is.EqualTo(1));
        Assert.That(ex!.Reason, Is.EqualTo(ThrottleRejectionReason.ServerThrottle));
        Assert.That(ex.RetryAfter, Is.EqualTo(TimeSpan.FromSeconds(30)));
        Assert.That(second.IsThrottleExhausted(), Is.True);
    }

    [Test]
    public async Task The_block_ends_after_Retry_After()
    {
        var partner = new FakePartner((_, call, _) => Task.FromResult(call == 0
            ? FakePartner.TooManyRequests(TimeSpan.FromSeconds(30))
            : new HttpResponseMessage(HttpStatusCode.OK)));
        using var host = TestHost.Create(partner, (o, _) => o.ThrottleRetry.MaxRetryAttempts = 0);
        using var first = await host.GetAsync("/catalog");

        host.Clock.Advance(TimeSpan.FromSeconds(31));
        using var second = await host.GetAsync("/catalog");

        Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(partner.Calls, Is.EqualTo(2));
    }

    [Test]
    public async Task A_blocked_request_is_retried_after_the_remaining_time()
    {
        var partner = new FakePartner(HttpStatusCode.OK);
        using var host = TestHost.Create(partner);
        await host.Get<IThrottleStateStore>().BlockAsync("api.partner.example/", host.Clock.GetUtcNow().AddSeconds(15), CancellationToken.None);
        var started = host.Clock.GetUtcNow();

        using var response = await host.GetAsync("/orders/1");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(partner.Calls, Is.EqualTo(1));
        Assert.That(host.Clock.GetUtcNow() - started, Is.InRange(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(18)));
    }

    [Test]
    public async Task Separate_rules_have_separate_blocks()
    {
        var partner = new FakePartner((request, _, _) => Task.FromResult(request.RequestUri!.AbsolutePath.StartsWith("/orders", StringComparison.Ordinal)
            ? FakePartner.TooManyRequests(TimeSpan.FromSeconds(30))
            : new HttpResponseMessage(HttpStatusCode.OK)));
        using var host = TestHost.Create(partner, (o, _) => o.ThrottleRetry.MaxRetryAttempts = 0, services: s => s.AddOutboundThrottlingRules(o =>
        {
            o.Rules.Add(new ThrottlingRule { Name = "orders", BaseAddress = new Uri("https://api.partner.example/orders"), Algorithm = RateLimitAlgorithm.Concurrency, PermitLimit = 100 });
            o.Rules.Add(new ThrottlingRule { Name = "catalog", BaseAddress = new Uri("https://api.partner.example/catalog"), Algorithm = RateLimitAlgorithm.Concurrency, PermitLimit = 100 });
        }));

        using var orders = await host.GetAsync("/orders/1");
        using var catalog = await host.GetAsync("/catalog");

        Assert.That(orders.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
        Assert.That(catalog.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task A_failing_store_lets_requests_through()
    {
        var partner = new FakePartner(HttpStatusCode.OK);
        using var host = TestHost.Create(partner, services: s => s.AddSingleton<IThrottleStateStore, BrokenStore>());
        using var failures = new MetricCollector<long>(host.Get<System.Diagnostics.Metrics.IMeterFactory>(), HttpResilienceTelemetry.MeterName, HttpResilienceMetrics.StoreFailures);

        using var response = await host.GetAsync("/orders/1");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(failures.GetMeasurementSnapshot().Select(m => m.Tags["store.operation"]), Does.Contain("get"));
    }

    private sealed class BrokenStore : IThrottleStateStore
    {
        public ValueTask<DateTimeOffset?> GetBlockedUntilAsync(string scopeKey, CancellationToken cancellationToken) => throw new InvalidOperationException("store down");

        public ValueTask BlockAsync(string scopeKey, DateTimeOffset until, CancellationToken cancellationToken) => throw new InvalidOperationException("store down");
    }
}

public class RateLimiterTests
{
    [Test]
    public async Task Rejected_permit_is_retried_after_the_limiter_hint()
    {
        var limiter = new ScriptedLimiter().Reject(TimeSpan.FromSeconds(5));
        var partner = new FakePartner(HttpStatusCode.OK);
        using var host = TestHost.Create(partner, services: TestHost.WithLimiter(new ScriptedLimiterFactory(limiter)));
        var started = host.Clock.GetUtcNow();

        using var response = await host.GetAsync("/orders/1");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(partner.Calls, Is.EqualTo(1));
        Assert.That(host.Clock.GetUtcNow() - started, Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(5)));
    }

    [Test]
    public async Task Exhausted_limiter_throws_without_calling_the_dependency()
    {
        var limiter = new ScriptedLimiter().Reject(TimeSpan.FromSeconds(1), times: 10);
        var partner = new FakePartner(HttpStatusCode.OK);
        using var host = TestHost.Create(partner, (o, _) => o.ThrottleRetry.MaxRetryAttempts = 2, services: TestHost.WithLimiter(new ScriptedLimiterFactory(limiter)));
        using var rejected = new MetricCollector<long>(host.Get<System.Diagnostics.Metrics.IMeterFactory>(), HttpResilienceTelemetry.MeterName, HttpResilienceMetrics.RejectedRequests);

        var ex = Assert.ThrowsAsync<OutboundThrottleRejectedException>(() => host.GetAsync("/orders/1"));

        Assert.That(ex!.Reason, Is.EqualTo(ThrottleRejectionReason.RateLimit));
        Assert.That(partner.Calls, Is.Zero);
        Assert.That(rejected.GetMeasurementSnapshot().Single().Tags["rejection.reason"], Is.EqualTo("rate_limit"));
    }

    [Test]
    public async Task Requests_without_a_matching_rule_are_not_limited()
    {
        var limiter = new ScriptedLimiter().Reject(null, times: 10);
        var factory = new ScriptedLimiterFactory(limiter);
        var partner = new FakePartner(HttpStatusCode.OK);
        using var host = TestHost.Create(partner, services: TestHost.WithLimiter(factory, o =>
            o.Rules.Add(new ThrottlingRule { Name = "other", BaseAddress = new Uri("https://other.example"), PermitLimit = 1 })));

        using var response = await host.GetAsync("/orders/1");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(factory.Scopes, Is.Empty);
    }

    [Test]
    public async Task Matching_rule_is_passed_to_the_limiter_factory()
    {
        var factory = new ScriptedLimiterFactory(new ScriptedLimiter());
        using var host = TestHost.Create(new FakePartner(HttpStatusCode.OK), services: TestHost.WithLimiter(factory, o =>
            o.Rules.Add(new ThrottlingRule { Name = "orders", BaseAddress = new Uri("https://api.partner.example/orders"), PermitLimit = 1 })));

        using var response = await host.GetAsync("/orders/1");

        Assert.That(factory.Scopes.Single().Key, Is.EqualTo("api.partner.example/orders"));
        Assert.That(factory.Scopes.Single().RuleName, Is.EqualTo("orders"));
    }

    [Test]
    public async Task Broken_limiter_lets_requests_through()
    {
        var factory = new ScriptedLimiterFactory(new ScriptedLimiter()) { Throw = true };
        var partner = new FakePartner(HttpStatusCode.OK);
        using var host = TestHost.Create(partner, services: TestHost.WithLimiter(factory));

        using var response = await host.GetAsync("/orders/1");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task Available_permits_gauge_reports_each_limiter()
    {
        var factory = new ScriptedLimiterFactory(new ScriptedLimiter());
        using var host = TestHost.Create(new FakePartner(HttpStatusCode.OK), services: TestHost.WithLimiter(factory));
        using var permits = new MetricCollector<long>(host.Get<System.Diagnostics.Metrics.IMeterFactory>(), HttpResilienceTelemetry.MeterName, HttpResilienceMetrics.AvailablePermits);

        using var response = await host.GetAsync("/orders/1");
        permits.RecordObservableInstruments();

        Assert.That(permits.LastMeasurement?.Value, Is.EqualTo(7));
        Assert.That(permits.LastMeasurement?.Tags["throttle.rule"], Is.EqualTo("*"));
    }
}

public class ConfigurationTests
{
    [Test]
    public async Task Options_are_bound_per_client_name()
    {
        var partner = new FakePartner(HttpStatusCode.ServiceUnavailable);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Partner:Resilience:TransientRetry:MaxRetryAttempts"] = "1",
            ["Partner:Resilience:AttemptTimeout"] = "00:00:07",
        }).Build();
        HttpResilienceOptions? seen = null;
        using var host = TestHost.Create(partner, (o, _) => seen = o, services: s =>
            s.Configure<HttpResilienceOptions>(TestHost.ClientName, configuration.GetSection("Partner:Resilience")));

        using var response = await host.GetAsync("/orders/1");

        Assert.That(partner.Calls, Is.EqualTo(2));
        Assert.That(seen!.AttemptTimeout, Is.EqualTo(TimeSpan.FromSeconds(7)));
    }

    [Test]
    public void Invalid_options_fail_on_first_use()
    {
        using var host = TestHost.Create(new FakePartner(HttpStatusCode.OK), (o, _) => o.AttemptTimeout = TimeSpan.Zero);

        Assert.ThrowsAsync<OptionsValidationException>(() => host.GetAsync("/orders/1"));
    }

    [Test]
    public void Invalid_rules_fail_when_read()
    {
        using var host = TestHost.Create(new FakePartner(HttpStatusCode.OK), services: s =>
            s.AddOutboundThrottlingRules(o => o.Rules.Add(new ThrottlingRule { Name = "broken", PermitLimit = 0 })));

        var ex = Assert.Throws<OptionsValidationException>(() => _ = host.Get<IOptions<OutboundThrottlingOptions>>().Value);
        Assert.That(ex!.Failures, Has.Some.Contains("PermitLimit"));
    }

    [Test]
    public void Rules_bind_from_configuration()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OutboundThrottling:Rules:0:Name"] = "orders",
            ["OutboundThrottling:Rules:0:BaseAddress"] = "https://api.partner.example/orders",
            ["OutboundThrottling:Rules:0:Algorithm"] = "TokenBucket",
            ["OutboundThrottling:Rules:0:PermitLimit"] = "10",
            ["OutboundThrottling:CatchAll:PermitLimit"] = "50",
        }).Build();
        using var host = TestHost.Create(new FakePartner(HttpStatusCode.OK), services: s =>
            s.AddOutboundThrottlingRules(configuration.GetSection(OutboundThrottlingOptions.SectionName)));

        var options = host.Get<IOptions<OutboundThrottlingOptions>>().Value;

        Assert.That(options.Rules.Single().Algorithm, Is.EqualTo(RateLimitAlgorithm.TokenBucket));
        Assert.That(options.CatchAll!.PermitLimit, Is.EqualTo(50));
    }
}
