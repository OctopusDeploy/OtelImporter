using System.Text.Json;
using System.Text.Json.Serialization;

namespace OtelImporter.Otlp;

// Source-generated (reflection-free) JSON metadata so deserialization works under
// Native AOT. AllowReadingFromString lets us accept the OTLP/JSON convention where
// 64-bit values (e.g. *UnixNano timestamps) are encoded as strings, while still
// accepting plain JSON numbers.
[JsonSourceGenerationOptions(NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(ExportTraceServiceRequest))]
[JsonSerializable(typeof(ExportTraceServiceResponse))]
internal partial class OtlpJsonContext : JsonSerializerContext
{
}
