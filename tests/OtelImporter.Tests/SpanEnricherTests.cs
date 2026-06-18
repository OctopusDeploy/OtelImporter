using OtelImporter.Otlp;
using OtelImporter.Pipeline;

namespace OtelImporter.Tests;

public class SpanEnricherTests
{
    static ExportTraceServiceRequest Batch(params Span[] spans) => new()
    {
        ResourceSpans = [new ResourceSpans { ScopeSpans = [new ScopeSpans { Spans = [.. spans] }] }],
    };

    static (string Key, string? Value)[] AttributesOf(Span span) =>
        [.. (span.Attributes ?? []).Select(a => (a.Key!, a.Value?.StringValue))];

    [Fact]
    public void AppendsLogFileNameAndCustomAttributesToEverySpan()
    {
        var enricher = SpanEnricher.Create(
            logFileName: "traces-1234.jsonl.zst",
            attributes: [new("octopus.prop", "abc"), new("octopus.otherprop", "def")]);

        var spanA = new Span { Name = "a" };
        var spanB = new Span { Name = "b" };
        enricher.Enrich(Batch(spanA, spanB));

        (string, string?)[] expected =
        [
            ("log.file.name", "traces-1234.jsonl.zst"),
            ("octopus.prop", "abc"),
            ("octopus.otherprop", "def"),
        ];
        Assert.Equal(expected, AttributesOf(spanA));
        Assert.Equal(expected, AttributesOf(spanB));
    }

    [Fact]
    public void PreservesExistingSpanAttributes()
    {
        var enricher = SpanEnricher.Create(logFileName: "f.jsonl", attributes: []);
        var span = new Span
        {
            Attributes = [new KeyValue { Key = "existing", Value = new AnyValue { StringValue = "keep" } }],
        };

        enricher.Enrich(Batch(span));

        Assert.Equal([("existing", "keep"), ("log.file.name", "f.jsonl")], AttributesOf(span));
    }

    [Fact]
    public void SuppressesLogFileNameWhenNotSupplied()
    {
        var enricher = SpanEnricher.Create(logFileName: null, attributes: [new("only", "this")]);
        var span = new Span();

        enricher.Enrich(Batch(span));

        Assert.Equal([("only", "this")], AttributesOf(span));
    }

    [Fact]
    public void WithNoAttributesIsANoOp()
    {
        var enricher = SpanEnricher.Create(logFileName: null, attributes: []);
        var span = new Span();

        enricher.Enrich(Batch(span));

        Assert.Null(span.Attributes);
    }

    [Fact]
    public void ToleratesBatchesWithNoSpans()
    {
        var enricher = SpanEnricher.Create(logFileName: "f.jsonl", attributes: []);

        // Should not throw on null ResourceSpans / ScopeSpans / Spans.
        enricher.Enrich(new ExportTraceServiceRequest());
        enricher.Enrich(new ExportTraceServiceRequest { ResourceSpans = [new ResourceSpans()] });
        enricher.Enrich(new ExportTraceServiceRequest
        {
            ResourceSpans = [new ResourceSpans { ScopeSpans = [new ScopeSpans()] }],
        });
    }
}
