using Azure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace HttpResilience.Redis;

/// <summary>
/// One connection for the limiters and the state store. Uses a registered <see cref="IConnectionMultiplexer"/>
/// when there is one; otherwise connects on first use, and tries again on the next call if that failed.
/// </summary>
internal sealed class RedisConnection : IDisposable
{
    private readonly RedisThrottlingOptions _options;
    private readonly IConnectionMultiplexer? _shared;
    private readonly Lock _gate = new();
    private Task<IConnectionMultiplexer>? _connecting;

    public RedisConnection(IOptions<RedisThrottlingOptions> options, IServiceProvider services)
    {
        _options = options.Value;
        _shared = services.GetService<IConnectionMultiplexer>();
    }

    public string KeyPrefix => _options.KeyPrefix;

    public async Task<IDatabase> GetDatabaseAsync(CancellationToken cancellationToken)
    {
        if (_shared is not null)
        {
            return _shared.GetDatabase();
        }

        Task<IConnectionMultiplexer> connecting;
        lock (_gate)
        {
            if (_connecting is null || _connecting.IsFaulted || _connecting.IsCanceled)
            {
                _connecting = ConnectAsync(_options);
            }

            connecting = _connecting;
        }

        var connection = await connecting.WaitAsync(cancellationToken).ConfigureAwait(false);
        return connection.GetDatabase();
    }

    private static async Task<IConnectionMultiplexer> ConnectAsync(RedisThrottlingOptions options)
    {
        ConfigurationOptions configuration;
        if (!string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            configuration = ConfigurationOptions.Parse(options.ConnectionString);
        }
        else
        {
            configuration = ConfigurationOptions.Parse(options.Endpoint!);
            await configuration.ConfigureForAzureWithTokenCredentialAsync(options.Credential ?? new DefaultAzureCredential()).ConfigureAwait(false);
        }

        // Keep the multiplexer when Redis is down at startup; it reconnects in the background.
        configuration.AbortOnConnectFail = false;
        return await ConnectionMultiplexer.ConnectAsync(configuration).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_connecting is { IsCompletedSuccessfully: true } connecting)
        {
            connecting.Result.Dispose();
        }
    }
}
