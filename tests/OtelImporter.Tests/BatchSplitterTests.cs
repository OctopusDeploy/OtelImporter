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

    // One resource carrying several named scopes, each with a few padded spans.
    static ExportTraceServiceRequest MultiScopeRequest(string[] scopeNames, int spansPerScope, int spanPadding)
    {
        var pad = new string('x', spanPadding);
        return new ExportTraceServiceRequest
        {
            ResourceSpans =
            [
                new ResourceSpans
                {
                    Resource = new Resource { Attributes = [new KeyValue { Key = "service.name", Value = new AnyValue { StringValue = "svc" } }] },
                    ScopeSpans =
                    [
                        .. scopeNames.Select(name => new ScopeSpans
                        {
                            Scope = new InstrumentationScope { Name = name },
                            Spans = [.. Enumerable.Range(0, spansPerScope).Select(i => new Span { Name = $"{pad}-{name}-{i}" })],
                        }),
                    ],
                },
            ],
        };
    }

    [Fact]
    public void PacksMultipleScopesIntoFewerBatches()
    {
        var request = MultiScopeRequest(["s0", "s1", "s2", "s3"], spansPerScope: 2, spanPadding: 80);

        // Budget = the exact size of a two-scope batch, so packing should put two scopes per
        // request -- four scopes become two batches rather than one-per-scope (four).
        var twoScopes = new ExportTraceServiceRequest
        {
            ResourceSpans =
            [
                new ResourceSpans
                {
                    Resource = request.ResourceSpans![0].Resource,
                    ScopeSpans = [.. request.ResourceSpans![0].ScopeSpans!.Take(2)],
                },
            ],
        };
        var limit = SizeOf(twoScopes);

        var result = BatchSplitter.Split(request, limit);

        Assert.Equal(0, result.SkippedSpanCount);
        Assert.Equal(8, result.Batches.Sum(b => SpansOf(b).Count())); // 4 scopes * 2 spans, nothing lost
        Assert.All(result.Batches, b => Assert.True(SizeOf(b) <= limit));
        // Packing worked: fewer batches than scopes, and a batch holding more than one scope.
        Assert.True(result.Batches.Count < 4, $"expected scopes to share batches, got {result.Batches.Count}");
        Assert.Contains(result.Batches, b => b.ResourceSpans!.Sum(rs => rs.ScopeSpans!.Count) > 1);
        // Every scope survives, with its name intact.
        var scopeNames = result.Batches
            .SelectMany(b => b.ResourceSpans!)
            .SelectMany(rs => rs.ScopeSpans!)
            .Select(ss => ss.Scope!.Name);
        Assert.Equal(["s0", "s1", "s2", "s3"], scopeNames);
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
