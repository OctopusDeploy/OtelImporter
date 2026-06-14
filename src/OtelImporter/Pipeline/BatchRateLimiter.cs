namespace OtelImporter.Pipeline;

// A simple leaky-bucket-style throttle that caps the batch send rate. The first
// batch goes immediately; each subsequent batch waits until its scheduled slot.
// When the import is already slower than the target rate, no delay is added and no
// "burst credit" is accumulated. Time is supplied via TimeProvider so the behaviour
// is deterministically testable with FakeTimeProvider.
internal sealed class BatchRateLimiter : IRateLimiter
{
    readonly TimeSpan _interval;
    readonly TimeProvider _timeProvider;

    DateTimeOffset _next;
    bool _started;

    public BatchRateLimiter(double batchesPerSecond, TimeProvider timeProvider)
    {
        if (batchesPerSecond <= 0)
            throw new ArgumentOutOfRangeException(nameof(batchesPerSecond), batchesPerSecond, "Rate must be positive.");

        _interval = TimeSpan.FromSeconds(1.0 / batchesPerSecond);
        _timeProvider = timeProvider;
    }

    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();

        if (!_started)
        {
            _started = true;
            _next = now + _interval;
            return;
        }

        if (now < _next)
        {
            await Task.Delay(_next - now, _timeProvider, cancellationToken).ConfigureAwait(false);
            _next += _interval;
        }
        else
        {
            // Behind schedule already; reset rather than bursting to catch up.
            _next = now + _interval;
        }
    }
}
