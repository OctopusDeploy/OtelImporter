using OtelImporter.Otlp;

namespace OtelImporter.Export;

// Exports a single batch (one ExportTraceServiceRequest) to the upstream collector.
// The batch is handed over already deserialized -- the runner parses each input line
// once and feeds the same model to both the exporter and the inspector -- so each
// implementation just decides how to put the model onto the wire.
// A transport-level failure throws TraceExportException; a transport-level success
// that nonetheless rejected spans is reported via the returned ExportOutcome.
internal interface ITraceExporter : IAsyncDisposable
{
    Task<ExportOutcome> ExportAsync(ExportTraceServiceRequest request, CancellationToken cancellationToken);
}
