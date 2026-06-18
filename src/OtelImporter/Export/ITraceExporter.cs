using OtelImporter.Otlp;

namespace OtelImporter.Export;

// A batch ready for the wire, split (if a size limit is configured) into one or more
// frames that each fit within that limit. Each frame is the protocol's serialized payload
// (OTLP/JSON for HTTP, OTLP protobuf for gRPC). SkippedSpanCount is the number of spans
// dropped because, even on their own, they exceeded the limit.
internal sealed record PreparedBatches(IReadOnlyList<ReadOnlyMemory<byte>> Frames, long SkippedSpanCount);

// Puts a parsed batch onto the wire. The model is serialized exactly once, by the
// exporter, in the transport's own format -- so size-based splitting (Prepare) measures
// real wire bytes rather than a stand-in. Splitting and sending are separated so the
// runner can rate-limit and the retry decorator can retry each frame independently:
//   * Prepare turns one logical batch into the frames that will actually be sent;
//   * SendAsync puts one frame on the wire -- throwing TraceExportException on transport
//     failure, returning partial-success details otherwise.
internal interface ITraceExporter : IAsyncDisposable
{
    PreparedBatches Prepare(ExportTraceServiceRequest request);

    Task<ExportOutcome> SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken);
}
