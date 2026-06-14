namespace OtelImporter.Export;

// Exports a single OTLP/JSON line (one ExportTraceServiceRequest) to the upstream
// collector. Implementations decide how to translate the JSON onto the wire.
// A transport-level failure throws TraceExportException; a transport-level success
// that nonetheless rejected spans is reported via the returned ExportOutcome.
internal interface ITraceExporter : IAsyncDisposable
{
    Task<ExportOutcome> ExportAsync(ReadOnlyMemory<byte> otlpJsonLine, CancellationToken cancellationToken);
}
