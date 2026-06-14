namespace OtelImporter.Otlp;

// Serializes an ExportTraceServiceRequest into the OTLP protobuf wire format used
// by the gRPC TraceService/Export call. Field numbers and wire types come straight
// from the .proto definitions:
//   collector/trace/v1/trace_service.proto, trace/v1/trace.proto,
//   resource/v1/resource.proto, common/v1/common.proto
//
// Nested messages are length-delimited, so each is serialized into a temporary
// (pooled) ProtoWriter and then embedded into its parent.
internal static class OtlpProtobufSerializer
{
    public static byte[] Serialize(ExportTraceServiceRequest request)
    {
        using var writer = new ProtoWriter();
        WriteExportRequest(writer, request);
        return writer.WrittenSpan.ToArray();
    }

    // ExportTraceServiceRequest { repeated ResourceSpans resource_spans = 1; }
    public static void WriteExportRequest(ProtoWriter writer, ExportTraceServiceRequest request)
    {
        if (request.ResourceSpans is null)
            return;

        foreach (var resourceSpans in request.ResourceSpans)
        {
            using var nested = new ProtoWriter();
            WriteResourceSpans(nested, resourceSpans);
            writer.WriteLengthDelimited(1, nested.WrittenSpan);
        }
    }

    // ResourceSpans { Resource resource = 1; repeated ScopeSpans scope_spans = 2; string schema_url = 3; }
    static void WriteResourceSpans(ProtoWriter writer, ResourceSpans resourceSpans)
    {
        if (resourceSpans.Resource is { } resource)
        {
            using var nested = new ProtoWriter();
            WriteResource(nested, resource);
            writer.WriteLengthDelimited(1, nested.WrittenSpan);
        }

        if (resourceSpans.ScopeSpans is { } scopeSpansList)
        {
            foreach (var scopeSpans in scopeSpansList)
            {
                using var nested = new ProtoWriter();
                WriteScopeSpans(nested, scopeSpans);
                writer.WriteLengthDelimited(2, nested.WrittenSpan);
            }
        }

        writer.WriteString(3, resourceSpans.SchemaUrl);
    }

    // Resource { repeated KeyValue attributes = 1; uint32 dropped_attributes_count = 2; }
    static void WriteResource(ProtoWriter writer, Resource resource)
    {
        WriteAttributes(writer, 1, resource.Attributes);
        writer.WriteUInt32(2, resource.DroppedAttributesCount);
    }

    // ScopeSpans { InstrumentationScope scope = 1; repeated Span spans = 2; string schema_url = 3; }
    static void WriteScopeSpans(ProtoWriter writer, ScopeSpans scopeSpans)
    {
        if (scopeSpans.Scope is { } scope)
        {
            using var nested = new ProtoWriter();
            WriteInstrumentationScope(nested, scope);
            writer.WriteLengthDelimited(1, nested.WrittenSpan);
        }

        if (scopeSpans.Spans is { } spans)
        {
            foreach (var span in spans)
            {
                using var nested = new ProtoWriter();
                WriteSpan(nested, span);
                writer.WriteLengthDelimited(2, nested.WrittenSpan);
            }
        }

        writer.WriteString(3, scopeSpans.SchemaUrl);
    }

    // InstrumentationScope { string name = 1; string version = 2; repeated KeyValue attributes = 3; uint32 dropped_attributes_count = 4; }
    static void WriteInstrumentationScope(ProtoWriter writer, InstrumentationScope scope)
    {
        writer.WriteString(1, scope.Name);
        writer.WriteString(2, scope.Version);
        WriteAttributes(writer, 3, scope.Attributes);
        writer.WriteUInt32(4, scope.DroppedAttributesCount);
    }

