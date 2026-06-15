using System.Text.Json;
using OtelImporter.Input;
using OtelImporter.Otlp;
using OtelImporter.Pipeline;

namespace OtelImporter.Inspect;

// Drives a read-only inspection pass: open the (optionally compressed) input stream,
// read it line by line, deserialize each batch into the object model and feed it to a
// TraceInspector. Nothing is exported. Like the import path, everything is streamed so
// memory use stays flat regardless of file size. An optional time filter drops
// out-of-window spans so the summary matches what an export with the same window would.
internal sealed class InspectRunner
{
    readonly IInputStreamFactory _inputStreamFactory;
    readonly SpanTimeFilter? _filter;

    public InspectRunner(IInputStreamFactory inputStreamFactory, SpanTimeFilter? filter = null)
    {
        _inputStreamFactory = inputStreamFactory;
        _filter = filter;
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
            if (request is null)
                continue;

            if (_filter is not null)
            {
                _filter.Apply(request);
                if (!SpanTimeFilter.HasSpans(request))
                    continue; // entire batch outside the window
            }

            inspector.Add(request);
            batchCount++;
            progress?.Report(batchCount);
        }

        return inspector.BuildSummary(batchCount);
    }
}
