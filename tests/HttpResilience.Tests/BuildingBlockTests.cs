using System.Net.Http.Headers;
using System.Threading.RateLimiting;
using HttpResilience.Strategies;
using HttpResilience.Throttling;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace HttpResilience.Tests;

public class RetryAfterTests
{
    private static readonly DateTimeOffset Now = new(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Default = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Max = TimeSpan.FromSeconds(120);

    [Test]
    public void Delta_is_used_as_is()
        => Assert.That(RetryAfter.Parse(new RetryConditionHeaderValue(TimeSpan.FromSeconds(7)), Now, Default, Max), Is.EqualTo(TimeSpan.FromSeconds(7)));

    [Test]
    public void Date_is_relative_to_now()
        => Assert.That(RetryAfter.Parse(new RetryConditionHeaderValue(Now.AddSeconds(30)), Now, Default, Max), Is.EqualTo(TimeSpan.FromSeconds(30)));

    [Test]
    public void Date_in_the_past_means_now()
        => Assert.That(RetryAfter.Parse(new RetryConditionHeaderValue(Now.AddSeconds(-30)), Now, Default, Max), Is.EqualTo(TimeSpan.Zero));

    [Test]
    public void Missing_header_gives_the_default()
        => Assert.That(RetryAfter.Parse(null, Now, Default, Max), Is.EqualTo(Default));

    [Test]
    public void Long_wait_is_capped()
        => Assert.That(RetryAfter.Parse(new RetryConditionHeaderValue(TimeSpan.FromHours(2)), Now, Default, Max), Is.EqualTo(Max));

    [Test]
    public void Jitter_only_adds_up_to_ten_percent()
    {
        var wait = TimeSpan.FromSeconds(20);

        Assert.That(RetryAfter.AddJitter(wait, 0), Is.EqualTo(wait));
        Assert.That(RetryAfter.AddJitter(wait, 0.999), Is.InRange(wait, wait + TimeSpan.FromSeconds(2)));
    }

    [Test]
    public void Short_waits_get_at_least_100_ms_of_jitter_range()
        => Assert.That(RetryAfter.AddJitter(TimeSpan.Zero, 0.5), Is.EqualTo(TimeSpan.FromMilliseconds(50)));

    [Test]
    public void Rejection_uses_its_own_hint_and_falls_back_to_the_default()
    {
        var options = new ThrottleRetryOptions { DefaultRetryAfter = TimeSpan.FromSeconds(9) };
        var withHint = Polly.Outcome.FromException<HttpResponseMessage>(new OutboundThrottleRejectedException(ThrottleRejectionReason.RateLimit, "x/", TimeSpan.FromSeconds(3)));
        var withoutHint = Polly.Outcome.FromException<HttpResponseMessage>(new OutboundThrottleRejectedException(ThrottleRejectionReason.RateLimit, "x/", null));

        Assert.That(ThrottleRetry.ComputeDelay(withHint, options, Now, 0), Is.EqualTo(TimeSpan.FromSeconds(3)));
        Assert.That(ThrottleRetry.ComputeDelay(withoutHint, options, Now, 0), Is.EqualTo(TimeSpan.FromSeconds(9)));
    }
}

public class ThrottleRuleMatcherTests
{
    private static ThrottleRuleMatcher Matcher(CatchAllThrottlingRule? catchAll = null, params string[] baseAddresses)
        => new(Options.Create(new OutboundThrottlingOptions
        {
            Rules = baseAddresses.Select((b, i) => new ThrottlingRule { Name = $"rule{i}", BaseAddress = new Uri(b), PermitLimit = 1 }).ToList(),
            CatchAll = catchAll,
        }));

    [Test]
    public void Longest_prefix_wins()
    {
        var scope = Matcher(null, "https://api.example", "https://api.example/orders", "https://api.example/orders/bulk")
            .Resolve(new Uri("https://api.example/orders/bulk/7"));

        Assert.That(scope!.Key, Is.EqualTo("api.example/orders/bulk"));
        Assert.That(scope.RuleName, Is.EqualTo("rule2"));
    }

    [Test]
    public void Prefix_matches_on_segment_boundaries_only()
    {
        var scope = Matcher(null, "https://api.example/orders").Resolve(new Uri("https://api.example/orders-archive/1"));

        Assert.That(scope!.Limit, Is.Null);
        Assert.That(scope.Key, Is.EqualTo("api.example/"));
    }

    [Test]
    public void Match_ignores_case_and_scheme()
    {
        var scope = Matcher(null, "https://API.example/Orders/").Resolve(new Uri("http://api.EXAMPLE/orders/1"));

        Assert.That(scope!.Key, Is.EqualTo("api.example/Orders"));
    }