    // Span { bytes trace_id = 1; bytes span_id = 2; string trace_state = 3; bytes parent_span_id = 4;
    //        string name = 5; SpanKind kind = 6; fixed64 start_time_unix_nano = 7; fixed64 end_time_unix_nano = 8;
    //        repeated KeyValue attributes = 9; uint32 dropped_attributes_count = 10;
    //        repeated Event events = 11; uint32 dropped_events_count = 12;
    //        repeated Link links = 13; uint32 dropped_links_count = 14;
    //        Status status = 15; fixed32 flags = 16; }
    static void WriteSpan(ProtoWriter writer, Span span)
    {
        WriteHexBytes(writer, 1, span.TraceId);
        WriteHexBytes(writer, 2, span.SpanId);
        writer.WriteString(3, span.TraceState);
        WriteHexBytes(writer, 4, span.ParentSpanId);
        writer.WriteString(5, span.Name);
        writer.WriteEnum(6, span.Kind);
        // start/end times are written unconditionally (via WriteFixed64 they are still
        // omitted only when genuinely zero); they are semantically required.
        writer.WriteFixed64(7, span.StartTimeUnixNano);
        writer.WriteFixed64(8, span.EndTimeUnixNano);
        WriteAttributes(writer, 9, span.Attributes);
        writer.WriteUInt32(10, span.DroppedAttributesCount);

        if (span.Events is { } events)
        {
            foreach (var spanEvent in events)
            {
                using var nested = new ProtoWriter();
                WriteEvent(nested, spanEvent);
                writer.WriteLengthDelimited(11, nested.WrittenSpan);
            }
        }

        writer.WriteUInt32(12, span.DroppedEventsCount);

        if (span.Links is { } links)
        {
            foreach (var link in links)
            {
                using var nested = new ProtoWriter();
                WriteLink(nested, link);
                writer.WriteLengthDelimited(13, nested.WrittenSpan);
            }
        }

        writer.WriteUInt32(14, span.DroppedLinksCount);

        if (span.Status is { } status)
        {
            using var nested = new ProtoWriter();
            WriteStatus(nested, status);
            writer.WriteLengthDelimited(15, nested.WrittenSpan);
        }

        writer.WriteFixed32(16, span.Flags);
    }

    // Span.Event { fixed64 time_unix_nano = 1; string name = 2; repeated KeyValue attributes = 3; uint32 dropped_attributes_count = 4; }
    static void WriteEvent(ProtoWriter writer, SpanEvent spanEvent)
    {
        writer.WriteFixed64(1, spanEvent.TimeUnixNano);
        writer.WriteString(2, spanEvent.Name);
        WriteAttributes(writer, 3, spanEvent.Attributes);
        writer.WriteUInt32(4, spanEvent.DroppedAttributesCount);
    }

    // Span.Link { bytes trace_id = 1; bytes span_id = 2; string trace_state = 3;
    //             repeated KeyValue attributes = 4; uint32 dropped_attributes_count = 5; fixed32 flags = 6; }
    static void WriteLink(ProtoWriter writer, SpanLink link)
    {
        WriteHexBytes(writer, 1, link.TraceId);
        WriteHexBytes(writer, 2, link.SpanId);
        writer.WriteString(3, link.TraceState);
        WriteAttributes(writer, 4, link.Attributes);
        writer.WriteUInt32(5, link.DroppedAttributesCount);
        writer.WriteFixed32(6, link.Flags);
    }

    // Status { string message = 2; StatusCode code = 3; }
    static void WriteStatus(ProtoWriter writer, Status status)
    {
        writer.WriteString(2, status.Message);
        writer.WriteEnum(3, status.Code);
    }

    static void WriteAttributes(ProtoWriter writer, int fieldNumber, List<KeyValue>? attributes)
    {
        if (attributes is null)
            return;

        foreach (var attribute in attributes)
        {
            using var nested = new ProtoWriter();
            WriteKeyValue(nested, attribute);
            writer.WriteLengthDelimited(fieldNumber, nested.WrittenSpan);
        }
    }

