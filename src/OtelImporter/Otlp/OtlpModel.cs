using System.Text.Json.Serialization;

namespace OtelImporter.Otlp;

// Object model mirroring the OpenTelemetry trace data model (OTLP).
// Field names match the OTLP/JSON encoding so System.Text.Json can deserialize
// the input *.jsonl lines directly. The same model is walked by the protobuf
// serializer when exporting over gRPC.
//
// See: opentelemetry/proto/trace/v1/trace.proto and common/v1/common.proto.

internal sealed class ExportTraceServiceRequest
{
    [JsonPropertyName("resourceSpans")] public List<ResourceSpans>? ResourceSpans { get; set; }
}

internal sealed class ResourceSpans
{
    [JsonPropertyName("resource")] public Resource? Resource { get; set; }
    [JsonPropertyName("scopeSpans")] public List<ScopeSpans>? ScopeSpans { get; set; }
    [JsonPropertyName("schemaUrl")] public string? SchemaUrl { get; set; }
}

internal sealed class Resource
{
    [JsonPropertyName("attributes")] public List<KeyValue>? Attributes { get; set; }
    [JsonPropertyName("droppedAttributesCount")] public uint DroppedAttributesCount { get; set; }
}

internal sealed class ScopeSpans
{
    [JsonPropertyName("scope")] public InstrumentationScope? Scope { get; set; }
    [JsonPropertyName("spans")] public List<Span>? Spans { get; set; }
    [JsonPropertyName("schemaUrl")] public string? SchemaUrl { get; set; }
}

internal sealed class InstrumentationScope
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("attributes")] public List<KeyValue>? Attributes { get; set; }
    [JsonPropertyName("droppedAttributesCount")] public uint DroppedAttributesCount { get; set; }
}

internal sealed class Span
{
    [JsonPropertyName("traceId")] public string? TraceId { get; set; }
    [JsonPropertyName("spanId")] public string? SpanId { get; set; }
    [JsonPropertyName("traceState")] public string? TraceState { get; set; }
    [JsonPropertyName("parentSpanId")] public string? ParentSpanId { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("kind")] public int Kind { get; set; }
    [JsonPropertyName("startTimeUnixNano")] public ulong StartTimeUnixNano { get; set; }
    [JsonPropertyName("endTimeUnixNano")] public ulong EndTimeUnixNano { get; set; }
    [JsonPropertyName("attributes")] public List<KeyValue>? Attributes { get; set; }
    [JsonPropertyName("droppedAttributesCount")] public uint DroppedAttributesCount { get; set; }
    [JsonPropertyName("events")] public List<SpanEvent>? Events { get; set; }
    [JsonPropertyName("droppedEventsCount")] public uint DroppedEventsCount { get; set; }
    [JsonPropertyName("links")] public List<SpanLink>? Links { get; set; }
    [JsonPropertyName("droppedLinksCount")] public uint DroppedLinksCount { get; set; }
    [JsonPropertyName("status")] public Status? Status { get; set; }
    [JsonPropertyName("flags")] public uint Flags { get; set; }
}

internal sealed class SpanEvent
{
    [JsonPropertyName("timeUnixNano")] public ulong TimeUnixNano { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("attributes")] public List<KeyValue>? Attributes { get; set; }
    [JsonPropertyName("droppedAttributesCount")] public uint DroppedAttributesCount { get; set; }
}

internal sealed class SpanLink
{
    [JsonPropertyName("traceId")] public string? TraceId { get; set; }
    [JsonPropertyName("spanId")] public string? SpanId { get; set; }
    [JsonPropertyName("traceState")] public string? TraceState { get; set; }
    [JsonPropertyName("attributes")] public List<KeyValue>? Attributes { get; set; }
    [JsonPropertyName("droppedAttributesCount")] public uint DroppedAttributesCount { get; set; }
    [JsonPropertyName("flags")] public uint Flags { get; set; }
}

internal sealed class Status
{
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("code")] public int Code { get; set; }
}

internal sealed class KeyValue
{
    [JsonPropertyName("key")] public string? Key { get; set; }
    [JsonPropertyName("value")] public AnyValue? Value { get; set; }
}

internal sealed class AnyValue
{
    [JsonPropertyName("stringValue")] public string? StringValue { get; set; }
    [JsonPropertyName("boolValue")] public bool? BoolValue { get; set; }
    [JsonPropertyName("intValue")] public long? IntValue { get; set; }
    [JsonPropertyName("doubleValue")] public double? DoubleValue { get; set; }
    [JsonPropertyName("arrayValue")] public ArrayValue? ArrayValue { get; set; }
    [JsonPropertyName("kvlistValue")] public KeyValueList? KvlistValue { get; set; }
    [JsonPropertyName("bytesValue")] public string? BytesValue { get; set; } // base64-encoded
}

internal sealed class ArrayValue
{
    [JsonPropertyName("values")] public List<AnyValue>? Values { get; set; }
}

internal sealed class KeyValueList
{
    [JsonPropertyName("values")] public List<KeyValue>? Values { get; set; }
}
