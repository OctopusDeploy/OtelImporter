namespace OtelImporter.Export;

// Exports a single OTLP/JSON line (one ExportTraceServiceRequest) to the upstream
// collector. Implementations decide how to translate the JSON onto the wire.
internal interface ITraceExporter : IAsyncDisposable
{
    Task ExportAsync(ReadOnlyMemory<byte> otlpJsonLine, CancellationToken cancellationToken);
}
