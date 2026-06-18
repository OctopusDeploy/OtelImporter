using System.Buffers;
using System.Text.Json;
using OtelImporter.Otlp;

namespace OtelImporter.Export;

// Assembles OTLP/JSON frames for the HTTP path. Each span/resource/scope is serialized once
// and spliced in verbatim via Utf8JsonWriter.WriteRawValue, so the frame matches exactly what
// a single serialization of the same spans would produce.
internal sealed class JsonBatchBuilder : WireBatchBuilder
{
    readonly ArrayBufferWriter<byte> _buffer = new();

    public override byte[] SerializeSpan(Span span) =>
        JsonSerializer.SerializeToUtf8Bytes(span, OtlpJsonContext.Default.Span);

    protected override ReadOnlyMemory<byte> Assemble()
    {
        _buffer.Clear();
        using (var writer = new Utf8JsonWriter(_buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("resourceSpans");
            writer.WriteStartArray();

            foreach (var resourceGroup in Groups)
            {
                writer.WriteStartObject();
                if (resourceGroup.Resource.Resource is { } resource)
                {
                    writer.WritePropertyName("resource");
                    writer.WriteRawValue(JsonSerializer.SerializeToUtf8Bytes(resource, OtlpJsonContext.Default.Resource), skipInputValidation: true);
                }
                if (!string.IsNullOrEmpty(resourceGroup.Resource.SchemaUrl))
                    writer.WriteString("schemaUrl", resourceGroup.Resource.SchemaUrl);

                writer.WritePropertyName("scopeSpans");
                writer.WriteStartArray();
                foreach (var scopeGroup in resourceGroup.Scopes)
                {
                    writer.WriteStartObject();
                    if (scopeGroup.Scope.Scope is { } scope)
                    {
                        writer.WritePropertyName("scope");
                        writer.WriteRawValue(JsonSerializer.SerializeToUtf8Bytes(scope, OtlpJsonContext.Default.InstrumentationScope), skipInputValidation: true);
                    }
                    if (!string.IsNullOrEmpty(scopeGroup.Scope.SchemaUrl))
                        writer.WriteString("schemaUrl", scopeGroup.Scope.SchemaUrl);

                    writer.WritePropertyName("spans");
                    writer.WriteStartArray();
                    foreach (var spanWire in scopeGroup.Spans)
                        writer.WriteRawValue(spanWire, skipInputValidation: true);
                    writer.WriteEndArray();

                    writer.WriteEndObject();
                }
                writer.WriteEndArray();

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return _buffer.WrittenMemory.ToArray();
    }
}