    [Test]
    public void Port_is_part_of_the_host()
    {
        var scope = Matcher(null, "https://api.example:8443").Resolve(new Uri("https://api.example/orders"));

        Assert.That(scope!.Limit, Is.Null);
    }

    [Test]
    public void Catch_all_applies_per_host()
    {
        var matcher = Matcher(new CatchAllThrottlingRule { PermitLimit = 5 }, "https://api.example/orders");

        var a = matcher.Resolve(new Uri("https://one.example/x"));
        var b = matcher.Resolve(new Uri("https://two.example/y"));

        Assert.That(a!.Key, Is.EqualTo("one.example/"));
        Assert.That(b!.Key, Is.EqualTo("two.example/"));
        Assert.That(a.RuleName, Is.EqualTo("*"));
        Assert.That(a.Limit, Is.Not.Null);
    }

    [Test]
    public void Relative_uri_has_no_scope()
        => Assert.That(Matcher().Resolve(new Uri("/orders", UriKind.Relative)), Is.Null);
}

public class OptionsValidationTests
{
    [Test]
    public void Defaults_are_valid()
        => Assert.That(OptionsValidation.Validate(new HttpResilienceOptions()), Is.Empty);

    [Test]
    public void Total_shorter_than_attempt_is_invalid()
        => Assert.That(OptionsValidation.Validate(new HttpResilienceOptions { AttemptTimeout = TimeSpan.FromSeconds(30), TotalTimeout = TimeSpan.FromSeconds(10) }), Has.Count.EqualTo(1));

    [Test]
    public void Max_delay_shorter_than_base_delay_is_invalid()
    {
        var options = new HttpResilienceOptions();
        options.TransientRetry.MaxDelay = TimeSpan.FromSeconds(1);

        Assert.That(OptionsValidation.Validate(options), Has.Some.Contains("MaxDelay"));
    }

    [Test]
    public void Default_retry_after_above_max_is_invalid()
        => Assert.That(OptionsValidation.Validate(new ThrottleRetryOptions { DefaultRetryAfter = TimeSpan.FromMinutes(5) }), Has.Some.Contains("DefaultRetryAfter"));

    [Test]
    public void Negative_retries_are_invalid()
        => Assert.That(OptionsValidation.Validate(new ThrottleRetryOptions { MaxRetryAttempts = -1 }), Has.Some.Contains("MaxRetryAttempts"));

    [Test]
    public void Default_total_timeout_covers_every_wait()
    {
        // (3 + 1) × 30 s + 3 × 15 s + 3 × 120 s
        Assert.That(new HttpResilienceOptions().GetEffectiveTotalTimeout(), Is.EqualTo(TimeSpan.FromSeconds(525)));
    }

    [Test]
    public void Explicit_total_timeout_wins()
        => Assert.That(new HttpResilienceOptions { TotalTimeout = TimeSpan.FromSeconds(42) }.GetEffectiveTotalTimeout(), Is.EqualTo(TimeSpan.FromSeconds(42)));

    [Test]
    public void Without_retries_the_total_is_one_attempt()
    {
        var options = new HttpResilienceOptions { AttemptTimeout = TimeSpan.FromSeconds(10) };
        options.TransientRetry.MaxRetryAttempts = 0;
        options.ThrottleRetry.MaxRetryAttempts = 0;

        Assert.That(options.GetEffectiveTotalTimeout(), Is.EqualTo(TimeSpan.FromSeconds(10)));
    }
}

public class ThrottlingRulesValidationTests
{
    private static ValidateOptionsResult Validate(Action<OutboundThrottlingOptions> configure)
    {
        var options = new OutboundThrottlingOptions();
        configure(options);
        return new OutboundThrottlingOptionsValidator().Validate(null, options);
    }

    [Test]
    public void Valid_rules_pass()
        => Assert.That(Validate(o => o.Rules.Add(new ThrottlingRule { Name = "a", BaseAddress = new Uri("https://a.example"), PermitLimit = 1 })).Succeeded, Is.True);

    [Test]
    public void Name_and_absolute_base_address_are_required()
    {
        var result = Validate(o => o.Rules.Add(new ThrottlingRule { BaseAddress = new Uri("/x", UriKind.Relative), PermitLimit = 1 }));

        Assert.That(result.Failures, Has.Some.Contains("Name"));
        Assert.That(result.Failures, Has.Some.Contains("absolute"));
    }

    [Test]
    public void Two_rules_for_the_same_prefix_are_rejected()
    {
        var result = Validate(o =>
        {
            o.Rules.Add(new ThrottlingRule { Name = "a", BaseAddress = new Uri("https://a.example/orders"), PermitLimit = 1 });
            o.Rules.Add(new ThrottlingRule { Name = "b", BaseAddress = new Uri("http://A.example/orders/"), PermitLimit = 1 });
        });

        Assert.That(result.Failures, Has.Some.Contains("already covers"));
    }

