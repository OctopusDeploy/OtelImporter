using System.Text.Json;
using OtelImporter.Otlp;
using OtelImporter.Pipeline;

namespace OtelImporter.Tests;

public class BatchSplitterTests
{
    // A span padded to roughly a known size via its name, so byte budgets are easy to reason about.
    static Span SpanOfSize(int approxBytes) => new() { Name = new string('x', approxBytes) };

    static ExportTraceServiceRequest Request(params Span[] spans) => new()
    {
        ResourceSpans =
        [
            new ResourceSpans
            {
                Resource = new Resource { Attributes = [new KeyValue { Key = "service.name", Value = new AnyValue { StringValue = "svc" } }] },
                ScopeSpans = [new ScopeSpans { Scope = new InstrumentationScope { Name = "scope" }, Spans = [.. spans] }],
            },
        ],
    };

    static long SizeOf(ExportTraceServiceRequest request) =>
        JsonSerializer.SerializeToUtf8Bytes(request, OtlpJsonContext.Default.ExportTraceServiceRequest).Length;

    static IEnumerable<Span> SpansOf(ExportTraceServiceRequest request) =>
        from rs in request.ResourceSpans ?? []
        from ss in rs.ScopeSpans ?? []
        from s in ss.Spans ?? []
        select s;

    [Fact]
    public void LeavesABatchUnderTheLimitUntouched()
    {
        var request = Request(SpanOfSize(10), SpanOfSize(10));

        var result = BatchSplitter.Split(request, maxBytes: 10_000);

        Assert.Equal(0, result.SkippedSpanCount);
        Assert.Single(result.Batches);
        Assert.Same(request, result.Batches[0]); // forwarded as-is, not rebuilt
    }

    [Fact]
    public void SplitsAnOversizedBatchIntoPiecesThatEachFit()
    {
        // Ten ~100-byte spans; force a small budget so it must split into several batches.
        var request = Request([.. Enumerable.Range(0, 10).Select(_ => SpanOfSize(100))]);
        var limit = SizeOf(request) / 3; // require at least ~3 batches

        var result = BatchSplitter.Split(request, limit);

        Assert.True(result.Batches.Count > 1);
        Assert.Equal(0, result.SkippedSpanCount);
        // No span is lost, and every produced batch is within the limit.
        Assert.Equal(10, result.Batches.Sum(b => SpansOf(b).Count()));
        Assert.All(result.Batches, b => Assert.True(SizeOf(b) <= limit));
    }

    [Fact]
    public void PreservesResourceAndScopeMetadataInEachPiece()
    {
        var request = Request([.. Enumerable.Range(0, 6).Select(_ => SpanOfSize(100))]);

        var result = BatchSplitter.Split(request, SizeOf(request) / 2);

        Assert.True(result.Batches.Count > 1);
        Assert.All(result.Batches, b =>
        {
            var rs = Assert.Single(b.ResourceSpans!);
            Assert.Equal("svc", rs.Resource!.Attributes![0].Value!.StringValue);
            var ss = Assert.Single(rs.ScopeSpans!);
            Assert.Equal("scope", ss.Scope!.Name);
        });
    }

    [Fact]
    public void SkipsASpanThatExceedsTheLimitOnItsOwnButKeepsTheRest()
    {
        var small1 = SpanOfSize(50);
        var huge = SpanOfSize(5_000);
        var small2 = SpanOfSize(50);
        var request = Request(small1, huge, small2);

        // Budget big enough for a small span + envelope, far too small for the huge one.
        var result = BatchSplitter.Split(request, maxBytes: 500);

        Assert.Equal(1, result.SkippedSpanCount);
        var names = result.Batches.SelectMany(SpansOf).Select(s => s.Name).ToList();
        Assert.Equal(2, names.Count);
        Assert.DoesNotContain(huge.Name, names);
    }
}
