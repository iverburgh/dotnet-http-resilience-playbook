using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace HttpResilience.Redis;

internal sealed class RedisThrottlingOptionsValidator(IServiceProviderIsService isService) : IValidateOptions<RedisThrottlingOptions>
{
    public ValidateOptionsResult Validate(string? name, RedisThrottlingOptions options)
    {
        var failures = new List<string>();
        var hasConnectionString = !string.IsNullOrWhiteSpace(options.ConnectionString);
        var hasEndpoint = !string.IsNullOrWhiteSpace(options.Endpoint);

        if (hasConnectionString && hasEndpoint)
        {
            failures.Add("Set either ConnectionString or Endpoint, not both.");
        }
        else if (!hasConnectionString && !hasEndpoint && !isService.IsService(typeof(IConnectionMultiplexer)))
        {
            failures.Add("Set ConnectionString or Endpoint, or register an IConnectionMultiplexer.");
        }

        if (string.IsNullOrWhiteSpace(options.KeyPrefix))
        {
            failures.Add("KeyPrefix is required.");
        }

        if (options.ConcurrencyLeaseTimeout <= TimeSpan.Zero)
        {
            failures.Add("ConcurrencyLeaseTimeout must be positive.");
        }

        if (!Enum.IsDefined(options.LimiterFailureMode))
        {
            failures.Add($"Unknown LimiterFailureMode '{options.LimiterFailureMode}'.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
