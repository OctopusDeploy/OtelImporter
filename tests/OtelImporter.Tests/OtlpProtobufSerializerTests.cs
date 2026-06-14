using System.Text;
using System.Text.Json;
using OtelImporter.Otlp;

namespace OtelImporter.Tests;

public class OtlpProtobufSerializerTests
{
    // One realistic OTLP/JSON line (the same shape the importer reads from *.jsonl files).
    const string SampleJson = """
        {"resourceSpans":[{"resource":{"attributes":[{"key":"service.name","value":{"stringValue":"Octopus.Tests"}}]},"scopeSpans":[{"scope":{"name":"System.Net.Http","version":"1.0"},"spans":[{"traceId":"fd44a1405ea764583b4993562fd72b5f","spanId":"7096a3a8ee440a87","parentSpanId":"03f02aeda865a704","name":"POST","kind":3,"startTimeUnixNano":"1779760521808778000","endTimeUnixNano":"1779760521962478800","flags":257,"attributes":[{"key":"http.request.method","value":{"stringValue":"POST"}},{"key":"server.port","value":{"intValue":443}}],"events":[{"timeUnixNano":"1779760521900000000","name":"exception"}]}]}]}]}
        """;

    static ExportTraceServiceRequest Deserialize(string json) =>
        JsonSerializer.Deserialize(json, OtlpJsonContext.Default.ExportTraceServiceRequest)!;

    [Fact]
    public void Round_trips_span_scalars_through_protobuf()
    {
        var request = Deserialize(SampleJson);
        var bytes = OtlpProtobufSerializer.Serialize(request);

        var span = FindFirstSpan(bytes);

        // trace_id (1) / span_id (2) / parent_span_id (4) are raw bytes of the hex.
        Assert.Equal(Convert.FromHexString("fd44a1405ea764583b4993562fd72b5f"), FirstLengthDelimited(span, 1));
        Assert.Equal(Convert.FromHexString("7096a3a8ee440a87"), FirstLengthDelimited(span, 2));
        Assert.Equal(Convert.FromHexString("03f02aeda865a704"), FirstLengthDelimited(span, 4));

        // name (5)
        Assert.Equal("POST", Encoding.UTF8.GetString(FirstLengthDelimited(span, 5)));
        // kind (6) varint
        Assert.Equal(3UL, Varint(span, 6));
        // start/end (7/8) fixed64
        Assert.Equal(1779760521808778000UL, Fixed64(span, 7));
        Assert.Equal(1779760521962478800UL, Fixed64(span, 8));
        // flags (16) fixed32
        Assert.Equal(257U, Fixed32(span, 16));
    }

    [Fact]
    public void Round_trips_string_and_int_attributes()
    {
        var request = Deserialize(SampleJson);
        var bytes = OtlpProtobufSerializer.Serialize(request);
        var span = FindFirstSpan(bytes);

        var attributes = AllLengthDelimited(span, 9);
        Assert.Equal(2, attributes.Count);

        // First attribute: http.request.method => stringValue "POST"
        Assert.Equal("http.request.method", Encoding.UTF8.GetString(FirstLengthDelimited(attributes[0], 1)));
        var firstValue = FirstLengthDelimited(attributes[0], 2);
        Assert.Equal("POST", Encoding.UTF8.GetString(FirstLengthDelimited(firstValue, 1))); // string_value = 1

        // Second attribute: server.port => intValue 443
        Assert.Equal("server.port", Encoding.UTF8.GetString(FirstLengthDelimited(attributes[1], 1)));
        var secondValue = FirstLengthDelimited(attributes[1], 2);
        Assert.Equal(443UL, Varint(secondValue, 3)); // int_value = 3
    }

    [Fact]
    public void Round_trips_events()
    {
        var request = Deserialize(SampleJson);
        var bytes = OtlpProtobufSerializer.Serialize(request);
        var span = FindFirstSpan(bytes);

        var events = AllLengthDelimited(span, 11);
        var spanEvent = Assert.Single(events);
        Assert.Equal(1779760521900000000UL, Fixed64(spanEvent, 1));
        Assert.Equal("exception", Encoding.UTF8.GetString(FirstLengthDelimited(spanEvent, 2)));
    }

    [Fact]
    public void Resource_and_scope_are_preserved()
    {
        var request = Deserialize(SampleJson);
        var bytes = OtlpProtobufSerializer.Serialize(request);

        var resourceSpans = FirstLengthDelimited(bytes, 1);
        var resource = FirstLengthDelimited(resourceSpans, 1);
        var resourceAttribute = FirstLengthDelimited(resource, 1);
        Assert.Equal("service.name", Encoding.UTF8.GetString(FirstLengthDelimited(resourceAttribute, 1)));

        var scopeSpans = FirstLengthDelimited(resourceSpans, 2);
        var scope = FirstLengthDelimited(scopeSpans, 1);
        Assert.Equal("System.Net.Http", Encoding.UTF8.GetString(FirstLengthDelimited(scope, 1)));
        Assert.Equal("1.0", Encoding.UTF8.GetString(FirstLengthDelimited(scope, 2)));
    }

    // --- navigation helpers (ExportTraceServiceRequest -> ResourceSpans -> ScopeSpans -> Span) ---

    static byte[] FindFirstSpan(ReadOnlySpan<byte> request)
    {
        var resourceSpans = FirstLengthDelimited(request, 1);
        var scopeSpans = FirstLengthDelimited(resourceSpans, 2);
        return FirstLengthDelimited(scopeSpans, 2);
    }

    static byte[] FirstLengthDelimited(ReadOnlySpan<byte> message, int fieldNumber) =>
        AllLengthDelimited(message, fieldNumber)[0];

    static List<byte[]> AllLengthDelimited(ReadOnlySpan<byte> message, int fieldNumber)
    {
        var results = new List<byte[]>();
        var reader = new TestProtoReader(message);
        while (!reader.End)
        {
            var (field, wireType) = reader.ReadTag();
            if (field == fieldNumber && wireType == 2)
                results.Add(reader.ReadLengthDelimited().ToArray());
            else
                reader.Skip(wireType);
        }
        return results;
    }

    static ulong Varint(ReadOnlySpan<byte> message, int fieldNumber)
    {
        var reader = new TestProtoReader(message);
        while (!reader.End)
        {
            var (field, wireType) = reader.ReadTag();
            if (field == fieldNumber && wireType == 0)
                return reader.ReadVarint();
            reader.Skip(wireType);
        }
        throw new InvalidOperationException($"varint field {fieldNumber} not found.");
    }

    static ulong Fixed64(ReadOnlySpan<byte> message, int fieldNumber)
    {
        var reader = new TestProtoReader(message);
        while (!reader.End)
        {
            var (field, wireType) = reader.ReadTag();
            if (field == fieldNumber && wireType == 1)
                return reader.ReadFixed64();
            reader.Skip(wireType);
        }
        throw new InvalidOperationException($"fixed64 field {fieldNumber} not found.");
    }

    static uint Fixed32(ReadOnlySpan<byte> message, int fieldNumber)
    {
        var reader = new TestProtoReader(message);
        while (!reader.End)
        {
            var (field, wireType) = reader.ReadTag();
            if (field == fieldNumber && wireType == 5)
                return reader.ReadFixed32();
            reader.Skip(wireType);
        }
        throw new InvalidOperationException($"fixed32 field {fieldNumber} not found.");
    }
}
