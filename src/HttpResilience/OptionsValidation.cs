namespace HttpResilience;

/// <summary>Validates options before a pipeline is built, so a mistake fails at the first call, loudly.</summary>
internal static class OptionsValidation
{
    public static IReadOnlyList<string> Validate(HttpResilienceOptions options)
    {
        var errors = new List<string>();
        if (options.AttemptTimeout <= TimeSpan.Zero)
        {
            errors.Add($"{nameof(HttpResilienceOptions.AttemptTimeout)} must be positive.");
        }

        if (options.TotalTimeout is { } total && total < options.AttemptTimeout)
        {
            errors.Add($"{nameof(HttpResilienceOptions.TotalTimeout)} ({total}) must not be shorter than {nameof(HttpResilienceOptions.AttemptTimeout)} ({options.AttemptTimeout}).");
        }

        var retry = options.TransientRetry;
        if (retry.MaxRetryAttempts < 0)
        {
            errors.Add($"{nameof(TransientRetryOptions)}.{nameof(TransientRetryOptions.MaxRetryAttempts)} must not be negative.");
        }

        if (retry.BaseDelay < TimeSpan.Zero)
        {
            errors.Add($"{nameof(TransientRetryOptions)}.{nameof(TransientRetryOptions.BaseDelay)} must not be negative.");
        }

        if (retry.MaxDelay < retry.BaseDelay)
        {
            errors.Add($"{nameof(TransientRetryOptions)}.{nameof(TransientRetryOptions.MaxDelay)} must not be shorter than {nameof(TransientRetryOptions.BaseDelay)}.");
        }

        errors.AddRange(Validate(options.ThrottleRetry));
        return errors;
    }

    public static IReadOnlyList<string> Validate(ThrottleRetryOptions options)
    {
        var errors = new List<string>();
        if (options.MaxRetryAttempts < 0)
        {
            errors.Add($"{nameof(ThrottleRetryOptions)}.{nameof(ThrottleRetryOptions.MaxRetryAttempts)} must not be negative.");
        }

        if (options.DefaultRetryAfter < TimeSpan.Zero)
        {
            errors.Add($"{nameof(ThrottleRetryOptions)}.{nameof(ThrottleRetryOptions.DefaultRetryAfter)} must not be negative.");
        }

        if (options.MaxRetryAfter <= TimeSpan.Zero)
        {
            errors.Add($"{nameof(ThrottleRetryOptions)}.{nameof(ThrottleRetryOptions.MaxRetryAfter)} must be positive.");
        }

        if (options.DefaultRetryAfter > options.MaxRetryAfter)
        {
            errors.Add($"{nameof(ThrottleRetryOptions)}.{nameof(ThrottleRetryOptions.DefaultRetryAfter)} must not exceed {nameof(ThrottleRetryOptions.MaxRetryAfter)}.");
        }

        return errors;
    }

    public static void ThrowIfInvalid<TOptions>(string clientName, IReadOnlyList<string> errors)
    {
        if (errors.Count > 0)
        {
            throw new Microsoft.Extensions.Options.OptionsValidationException(clientName, typeof(TOptions), errors);
        }
    }
}
