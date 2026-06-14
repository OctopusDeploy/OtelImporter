using System.Text.Json;
using System.Text.Json.Serialization;

namespace OtelImporter.Otlp;

// Source-generated (reflection-free) JSON metadata so serialization works under
// Native AOT. AllowReadingFromString lets us accept the OTLP/JSON convention where
// 64-bit values (e.g. *UnixNano timestamps) are encoded as strings, while still
// accepting plain JSON numbers. WhenWritingNull keeps the re-serialized output clean
// (absent optional fields are omitted rather than emitted as explicit nulls).
[JsonSourceGenerationOptions(
    NumberHandling = JsonNumberHandling.AllowReadingFromString,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ExportTraceServiceRequest))]
[JsonSerializable(typeof(ExportTraceServiceResponse))]
internal partial class OtlpJsonContext : JsonSerializerContext
{
}