    [Test]
    public void Token_bucket_needs_a_refill()
    {
        var result = Validate(o => o.CatchAll = new CatchAllThrottlingRule
        {
            Algorithm = RateLimitAlgorithm.TokenBucket, PermitLimit = 1, TokensPerPeriod = 0, ReplenishmentPeriod = TimeSpan.Zero,
        });

        Assert.That(result.Failures, Has.Count.EqualTo(2));
    }
}

public class InMemoryThrottleStateStoreTests
{
    [Test]
    public async Task Block_is_visible_until_it_expires()
    {
        var clock = new FakeTimeProvider();
        var store = new InMemoryThrottleStateStore(clock);
        var until = clock.GetUtcNow().AddSeconds(10);

        await store.BlockAsync("a/", until, CancellationToken.None);
        Assert.That(await store.GetBlockedUntilAsync("a/", CancellationToken.None), Is.EqualTo(until));
        Assert.That(await store.GetBlockedUntilAsync("b/", CancellationToken.None), Is.Null);

        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.That(await store.GetBlockedUntilAsync("a/", CancellationToken.None), Is.Null);
    }

    [Test]
    public async Task Block_is_never_shortened()
    {
        var clock = new FakeTimeProvider();
        var store = new InMemoryThrottleStateStore(clock);
        var later = clock.GetUtcNow().AddSeconds(60);

        await store.BlockAsync("a/", later, CancellationToken.None);
        await store.BlockAsync("a/", clock.GetUtcNow().AddSeconds(5), CancellationToken.None);

        Assert.That(await store.GetBlockedUntilAsync("a/", CancellationToken.None), Is.EqualTo(later));
    }
}

public class LocalRateLimiterFactoryTests
{
    private static ThrottleScope Scope(string key, RateLimitAlgorithm algorithm)
        => new(key, "h", "/", "r", new ThrottlingRule { Algorithm = algorithm, PermitLimit = 2 });

    [TestCase(RateLimitAlgorithm.SlidingWindow, typeof(SlidingWindowRateLimiter))]
    [TestCase(RateLimitAlgorithm.FixedWindow, typeof(FixedWindowRateLimiter))]
    [TestCase(RateLimitAlgorithm.TokenBucket, typeof(TokenBucketRateLimiter))]
    [TestCase(RateLimitAlgorithm.Concurrency, typeof(ConcurrencyLimiter))]
    public void Algorithm_picks_the_limiter(RateLimitAlgorithm algorithm, Type expected)
    {
        using var factory = new LocalRateLimiterFactory();

        Assert.That(factory.GetLimiter(Scope("k", algorithm)), Is.TypeOf(expected));
    }

    [Test]
    public void One_limiter_per_scope_key()
    {
        using var factory = new LocalRateLimiterFactory();

        var first = factory.GetLimiter(Scope("a", RateLimitAlgorithm.Concurrency));

        Assert.That(factory.GetLimiter(Scope("a", RateLimitAlgorithm.Concurrency)), Is.SameAs(first));
        Assert.That(factory.GetLimiter(Scope("b", RateLimitAlgorithm.Concurrency)), Is.Not.SameAs(first));
        Assert.That(factory.ActiveLimiters, Has.Count.EqualTo(2));
    }

    [Test]
    public void Concurrency_limiter_enforces_its_limit()
    {
        using var factory = new LocalRateLimiterFactory();
        var limiter = factory.GetLimiter(Scope("a", RateLimitAlgorithm.Concurrency));

        using var one = limiter.AttemptAcquire();
        using var two = limiter.AttemptAcquire();
        using var three = limiter.AttemptAcquire();

        Assert.That(new[] { one.IsAcquired, two.IsAcquired, three.IsAcquired }, Is.EqualTo(new[] { true, true, false }));
    }
}

public class RetrySafetyTests
{
    [TestCase("GET", true)]
    [TestCase("HEAD", true)]
    [TestCase("PUT", true)]
    [TestCase("DELETE", true)]
    [TestCase("OPTIONS", true)]
    [TestCase("POST", false)]
    [TestCase("PATCH", false)]
    public void Idempotent_methods_are_retry_safe(string method, bool expected)
        => Assert.That(TransientRetry.IsRetrySafe(new HttpRequestMessage(new HttpMethod(method), "https://a.example"), new TransientRetryOptions()), Is.EqualTo(expected));

    [Test]
    public void Idempotency_key_header_makes_a_post_retry_safe()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "https://a.example");
        request.Headers.Add("Idempotency-Key", "1");

        Assert.That(TransientRetry.IsRetrySafe(request, new TransientRetryOptions()), Is.True);
    }

    [Test]
    public void Unknown_request_is_not_retry_safe()
        => Assert.That(TransientRetry.IsRetrySafe(null, new TransientRetryOptions()), Is.False);
}
