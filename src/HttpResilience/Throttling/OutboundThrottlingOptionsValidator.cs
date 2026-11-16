using Microsoft.Extensions.Options;

namespace HttpResilience.Throttling;

/// <summary>Rejects throttling rules that would silently not limit anything, or limit the wrong thing.</summary>
internal sealed class OutboundThrottlingOptionsValidator : IValidateOptions<OutboundThrottlingOptions>
{
    public ValidateOptionsResult Validate(string? name, OutboundThrottlingOptions options)
    {
        var failures = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < options.Rules.Count; i++)
        {
            var rule = options.Rules[i];
            var label = string.IsNullOrWhiteSpace(rule.Name) ? $"Rules[{i}]" : $"Rules[{i}] ({rule.Name})";

            if (string.IsNullOrWhiteSpace(rule.Name))
            {
                failures.Add($"{label}: Name is required.");
            }

            if (rule.BaseAddress is null || !rule.BaseAddress.IsAbsoluteUri)
            {
                failures.Add($"{label}: BaseAddress must be an absolute URI.");
            }
            else if (!seen.Add(ThrottleRuleMatcher.Describe(rule.BaseAddress)))
            {
                failures.Add($"{label}: another rule already covers {ThrottleRuleMatcher.Describe(rule.BaseAddress)}.");
            }

            ValidateLimit(rule, label, failures);
        }

        if (options.CatchAll is not null)
        {
            ValidateLimit(options.CatchAll, "CatchAll", failures);
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateLimit(ThrottlingRuleBase rule, string label, List<string> failures)
    {
        if (!Enum.IsDefined(rule.Algorithm))
        {
            failures.Add($"{label}: unknown Algorithm '{rule.Algorithm}'.");
        }

        if (rule.PermitLimit <= 0)
        {
            failures.Add($"{label}: PermitLimit must be positive.");
        }

        if (rule.QueueLimit < 0)
        {
            failures.Add($"{label}: QueueLimit must not be negative.");
        }

        switch (rule.Algorithm)
        {
            case RateLimitAlgorithm.SlidingWindow:
                if (rule.SegmentsPerWindow <= 0)
                {
                    failures.Add($"{label}: SegmentsPerWindow must be positive.");
                }

                goto case RateLimitAlgorithm.FixedWindow;
            case RateLimitAlgorithm.FixedWindow:
                if (rule.Window <= TimeSpan.Zero)
                {
                    failures.Add($"{label}: Window must be positive.");
                }

                break;
            case RateLimitAlgorithm.TokenBucket:
                if (rule.TokensPerPeriod <= 0)
                {
                    failures.Add($"{label}: TokensPerPeriod must be positive.");
                }

                if (rule.ReplenishmentPeriod <= TimeSpan.Zero)
                {
                    failures.Add($"{label}: ReplenishmentPeriod must be positive.");
                }

                break;
        }
    }
}
