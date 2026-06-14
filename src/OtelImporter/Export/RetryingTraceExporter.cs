using OtelImporter.Otlp;

namespace OtelImporter.Export;

// Wraps another exporter, retrying transient failures with exponential backoff.
// Transient = the inner exporter raised a retryable TraceExportException, or a
// network/timeout error. A server-supplied Retry-After hint overrides the computed
// backoff. Time is supplied via TimeProvider so tests run without real waits.
internal sealed class RetryingTraceExporter : ITraceExporter
{
    readonly ITraceExporter _inner;
    readonly RetryOptions _options;
    readonly TimeProvider _timeProvider;
    readonly Action<string>? _onRetry;

    public RetryingTraceExporter(
        ITraceExporter inner,
        RetryOptions options,
        TimeProvider timeProvider,
        Action<string>? onRetry = null)
    {
        _inner = inner;
        _options = options;
        _timeProvider = timeProvider;
        _onRetry = onRetry;
    }

    public async Task<ExportOutcome> ExportAsync(ExportTraceServiceRequest request, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await _inner.ExportAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (attempt >= _options.MaxAttempts || !IsTransient(ex, cancellationToken, out var retryAfter))
                    throw;

                var delay = retryAfter ?? Backoff(attempt);
                _onRetry?.Invoke(
                    $"transient export failure (attempt {attempt}/{_options.MaxAttempts}), retrying in {delay.TotalSeconds:F1}s: {ex.Message}");
                await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    static bool IsTransient(Exception ex, CancellationToken cancellationToken, out TimeSpan? retryAfter)
    {
        retryAfter = null;

        // A genuine user cancellation should never be retried.
        if (cancellationToken.IsCancellationRequested)
            return false;

        switch (ex)
        {
            case TraceExportException trace:
                retryAfter = trace.RetryAfter;
                return trace.IsRetryable;
            case HttpRequestException:        // connection refused, reset, DNS, etc.
            case IOException:                 // dropped mid-stream
            case TimeoutException:            // HttpClient timeout (newer runtimes)
            case TaskCanceledException:       // HttpClient timeout (token not cancelled, checked above)
                return true;
            default:
                return false;
        }
    }

    TimeSpan Backoff(int attempt)
    {
        // base * 2^(attempt-1), capped at MaxDelay. Shift is clamped to avoid overflow.
        var shift = Math.Min(attempt - 1, 30);
        var ticks = _options.BaseDelay.Ticks * (1L << shift);
        if (ticks <= 0 || ticks > _options.MaxDelay.Ticks)
            ticks = _options.MaxDelay.Ticks;
        return TimeSpan.FromTicks(ticks);
    }

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
