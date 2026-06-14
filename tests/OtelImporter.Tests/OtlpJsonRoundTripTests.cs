using System.Text;
using System.Text.Json;
using OtelImporter.Otlp;

namespace OtelImporter.Tests;

// The HTTP exporter no longer forwards bytes verbatim: it deserializes each OTLP/JSON
// line into the object model and re-serializes it (parity with the gRPC path, which
// re-encodes as protobuf). These tests guard that round-trip -- data is preserved and
// the re-serialized output is clean OTLP/JSON.
public class OtlpJsonRoundTripTests
{
    const string SampleJson = """
        {"resourceSpans":[{"resource":{"attributes":[{"key":"service.name","value":{"stringValue":"Octopus.Tests"}}]},"scopeSpans":[{"scope":{"name":"System.Net.Http","version":"1.0"},"spans":[{"traceId":"fd44a1405ea764583b4993562fd72b5f","spanId":"7096a3a8ee440a87","parentSpanId":"03f02aeda865a704","name":"POST","kind":3,"startTimeUnixNano":"1779760521808778000","endTimeUnixNano":"1779760521962478800","flags":257,"attributes":[{"key":"http.request.method","value":{"stringValue":"POST"}},{"key":"server.port","value":{"intValue":443}}],"events":[{"timeUnixNano":"1779760521900000000","name":"exception"}]}]}]}]}
        """;

    static ExportTraceServiceRequest Deserialize(ReadOnlySpan<byte> json) =>
        JsonSerializer.Deserialize(json, OtlpJsonContext.Default.ExportTraceServiceRequest)!;

    static byte[] Serialize(ExportTraceServiceRequest request) =>
        JsonSerializer.SerializeToUtf8Bytes(request, OtlpJsonContext.Default.ExportTraceServiceRequest);

    [Fact]
    public void RoundTripPreservesSpanData()
    {
        var original = Deserialize(Encoding.UTF8.GetBytes(SampleJson));

        // Re-serialize then parse again: the model must survive a full round-trip unchanged.
        var reparsed = Deserialize(Serialize(original));

        var span = reparsed.ResourceSpans![0].ScopeSpans![0].Spans![0];
        Assert.Equal("fd44a1405ea764583b4993562fd72b5f", span.TraceId);
        Assert.Equal("7096a3a8ee440a87", span.SpanId);
        Assert.Equal("03f02aeda865a704", span.ParentSpanId);
        Assert.Equal("POST", span.Name);
        Assert.Equal(3, span.Kind);
        Assert.Equal(1779760521808778000UL, span.StartTimeUnixNano);
        Assert.Equal(1779760521962478800UL, span.EndTimeUnixNano);
        Assert.Equal(257u, span.Flags);

        Assert.Equal("service.name", reparsed.ResourceSpans[0].Resource!.Attributes![0].Key);
        Assert.Equal("Octopus.Tests", reparsed.ResourceSpans[0].Resource!.Attributes![0].Value!.StringValue);
        Assert.Equal("System.Net.Http", reparsed.ResourceSpans[0].ScopeSpans![0].Scope!.Name);

        Assert.Equal(2, span.Attributes!.Count);
        Assert.Equal(443, span.Attributes[1].Value!.IntValue);

        Assert.Single(span.Events!);
        Assert.Equal("exception", span.Events![0].Name);
        Assert.Equal(1779760521900000000UL, span.Events[0].TimeUnixNano);
    }

    [Fact]
    public void ReSerializedOutputOmitsAbsentOptionalFields()
    {
        var json = Encoding.UTF8.GetString(Serialize(Deserialize(Encoding.UTF8.GetBytes(SampleJson))));

        // Optional fields that were absent from the input must not reappear as nulls.
        Assert.DoesNotContain("null", json);
        Assert.DoesNotContain("\"status\"", json);     // no status in the sample
        Assert.DoesNotContain("\"links\"", json);      // no links in the sample
        Assert.DoesNotContain("\"traceState\"", json); // not set

        // The oneof AnyValue only emits the set arm.
        Assert.Contains("\"stringValue\":\"POST\"", json);
        Assert.DoesNotContain("\"boolValue\"", json);
    }
}
