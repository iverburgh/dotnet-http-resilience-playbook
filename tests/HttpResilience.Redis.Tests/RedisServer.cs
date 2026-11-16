using Testcontainers.Redis;

namespace HttpResilience.Redis.Tests;

/// <summary>
/// Finds a Redis for the tests: <c>REDIS_CONNECTION_STRING</c> when set, otherwise a container. Without either the
/// tests that need Redis are skipped, unless <c>REDIS_TESTS_REQUIRED=true</c> (as in CI), where they fail.
/// </summary>
[SetUpFixture]
public sealed class RedisServer
{
    private static RedisContainer? _container;
    private static string? _connectionString;
    private static string? _unavailableReason;

    public static string ConnectionString
    {
        get
        {
            if (_connectionString is not null)
            {
                return _connectionString;
            }

            var message = $"No Redis available: {_unavailableReason}";
            if (string.Equals(Environment.GetEnvironmentVariable("REDIS_TESTS_REQUIRED"), "true", StringComparison.OrdinalIgnoreCase))
            {
                Assert.Fail(message);
            }

            Assert.Ignore(message);
            return null!;
        }
    }

    /// <summary>A key prefix per test, so tests do not see each other's counters.</summary>
    public static string UniquePrefix() => $"test-{Guid.NewGuid():N}";

    [OneTimeSetUp]
    public async Task StartAsync()
    {
        _connectionString = Environment.GetEnvironmentVariable("REDIS_CONNECTION_STRING");
        if (!string.IsNullOrWhiteSpace(_connectionString))
        {
            return;
        }

        _connectionString = null;
        try
        {
            _container = new RedisBuilder("redis:7.4-alpine").Build();
            await _container.StartAsync();
            _connectionString = _container.GetConnectionString();
        }
        catch (Exception ex)
        {
            _unavailableReason = $"set REDIS_CONNECTION_STRING or start Docker ({ex.GetType().Name}: {ex.Message})";
            if (_container is not null)
            {
                await _container.DisposeAsync();
                _container = null;
            }
        }
    }

    [OneTimeTearDown]
    public async Task StopAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}
