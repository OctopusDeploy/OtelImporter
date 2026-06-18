using System.Text.Json;
using OtelImporter.Input;
using OtelImporter.Otlp;
using OtelImporter.Pipeline;

namespace OtelImporter.Inspect;

// The running batch count after a file (so it chains across a directory) plus the number
// of spans that would be skipped on export because they exceed the max batch size. With
// no size cap, BatchCount is just the line count and SkippedSpanCount is zero.
internal sealed record InspectRunResult(long BatchCount, long SkippedSpanCount);

// Drives a read-only inspection pass: open the (optionally compressed) input stream,
// read it line by line, deserialize each batch into the object model and feed it to a
// TraceInspector. Nothing is exported. Like the import path, everything is streamed so
// memory use stays flat regardless of file size. An optional time filter drops
// out-of-window spans so the summary matches what an export with the same window would.
//
// When a max batch size is set, BatchCount reflects how many batches an export would send
// (oversized batches counted as if split); the span counts still describe the whole file,
// since splitting only regroups spans. The size is measured without enrichment, so the real
// export may split into slightly more batches once log.file.name/attributes are added.
internal sealed class InspectRunner
{
    readonly IInputStreamFactory _inputStreamFactory;
    readonly SpanTimeFilter? _filter;
    readonly long? _maxBatchBytes;

    public InspectRunner(IInputStreamFactory inputStreamFactory, SpanTimeFilter? filter = null, long? maxBatchBytes = null)
    {
        _inputStreamFactory = inputStreamFactory;
        _filter = filter;
        _maxBatchBytes = maxBatchBytes;
    }

    // The caller owns the inspector and the running batch count: this reads one file into
    // the shared inspector and returns the new running totals. Chaining several files through
    // the same inspector lets the caller build a single summary (once, at the end) covering
    // the whole directory, rather than rebuilding it per file.
    public async Task<InspectRunResult> RunAsync(
        string inputFile,
        TraceInspector inspector,
        IProgress<long>? progress = null,
        long startingBatchCount = 0,
        CancellationToken cancellationToken = default)
    {
        await using var stream = _inputStreamFactory.Open(inputFile);

        var batchCount = startingBatchCount;
        var skippedSpanCount = 0L;
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

            // The summary always describes the whole file, so every span is counted here
            // regardless of splitting.
            inspector.Add(request);

            // With a size cap, count the batches an export would actually send; otherwise
            // each line is one batch.
            if (_maxBatchBytes is { } maxBytes)
            {
                var split = BatchSplitter.Split(request, maxBytes);
                batchCount += split.Batches.Count;
                skippedSpanCount += split.SkippedSpanCount;
            }
            else
            {
                batchCount++;
            }

            progress?.Report(batchCount);
        }

        return new InspectRunResult(batchCount, skippedSpanCount);
    }
}
