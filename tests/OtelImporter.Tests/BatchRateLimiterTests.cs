using Microsoft.Extensions.Time.Testing;
using OtelImporter.Pipeline;

namespace OtelImporter.Tests;

public class BatchRateLimiterTests
{
    [Fact]
    public async Task FirstBatchIsNotDelayed()
    {
        var time = new FakeTimeProvider();
        var limiter = new BatchRateLimiter(2, time);

        var first = limiter.WaitAsync(CancellationToken.None);

        Assert.True(first.IsCompleted); // returns immediately, without advancing the clock
        await first;
    }

    [Fact]
    public async Task SubsequentBatchesArePacedToTheInterval()
    {
        var time = new FakeTimeProvider();
        var limiter = new BatchRateLimiter(2, time); // 2/sec => 500ms interval

        await limiter.WaitAsync(CancellationToken.None); // immediate

        var second = limiter.WaitAsync(CancellationToken.None);
        Assert.False(second.IsCompleted); // waiting for its slot

        time.Advance(TimeSpan.FromMilliseconds(499));
        Assert.False(second.IsCompleted);

        time.Advance(TimeSpan.FromMilliseconds(1)); // 500ms total
        await second; // now released
    }

    [Fact]
    public async Task DoesNotDelayWhenAlreadySlowerThanTheRate()
    {
        var time = new FakeTimeProvider();
        var limiter = new BatchRateLimiter(10, time); // 100ms interval

        var first = limiter.WaitAsync(CancellationToken.None);
        Assert.True(first.IsCompleted);
        await first;

        time.Advance(TimeSpan.FromSeconds(1)); // slow processing, longer than the interval
        var second = limiter.WaitAsync(CancellationToken.None);
        Assert.True(second.IsCompleted); // no throttling needed
        await second;
    }

    [Fact]
    public void RejectsNonPositiveRate()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BatchRateLimiter(0, new FakeTimeProvider()));
    }
}
