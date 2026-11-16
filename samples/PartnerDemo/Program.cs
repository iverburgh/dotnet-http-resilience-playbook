// Runs a flaky fake partner in-process and calls it through the resilience pipeline.
// `dotnet run --project samples/PartnerDemo` prints what the pipeline did for each call.
using System.Collections.Concurrent;
using System.Diagnostics;
using HttpResilience;
using HttpResilience.Throttling;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

await using var partner = FakePartner.Build();
await partner.StartAsync();
var partnerAddress = new Uri(partner.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());

var builder = Host.CreateApplicationBuilder(args);
builder.Services.Configure<HttpResilienceOptions>("partner", builder.Configuration.GetSection("Partner"));
builder.Services.AddOutboundThrottlingRules(builder.Configuration.GetSection(OutboundThrottlingOptions.SectionName));
builder.Services.AddHttpClient("partner", client => client.BaseAddress = partnerAddress)
    .AddHttpResilience();

using var host = builder.Build();
var client = host.Services.GetRequiredService<IHttpClientFactory>().CreateClient("partner");

await Show("GET  /orders/7      503, 503, then 200: retried", () => client.GetAsync("/orders/7"));
await Show("POST /orders        503: not retried, POST is not idempotent", () => client.PostAsync("/orders", null));
await Show("POST /orders + key  503, then 201: retried, the Idempotency-Key makes it safe", () =>
{
    var request = new HttpRequestMessage(HttpMethod.Post, "/orders?attempts=2");
    request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
    return client.SendAsync(request);
});
await Show("GET  /slow          each attempt times out after 1 s", () => client.GetAsync("/slow"));
await Show("GET  /catalog       429 with Retry-After: 1, then 200", () => client.GetAsync("/catalog"));

static async Task Show(string title, Func<Task<HttpResponseMessage>> call)
{
    FakePartner.Calls.Clear();
    var watch = Stopwatch.StartNew();
    string outcome;
    try
    {
        using var response = await call();
        outcome = $"{(int)response.StatusCode} {response.StatusCode}";
    }
    catch (Exception ex)
    {
        outcome = ex.GetType().Name;
    }

    Console.WriteLine($"{title}\n     -> {outcome} after {FakePartner.Calls.Values.Sum()} call(s), {watch.Elapsed.TotalSeconds:0.0} s\n");
}

internal static class FakePartner
{
    public static readonly ConcurrentDictionary<string, int> Calls = new();

    public static WebApplication Build()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();

        app.MapGet("/orders/{id}", (string id) => Count($"order-{id}") <= 2 ? Results.StatusCode(503) : Results.Ok(new { id }));
        app.MapPost("/orders", (int? attempts) =>
        {
            var call = Count("post");
            return attempts is { } n && call >= n ? Results.StatusCode(201) : Results.StatusCode(503);
        });
        app.MapGet("/slow", async (CancellationToken cancellationToken) =>
        {
            Count("slow");
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            return Results.Ok();
        });
        app.MapGet("/catalog", (HttpContext context) =>
        {
            if (Count("catalog") == 1)
            {
                context.Response.Headers.RetryAfter = "1";
                return Results.StatusCode(429);
            }

            return Results.Ok(new[] { "bolts", "nuts" });
        });

        return app;
    }

    private static int Count(string key) => Calls.AddOrUpdate(key, 1, (_, n) => n + 1);
}
