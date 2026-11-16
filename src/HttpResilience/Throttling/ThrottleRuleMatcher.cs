using Microsoft.Extensions.Options;

namespace HttpResilience.Throttling;

/// <summary>
/// What a request is throttled as: the host plus the path prefix of the rule that matched it.
/// The 429 gate and the rate limiter share this key, so a 429 on <c>/orders</c> does not block <c>/catalog</c>
/// when both have their own rule.
/// </summary>
/// <param name="Key">Stable identity, for example <c>api.partner.example/orders</c>.</param>
/// <param name="Host">Host and port, lowercase.</param>
/// <param name="PathPrefix">The matched prefix, or <c>/</c> for the host as a whole.</param>
/// <param name="RuleName">Name of the matched rule; <c>*</c> for the catch-all; empty when nothing matched.</param>
/// <param name="Limit">The limit to apply, or <see langword="null"/> when the request is not rate limited.</param>
public sealed record ThrottleScope(string Key, string Host, string PathPrefix, string RuleName, ThrottlingRuleBase? Limit);

/// <summary>Finds the rule for a request: same host, longest path prefix that matches on a segment boundary.</summary>
internal sealed class ThrottleRuleMatcher
{
    private readonly (string Host, string Prefix, ThrottlingRule Rule)[] _rules;
    private readonly CatchAllThrottlingRule? _catchAll;

    public ThrottleRuleMatcher(IOptions<OutboundThrottlingOptions> options)
    {
        var value = options.Value;
        _catchAll = value.CatchAll;
        _rules = value.Rules
            .Where(r => r.BaseAddress is { IsAbsoluteUri: true })
            .Select(r => (Host: r.BaseAddress!.Authority.ToLowerInvariant(), Prefix: NormalizePrefix(r.BaseAddress!.AbsolutePath), Rule: r))
            .OrderByDescending(r => r.Prefix.Length)
            .ToArray();
    }

    public ThrottleScope? Resolve(Uri? requestUri)
    {
        if (requestUri is not { IsAbsoluteUri: true })
        {
            return null;
        }

        var host = requestUri.Authority.ToLowerInvariant();
        var path = requestUri.AbsolutePath;
        foreach (var (ruleHost, prefix, rule) in _rules)
        {
            if (ruleHost == host && PrefixMatches(prefix, path))
            {
                return new ThrottleScope(host + prefix, host, prefix, rule.Name, rule);
            }
        }

        return _catchAll is null
            ? new ThrottleScope(host + "/", host, "/", string.Empty, null)
            : new ThrottleScope(host + "/", host, "/", "*", _catchAll);
    }

    internal static string Describe(Uri baseAddress)
        => baseAddress.Authority.ToLowerInvariant() + NormalizePrefix(baseAddress.AbsolutePath);

    private static string NormalizePrefix(string path)
    {
        var trimmed = path.TrimEnd('/');
        return trimmed.Length == 0 ? "/" : trimmed;
    }

    private static bool PrefixMatches(string prefix, string path)
        => prefix == "/"
        || path.Equals(prefix, StringComparison.OrdinalIgnoreCase)
        || (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && path.Length > prefix.Length && path[prefix.Length] == '/');
}
