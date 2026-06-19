using OtelImporter.Otlp;

namespace OtelImporter.Export;

// Assembles OTLP protobuf frames for the gRPC path. Spans are pre-serialized once and
// spliced into freshly framed ScopeSpans/ResourceSpans; the resource/scope headers are
// small and re-encoded per frame. Field numbers:
//   ExportTraceServiceRequest.resource_spans = 1
//   ResourceSpans.scope_spans = 2   ScopeSpans.spans = 2
internal sealed class ProtobufBatchBuilder : WireBatchBuilder
{
    const int ResourceSpansField = 1;
    const int ScopeSpansField = 2;
    const int SpansField = 2;

    public override byte[] SerializeSpan(Span span) => OtlpProtobufSerializer.SerializeSpan(span);

    protected override ReadOnlyMemory<byte> Assemble()
    {
        using var request = new ProtoWriter();

        foreach (var resourceGroup in Groups)
        {
            using var resource = new ProtoWriter();
            resource.WriteRawBytes(OtlpProtobufSerializer.SerializeResourceSpansHeader(resourceGroup.Resource));

            foreach (var scopeGroup in resourceGroup.Scopes)
            {
                using var scope = new ProtoWriter();
                scope.WriteRawBytes(OtlpProtobufSerializer.SerializeScopeSpansHeader(scopeGroup.Scope));
                foreach (var spanWire in scopeGroup.Spans)
                    scope.WriteLengthDelimited(SpansField, spanWire);

                resource.WriteLengthDelimited(ScopeSpansField, scope.WrittenSpan);
            }

            request.WriteLengthDelimited(ResourceSpansField, resource.WrittenSpan);
        }

        return request.WrittenSpan.ToArray();
    }
}
