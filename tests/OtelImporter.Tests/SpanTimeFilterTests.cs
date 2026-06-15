using OtelImporter.Otlp;
using OtelImporter.Pipeline;

namespace OtelImporter.Tests;

public class SpanTimeFilterTests
{
    const ulong Second = 1_000_000_000UL;
    static readonly DateTimeOffset Epoch = DateTimeOffset.UnixEpoch;

    static ExportTraceServiceRequest Batch(params ulong[] startNanos) => new()
    {
        ResourceSpans =
        [
            new ResourceSpans
            {
                ScopeSpans = [new ScopeSpans { Spans = [.. startNanos.Select(n => new Span { StartTimeUnixNano = n })] }],
            },
        ],
    };

    static ulong[] RemainingStarts(ExportTraceServiceRequest request) =>
        [.. request.ResourceSpans![0].ScopeSpans![0].Spans!.Select(s => s.StartTimeUnixNano)];

    [Fact]
    public void NoBoundsMeansNoFilter()
    {
        Assert.Null(SpanTimeFilter.Create(null, null));
    }

    [Fact]
    public void DropsSpansBeforeFromInclusive()
    {
        // from = epoch + 10s -> nanos at exactly 10s are kept, 9s dropped.
        var filter = SpanTimeFilter.Create(Epoch.AddSeconds(10), null)!;
        var batch = Batch(9 * Second, 10 * Second, 11 * Second);

        filter.Apply(batch);

        Assert.Equal([10 * Second, 11 * Second], RemainingStarts(batch));
    }

    [Fact]
    public void DropsSpansAfterToInclusive()
    {
        var filter = SpanTimeFilter.Create(null, Epoch.AddSeconds(10))!;
        var batch = Batch(9 * Second, 10 * Second, 11 * Second);

        filter.Apply(batch);

        Assert.Equal([9 * Second, 10 * Second], RemainingStarts(batch));
    }

    [Fact]
    public void KeepsOnlySpansWithinTheWindow()
    {
        var filter = SpanTimeFilter.Create(Epoch.AddSeconds(10), Epoch.AddSeconds(20))!;
        var batch = Batch(5 * Second, 10 * Second, 15 * Second, 20 * Second, 25 * Second);

        filter.Apply(batch);

        Assert.Equal([10 * Second, 15 * Second, 20 * Second], RemainingStarts(batch));
    }

    [Fact]
    public void KeepsSpansWithUnsetTimestamp()
    {
        // A span with start 0 (unset) is never dropped, even with both bounds set.
        var filter = SpanTimeFilter.Create(Epoch.AddSeconds(10), Epoch.AddSeconds(20))!;
        var batch = Batch(0, 15 * Second, 5 * Second);

        filter.Apply(batch);

        Assert.Equal([0, 15 * Second], RemainingStarts(batch));
    }

    [Fact]
    public void HasSpansReflectsWhatSurvives()
    {
        var filter = SpanTimeFilter.Create(Epoch.AddSeconds(100), null)!;
        var batch = Batch(1 * Second, 2 * Second); // all before 'from'

        Assert.True(SpanTimeFilter.HasSpans(batch));
        filter.Apply(batch);
        Assert.False(SpanTimeFilter.HasSpans(batch));
    }

    [Fact]
    public void RespectsExplicitOffsetInBound()
    {
        // 01:00:10 at +01:00 is the same instant as 00:00:10 UTC (epoch + 10s); the
        // comparison must happen in UTC, not against the wall-clock 01:00:10.
        var from = new DateTimeOffset(1970, 1, 1, 1, 0, 10, TimeSpan.FromHours(1));
        var filter = SpanTimeFilter.Create(from, null)!;
        var batch = Batch(9 * Second, 11 * Second);

        filter.Apply(batch);

        Assert.Equal([11 * Second], RemainingStarts(batch));
    }
}