    // KeyValue { string key = 1; AnyValue value = 2; }
    static void WriteKeyValue(ProtoWriter writer, KeyValue keyValue)
    {
        writer.WriteString(1, keyValue.Key);
        if (keyValue.Value is { } value)
        {
            using var nested = new ProtoWriter();
            WriteAnyValue(nested, value);
            writer.WriteLengthDelimited(2, nested.WrittenSpan);
        }
    }

    // AnyValue is a oneof: string_value=1, bool_value=2, int_value=3, double_value=4,
    //                      array_value=5, kvlist_value=6, bytes_value=7
    static void WriteAnyValue(ProtoWriter writer, AnyValue value)
    {
        if (value.StringValue is not null)
        {
            // Written even when empty so that an explicitly-present empty string round-trips.
            writer.WriteRawByte((1 << 3) | 2);
            var byteCount = System.Text.Encoding.UTF8.GetByteCount(value.StringValue);
            writer.WriteVarint((ulong)byteCount);
            Span<byte> scratch = byteCount <= 256 ? stackalloc byte[byteCount] : new byte[byteCount];
            System.Text.Encoding.UTF8.GetBytes(value.StringValue, scratch);
            writer.WriteRawBytes(scratch);
        }
        else if (value.BoolValue is { } boolValue)
        {
            writer.WriteRawByte((2 << 3) | 0);
            writer.WriteVarint(boolValue ? 1UL : 0UL);
        }
        else if (value.IntValue is { } intValue)
        {
            writer.WriteRawByte((3 << 3) | 0);
            writer.WriteVarint((ulong)intValue);
        }
        else if (value.DoubleValue is { } doubleValue)
        {
            writer.WriteDouble(4, doubleValue);
        }
        else if (value.ArrayValue is { } arrayValue)
        {
            using var nested = new ProtoWriter();
            if (arrayValue.Values is { } values)
            {
                foreach (var item in values)
                {
                    using var element = new ProtoWriter();
                    WriteAnyValue(element, item);
                    nested.WriteLengthDelimited(1, element.WrittenSpan);
                }
            }
            writer.WriteLengthDelimited(5, nested.WrittenSpan);
        }
        else if (value.KvlistValue is { } kvlistValue)
        {
            using var nested = new ProtoWriter();
            if (kvlistValue.Values is { } values)
            {
                foreach (var item in values)
                {
                    using var element = new ProtoWriter();
                    WriteKeyValue(element, item);
                    nested.WriteLengthDelimited(1, element.WrittenSpan);
                }
            }
            writer.WriteLengthDelimited(6, nested.WrittenSpan);
        }
        else if (value.BytesValue is not null)
        {
            writer.WriteBytes(7, Convert.FromBase64String(value.BytesValue));
        }
    }

    // OTLP/JSON encodes trace/span ids as lower-case hex strings; on the wire they are raw bytes.
    static void WriteHexBytes(ProtoWriter writer, int fieldNumber, string? hex)
    {
        if (string.IsNullOrEmpty(hex))
            return;

        var byteLength = hex.Length / 2;
        Span<byte> bytes = byteLength <= 64 ? stackalloc byte[byteLength] : new byte[byteLength];
        if (!TryParseHex(hex, bytes))
            throw new FormatException($"'{hex}' is not a valid hex-encoded identifier.");

        writer.WriteBytes(fieldNumber, bytes);
    }

    static bool TryParseHex(ReadOnlySpan<char> hex, Span<byte> destination)
    {
        if ((hex.Length & 1) != 0)
            return false;

        for (var i = 0; i < destination.Length; i++)
        {
            var hi = FromHexDigit(hex[i * 2]);
            var lo = FromHexDigit(hex[i * 2 + 1]);
            if (hi < 0 || lo < 0)
                return false;

            destination[i] = (byte)((hi << 4) | lo);
        }

        return true;
    }

    static int FromHexDigit(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };
}
