using System.Text.Json;
using OtelImporter.Export;
using OtelImporter.Otlp;

namespace OtelImporter.Tests;

// Splitting is the exporters' job now, measured in each transport's real wire format. These
// tests drive Prepare directly (no network) and inspect the produced frames.
public class ExporterSplittingTests
{
    // One resource carrying several named scopes, each with a padded span.
    static ExportTraceServiceRequest MultiScopeRequest(int scopes, int spansPerScope, int spanPadding)
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
                        .. Enumerable.Range(0, scopes).Select(s => new ScopeSpans
                        {
                            Scope = new InstrumentationScope { Name = $"scope-{s}" },
                            Spans = [.. Enumerable.Range(0, spansPerScope).Select(i => new Span { Name = $"{pad}-s{s}-{i}" })],
                        }),
                    ],
                },
            ],
        };
    }

    static OtlpHttpExporter HttpExporter(long maxBatchBytes) =>
        new(new HttpClient(), new Uri("http://localhost:4318"), maxBatchBytes: maxBatchBytes);

    static OtlpGrpcExporter GrpcExporter(long maxBatchBytes) =>
        new(new HttpClient(), new Uri("http://localhost:4317"), maxBatchBytes: maxBatchBytes);

    // --- JSON (HTTP) ---

    static List<string> JsonSpanNames(ReadOnlyMemory<byte> frame)
    {
        var request = JsonSerializer.Deserialize(frame.Span, OtlpJsonContext.Default.ExportTraceServiceRequest)!;
        return [.. from rs in request.ResourceSpans ?? []
                   from ss in rs.ScopeSpans ?? []
                   from s in ss.Spans ?? []
                   select s.Name!];
    }

    [Fact]
    public void HttpSplitsIntoFramesWithinTheLimitPreservingEverySpan()
    {
        var request = MultiScopeRequest(scopes: 8, spansPerScope: 1, spanPadding: 200);
        var prepared = HttpExporter(maxBatchBytes: 800).Prepare(request);

        Assert.Equal(0, prepared.SkippedSpanCount);
        Assert.True(prepared.Frames.Count > 1, $"expected a split, got {prepared.Frames.Count} frame(s)");
        Assert.All(prepared.Frames, f => Assert.True(f.Length <= 800, $"frame of {f.Length} bytes exceeds 800"));

        var names = prepared.Frames.SelectMany(JsonSpanNames).ToList();
        Assert.Equal(8, names.Count);
        Assert.Equal(Enumerable.Range(0, 8).Select(s => $"{new string('x', 200)}-s{s}-0"), names);
    }

    [Fact]
    public void HttpPacksSeveralScopesPerFrame()
    {
        // Budget = the exact size of a two-scope frame, so packing puts two scopes per frame:
        // six scopes become fewer than six frames rather than one-per-scope.
        var limit = HttpExporter(maxBatchBytes: long.MaxValue)
            .Prepare(MultiScopeRequest(scopes: 2, spansPerScope: 1, spanPadding: 60)).Frames[0].Length;

        var prepared = HttpExporter(limit).Prepare(MultiScopeRequest(scopes: 6, spansPerScope: 1, spanPadding: 60));

        Assert.True(prepared.Frames.Count is > 1 and < 6, $"expected packing, got {prepared.Frames.Count} frame(s)");
        Assert.Equal(6, prepared.Frames.Sum(f => JsonSpanNames(f).Count));
    }

    [Fact]
    public void HttpSkipsASpanLargerThanTheLimitButSendsTheRest()
    {
        var request = new ExportTraceServiceRequest
        {
            ResourceSpans =
            [
                new ResourceSpans
                {
                    ScopeSpans = [new ScopeSpans { Spans =
                    [
                        new Span { Name = "a" },
                        new Span { Name = new string('x', 5_000) },
                        new Span { Name = "b" },
                    ] }],
                },
            ],
        };

        var prepared = HttpExporter(maxBatchBytes: 500).Prepare(request);

        Assert.Equal(1, prepared.SkippedSpanCount);
        Assert.Equal(["a", "b"], prepared.Frames.SelectMany(JsonSpanNames));
    }

    // --- protobuf (gRPC) ---
    // Frame sizing/skip is asserted here; that every span survives a protobuf split round-trip
    // is covered end to end by the gRPC integration test (a real OTLP server counts the spans).

    [Fact]
    public void GrpcSplitsIntoFramesWithinTheLimit()
    {
        var request = MultiScopeRequest(scopes: 8, spansPerScope: 1, spanPadding: 200);
        var prepared = GrpcExporter(maxBatchBytes: 800).Prepare(request);

        Assert.Equal(0, prepared.SkippedSpanCount);
        Assert.True(prepared.Frames.Count > 1, $"expected a split, got {prepared.Frames.Count} frame(s)");
        Assert.All(prepared.Frames, f => Assert.True(f.Length <= 800, $"frame of {f.Length} bytes exceeds 800"));
    }

    [Fact]
    public void GrpcSkipsASpanLargerThanTheLimit()
    {
        var request = new ExportTraceServiceRequest
        {
            ResourceSpans =
            [
                new ResourceSpans
                {
                    ScopeSpans = [new ScopeSpans { Spans =
                    [
                        new Span { Name = "a" },
                        new Span { Name = new string('x', 5_000) },
                        new Span { Name = "b" },
                    ] }],
                },
            ],
        };

        var prepared = GrpcExporter(maxBatchBytes: 400).Prepare(request);

        Assert.Equal(1, prepared.SkippedSpanCount);
        Assert.True(prepared.Frames.Count >= 1);
        Assert.All(prepared.Frames, f => Assert.True(f.Length <= 400));
    }

    [Fact]
    public void WithNoLimitTheWholeBatchIsASingleFrame()
    {
        var request = MultiScopeRequest(scopes: 4, spansPerScope: 3, spanPadding: 50);

        Assert.Single(HttpExporter(maxBatchBytes: long.MaxValue).Prepare(request).Frames);
        Assert.Single(GrpcExporter(maxBatchBytes: long.MaxValue).Prepare(request).Frames);
    }
}
