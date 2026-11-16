using System.Collections.Concurrent;
using System.Net;
using System.Threading.RateLimiting;
using HttpResilience.Throttling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Time.Testing;

namespace HttpResilience.Tests.Pipeline;

/// <summary>The dependency: answers each call with the next scripted response.</summary>
internal sealed class FakePartner : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> _respond;
    private int _calls;

    public FakePartner(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;

    public FakePartner(params HttpStatusCode[] statuses)
        : this((_, call, _) => Task.FromResult(new HttpResponseMessage(statuses[Math.Min(call, statuses.Length - 1)])))
    {
    }

    public int Calls => Volatile.Read(ref _calls);

    public ConcurrentQueue<(HttpMethod Method, Uri? Uri)> Requests { get; } = new();

    public static HttpResponseMessage TooManyRequests(TimeSpan? retryAfter)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        if (retryAfter is { } delta)
        {
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(delta);
        }

        return response;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Enqueue((request.Method, request.RequestUri));
        var call = Interlocked.Increment(ref _calls) - 1;
        var response = await _respond(request, call, cancellationToken);
        response.RequestMessage ??= request;
        return response;
    }
}

/// <summary>Counts how often it runs: shows whether a retry re-runs the handlers inside the pipeline.</summary>
internal sealed class CountingHandler : DelegatingHandler
{
    public int Calls;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Calls);
        return base.SendAsync(request, cancellationToken);
    }
}

/// <summary>A limiter that answers from a script, so tests do not depend on the wall clock.</summary>
internal sealed class ScriptedLimiter : RateLimiter
{
    private readonly ConcurrentQueue<TimeSpan?> _rejections = new();

    public int Acquired;

    /// <summary>Queues a rejection; <paramref name="retryAfter"/> becomes the lease's retry hint.</summary>
    public ScriptedLimiter Reject(TimeSpan? retryAfter, int times = 1)
    {
        for (var i = 0; i < times; i++)
        {
            _rejections.Enqueue(retryAfter);
        }

        return this;
    }

    public override TimeSpan? IdleDuration => null;

    public override RateLimiterStatistics? GetStatistics() => new() { CurrentAvailablePermits = 7 };

    protected override RateLimitLease AttemptAcquireCore(int permitCount) => Next();

    protected override ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken) => ValueTask.FromResult(Next());

    private RateLimitLease Next()
    {
        if (_rejections.TryDequeue(out var retryAfter))
        {
            return new Lease(false, retryAfter);
        }

        Interlocked.Increment(ref Acquired);
        return new Lease(true, null);
    }

    private sealed class Lease(bool acquired, TimeSpan? retryAfter) : RateLimitLease
    {
        public override bool IsAcquired => acquired;

        public override IEnumerable<string> MetadataNames => retryAfter is null ? [] : [MetadataName.RetryAfter.Name];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            metadata = retryAfter;
            return retryAfter is not null && metadataName == MetadataName.RetryAfter.Name;
        }
    }
}

internal sealed class ScriptedLimiterFactory(RateLimiter limiter) : IRateLimiterFactory
{
    public bool Throw { get; set; }

    public List<ThrottleScope> Scopes { get; } = [];

    public IReadOnlyCollection<KeyValuePair<ThrottleScope, RateLimiter>> ActiveLimiters
        => Scopes.Select(s => KeyValuePair.Create(s, limiter)).ToArray();

    public RateLimiter GetLimiter(ThrottleScope scope)
    {
        if (Throw)
        {
            throw new InvalidOperationException("limiter backend down");
        }

        Scopes.Add(scope);
        return limiter;
    }
}

/// <summary>A service provider with one resilient client named <c>partner</c>, on a fake clock.</summary>
internal sealed class TestHost : IDisposable
{
    public const string ClientName = "partner";
    public static readonly Uri BaseAddress = new("https://api.partner.example");

    private readonly ServiceProvider _provider;

    private TestHost(ServiceProvider provider, FakeTimeProvider clock)
    {
        _provider = provider;
        Clock = clock;
    }

    public FakeTimeProvider Clock { get; }

    public IServiceProvider Services => _provider;

    public static TestHost Create(
        HttpMessageHandler partner,
        Action<HttpResilienceOptions, ResilienceHandlerContext>? configure = null,
        Action<IServiceCollection>? services = null,
        Action<IHttpClientBuilder>? client = null,
        bool throttlingOnly = false)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.Zero));
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddSingleton<TimeProvider>(clock);

        var builder = collection.AddHttpClient(ClientName, c => c.BaseAddress = BaseAddress);
        if (throttlingOnly)
        {
            builder.AddOutboundThrottling();
        }
        else
        {
            builder.AddHttpResilience((options, context) =>
            {
                // Fast defaults; individual tests override what they exercise.
                options.TransientRetry.UseJitter = false;
                configure?.Invoke(options, context);
            });
        }

        client?.Invoke(builder);
        builder.ConfigurePrimaryHttpMessageHandler(() => partner).SetHandlerLifetime(Timeout.InfiniteTimeSpan);

        services?.Invoke(collection);
        return new TestHost(collection.BuildServiceProvider(validateScopes: true), clock);
    }

    public static Action<IServiceCollection> WithLimiter(IRateLimiterFactory factory, Action<OutboundThrottlingOptions>? rules = null)
        => services =>
        {
            services.AddOutboundThrottlingRules(rules ?? (o => o.CatchAll = new CatchAllThrottlingRule { PermitLimit = 1 }));
            services.Replace(ServiceDescriptor.Singleton(factory));
        };

    public HttpClient CreateClient() => Services.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName);

    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();

    /// <summary>Sends <paramref name="request"/> and moves the fake clock until the pipeline is done.</summary>
    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request) => DriveAsync(CreateClient().SendAsync(request));

    public Task<HttpResponseMessage> GetAsync(string path) => SendAsync(new HttpRequestMessage(HttpMethod.Get, path));

    /// <summary>Advances the clock in steps of <paramref name="step"/> (1 s by default) until <paramref name="task"/> completes.</summary>
    public async Task<T> DriveAsync<T>(Task<T> task, TimeSpan? step = null)
    {
        for (var i = 0; !task.IsCompleted; i++)
        {
            if (i > 5_000)
            {
                Assert.Fail("The pipeline did not finish; is it waiting on real time?");
            }

            await Task.Delay(1);
            if (!task.IsCompleted)
            {
                Clock.Advance(step ?? TimeSpan.FromSeconds(1));
            }
        }

        return await task;
    }

    public void Dispose() => _provider.Dispose();
}
