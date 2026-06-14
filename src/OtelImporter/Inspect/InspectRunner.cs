using System.Text.Json;
using OtelImporter.Input;
using OtelImporter.Otlp;

namespace OtelImporter.Inspect;

// Drives a read-only inspection pass: open the (optionally compressed) input stream,
// read it line by line, deserialize each batch into the object model and feed it to a
// TraceInspector. Nothing is exported. Like the import path, everything is streamed so
// memory use stays flat regardless of file size.
internal sealed class InspectRunner
{
    readonly IInputStreamFactory _inputStreamFactory;

    public InspectRunner(IInputStreamFactory inputStreamFactory)
    {
        _inputStreamFactory = inputStreamFactory;
    }

    public async Task<InspectionSummary> RunAsync(
        string inputFile,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await using var stream = _inputStreamFactory.Open(inputFile);

        var inspector = new TraceInspector();
        var batchCount = 0L;
        await foreach (var line in JsonlLineReader.ReadLinesAsync(stream, cancellationToken).ConfigureAwait(false))
        {
            var request = JsonSerializer.Deserialize(line.Span, OtlpJsonContext.Default.ExportTraceServiceRequest);
            if (request is not null)
                inspector.Add(request);

            batchCount++;
            progress?.Report(batchCount);
        }

        return inspector.BuildSummary(batchCount);
    }
}
