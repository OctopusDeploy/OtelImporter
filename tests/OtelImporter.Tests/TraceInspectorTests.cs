using OtelImporter.Inspect;
using OtelImporter.Otlp;

namespace OtelImporter.Tests;

public class TraceInspectorTests
{
    // 1 second in nanoseconds; span timestamps are unix-nanos.
    const ulong Second = 1_000_000_000UL;
    static readonly ulong Base = 1_700_000_000UL * Second; // some 2023-era epoch-nanos

    static ExportTraceServiceRequest Batch(params Span[] spans) => new()
    {
        ResourceSpans =
        [
            new ResourceSpans { ScopeSpans = [new ScopeSpans { Spans = [.. spans] }] },
        ],
    };

    static Span SpanWith(string? name, ulong startNano = 0) =>
        new() { Name = name, StartTimeUnixNano = startNano };

    [Fact]
    public void CountsSpansAcrossBatches()
    {
        var inspector = new TraceInspector();
        inspector.Add(Batch(SpanWith("a"), SpanWith("b")));
        inspector.Add(Batch(SpanWith("c")));

        var summary = inspector.BuildSummary(batchCount: 2);

        Assert.Equal(2, summary.BatchCount);
        Assert.Equal(3, summary.SpanCount);
    }

    [Fact]
    public void GroupsByNameAndUsesProxyForMissingNames()
    {
        var inspector = new TraceInspector();
        inspector.Add(Batch(SpanWith("GET"), SpanWith("GET"), SpanWith(null), SpanWith("")));

        var summary = inspector.BuildSummary(batchCount: 1);

        Assert.Contains(new SpanNameCount("GET", 2), summary.TopSpanNames);
        Assert.Contains(new SpanNameCount(TraceInspector.NoName, 2), summary.TopSpanNames);
    }

    [Fact]
    public void TopSpanNamesIsLimitedToTenOrderedByCount()
    {
        var inspector = new TraceInspector();
        // 12 distinct names, each appearing (index+1) times => name "n11" is most frequent.
        for (var i = 0; i < 12; i++)
            for (var n = 0; n <= i; n++)
                inspector.Add(Batch(SpanWith($"n{i:D2}")));

        var summary = inspector.BuildSummary(batchCount: 1);

        Assert.Equal(10, summary.TopSpanNames.Count);
        // Descending by count: the two least frequent (n00=1, n01=2) fall off the bottom.
        Assert.Equal("n11", summary.TopSpanNames[0].Name);
        Assert.Equal(12, summary.TopSpanNames[0].Count);
        Assert.Equal("n02", summary.TopSpanNames[9].Name);
        Assert.DoesNotContain(summary.TopSpanNames, s => s.Name is "n00" or "n01");
    }

    [Fact]
    public void TiedCountsAreOrderedByNameForDeterminism()
    {
        var inspector = new TraceInspector();
        inspector.Add(Batch(SpanWith("zebra"), SpanWith("apple"), SpanWith("mango")));

        var summary = inspector.BuildSummary(batchCount: 1);

        Assert.Equal(["apple", "mango", "zebra"], summary.TopSpanNames.Select(s => s.Name));
    }

    [Fact]
    public void TracksOldestAndNewestTimestampsAndDuration()
    {
        var inspector = new TraceInspector();
        inspector.Add(Batch(
            SpanWith("a", Base + 5 * Second),
            SpanWith("b", Base + 1 * Second),   // oldest
            SpanWith("c", Base + 12 * Second))); // newest

        var summary = inspector.BuildSummary(batchCount: 1);

        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(1_700_000_001), summary.OldestSpan);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(1_700_000_012), summary.NewestSpan);
        Assert.Equal(TimeSpan.FromSeconds(11), summary.Duration);
    }

    [Fact]
    public void IgnoresZeroTimestampsForOldestNewest()
    {
        var inspector = new TraceInspector();
        inspector.Add(Batch(
            SpanWith("a"),                       // start 0 -> ignored
            SpanWith("b", Base + 3 * Second)));

        var summary = inspector.BuildSummary(batchCount: 1);

        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(1_700_000_003), summary.OldestSpan);
        Assert.Equal(summary.OldestSpan, summary.NewestSpan);
        Assert.Equal(TimeSpan.Zero, summary.Duration);
    }

    [Fact]
    public void EmptyInputProducesNullTimestampsAndNoNames()
    {
        var summary = new TraceInspector().BuildSummary(batchCount: 0);

        Assert.Equal(0, summary.SpanCount);
        Assert.Null(summary.OldestSpan);
        Assert.Null(summary.NewestSpan);
        Assert.Null(summary.Duration);
        Assert.Empty(summary.TopSpanNames);
    }
}
