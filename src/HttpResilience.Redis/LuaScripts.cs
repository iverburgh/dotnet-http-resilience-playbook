namespace HttpResilience.Redis;

/// <summary>
/// Lua scripts that check and update a limit in one round trip. Each returns
/// <c>{allowed (0|1), remaining, retryAfterMs (-1 = no hint)}</c> and reads the Redis clock, so instances with
/// skewed clocks still agree.
/// </summary>
internal static class LuaScripts
{
    // TIME is non-deterministic; old Redis versions need effect replication before a write that follows it.
    private const string Now = """
        if redis.replicate_commands then redis.replicate_commands() end
        local t = redis.call('TIME')
        local now = tonumber(t[1]) * 1000 + math.floor(tonumber(t[2]) / 1000)
        """;

    /// <summary>ARGV: limit, windowMs.</summary>
    public const string FixedWindow = """
        local limit = tonumber(ARGV[1])
        local windowMs = tonumber(ARGV[2])
        local count = redis.call('INCR', KEYS[1])
        if count == 1 then redis.call('PEXPIRE', KEYS[1], windowMs) end
        local ttl = redis.call('PTTL', KEYS[1])
        if ttl < 0 then
          redis.call('PEXPIRE', KEYS[1], windowMs)
          ttl = windowMs
        end
        if count > limit then return {0, 0, ttl} end
        return {1, limit - count, 0}
        """;

    /// <summary>ARGV: limit, windowMs, unique member.</summary>
    public const string SlidingWindow = Now + """

        local limit = tonumber(ARGV[1])
        local windowMs = tonumber(ARGV[2])
        redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', now - windowMs)
        local count = redis.call('ZCARD', KEYS[1])
        if count >= limit then
          local oldest = redis.call('ZRANGE', KEYS[1], 0, 0, 'WITHSCORES')
          local retry = windowMs
          if oldest[2] then retry = tonumber(oldest[2]) + windowMs - now end
          if retry < 1 then retry = 1 end
          return {0, 0, retry}
        end
        redis.call('ZADD', KEYS[1], now, ARGV[3])
        redis.call('PEXPIRE', KEYS[1], windowMs)
        return {1, limit - count - 1, 0}
        """;

    /// <summary>ARGV: capacity, tokensPerPeriod, periodMs.</summary>
    public const string TokenBucket = Now + """

        local capacity = tonumber(ARGV[1])
        local perPeriod = tonumber(ARGV[2])
        local periodMs = tonumber(ARGV[3])
        local state = redis.call('HMGET', KEYS[1], 'tokens', 'ts')
        local tokens = tonumber(state[1])
        local ts = tonumber(state[2])
        if tokens == nil or ts == nil then
          tokens = capacity
          ts = now
        end
        local periods = math.floor((now - ts) / periodMs)
        if periods > 0 then
          tokens = math.min(capacity, tokens + periods * perPeriod)
          ts = ts + periods * periodMs
        end
        local allowed = 0
        local retry = 0
        if tokens >= 1 then
          tokens = tokens - 1
          allowed = 1
        else
          retry = ts + periodMs - now
          if retry < 1 then retry = 1 end
        end
        redis.call('HSET', KEYS[1], 'tokens', tokens, 'ts', ts)
        redis.call('PEXPIRE', KEYS[1], (math.ceil(capacity / perPeriod) + 1) * periodMs)
        return {allowed, tokens, retry}
        """;

    /// <summary>ARGV: limit, leaseMs, unique member. Entries are scored by expiry, so a dead holder frees its permit.</summary>
    public const string Concurrency = Now + """

        local limit = tonumber(ARGV[1])
        local leaseMs = tonumber(ARGV[2])
        redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', now)
        local count = redis.call('ZCARD', KEYS[1])
        if count >= limit then return {0, 0, -1} end
        redis.call('ZADD', KEYS[1], now + leaseMs, ARGV[3])
        redis.call('PEXPIRE', KEYS[1], leaseMs)
        return {1, limit - count - 1, 0}
        """;

    /// <summary>
    /// Sets the blocked-until time, but only to a later moment. ARGV: untilMs, ttlMs.
    /// (<c>until</c> is a reserved word in Lua.)
    /// </summary>
    public const string Block = """
        local untilMs = tonumber(ARGV[1])
        local raw = redis.call('GET', KEYS[1])
        local current = nil
        if raw then current = tonumber(raw) end
        if current == nil or untilMs > current then
          redis.call('SET', KEYS[1], ARGV[1], 'PX', ARGV[2])
          return 1
        end
        return 0
        """;
}
