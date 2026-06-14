namespace OtelImporter.Export;

// Controls retry-with-backoff behaviour. MaxAttempts includes the initial try
// (so MaxAttempts == 1 disables retries).
internal sealed record RetryOptions(int MaxAttempts, TimeSpan BaseDelay, TimeSpan MaxDelay)
{
    public static readonly RetryOptions Default = new(
        MaxAttempts: 5,
        BaseDelay: TimeSpan.FromMilliseconds(500),
        MaxDelay: TimeSpan.FromSeconds(30));

    public static RetryOptions FromMaxRetries(int maxRetries) =>
        Default with { MaxAttempts = Math.Max(1, maxRetries + 1) };
}
