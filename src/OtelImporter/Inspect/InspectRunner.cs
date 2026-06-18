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

    // The caller owns the inspector and the running batch count: this reads one file
    // into the shared inspector and returns the new batch count. Chaining several files
    // through the same inspector lets the caller build a single summary (once, at the
    // end) covering the whole directory, rather than rebuilding it per file.
    public async Task<long> RunAsync(
        string inputFile,
        TraceInspector inspector,
        IProgress<long>? progress = null,
        long startingBatchCount = 0,
        CancellationToken cancellationToken = default)
    {
        await using var stream = _inputStreamFactory.Open(inputFile);

        var batchCount = startingBatchCount;
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

        return batchCount;
    }
}
