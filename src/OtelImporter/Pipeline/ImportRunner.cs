using System.Text.Json;
using OtelImporter.Export;
using OtelImporter.Input;
using OtelImporter.Inspect;
using OtelImporter.Otlp;

namespace OtelImporter.Pipeline;

internal sealed record ImportResult(long BatchCount, long RejectedSpanCount, long SkippedSpanCount);

// Drives the import: open the (optionally compressed) input stream, read it line by
// line, deserialize each OTLP/JSON batch once, and hand the model to the exporter
// (and, when supplied, to an inspector for the end-of-run summary). Everything is
// streamed, so memory use stays flat regardless of file size.
internal sealed class ImportRunner
{
    readonly IInputStreamFactory _inputStreamFactory;
    readonly ITraceExporter _exporter;
    readonly IRateLimiter? _rateLimiter;
    readonly SpanEnricher? _enricher;
    readonly SpanTimeFilter? _filter;
    readonly long? _maxBatchBytes;

    public ImportRunner(
        IInputStreamFactory inputStreamFactory,
        ITraceExporter exporter,
        IRateLimiter? rateLimiter = null,
        SpanEnricher? enricher = null,
        SpanTimeFilter? filter = null,
        long? maxBatchBytes = null)
    {
        _inputStreamFactory = inputStreamFactory;
        _exporter = exporter;
        _rateLimiter = rateLimiter;
        _enricher = enricher;
        _filter = filter;
        _maxBatchBytes = maxBatchBytes;
    }

    // startingBatchCount lets a caller chain several files through one exporter: the
    // returned BatchCount, progress reports and "batch N" diagnostics all continue from
    // it, so numbering runs unbroken across an entire directory. RejectedSpanCount is
    // counted for this file only -- the caller sums it across files.
    public async Task<ImportResult> RunAsync(
        string inputFile,
        IProgress<long>? progress = null,
        Action<string>? onDiagnostic = null,
        TraceInspector? inspector = null,
        CancellationToken cancellationToken = default,
        long startingBatchCount = 0)
    {
        await using var stream = _inputStreamFactory.Open(inputFile);

        var batchCount = startingBatchCount;
        var rejectedSpanCount = 0L;
        var skippedSpanCount = 0L;

        // Sends one batch and folds its outcome into the running totals/progress; shared by
        // the plain path and the split path so numbering and diagnostics behave identically.
        async Task ExportOneAsync(ExportTraceServiceRequest batch)
        {
            if (_rateLimiter is not null)
                await _rateLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);

            var outcome = await _exporter.ExportAsync(batch, cancellationToken).ConfigureAwait(false);
            batchCount++;

            if (outcome.HasProblem)
            {
                rejectedSpanCount += outcome.RejectedSpans;
                onDiagnostic?.Invoke(FormatProblem(batchCount, outcome));
            }

            progress?.Report(batchCount);
        }

        await foreach (var line in JsonlLineReader.ReadLinesAsync(stream, cancellationToken).ConfigureAwait(false))
        {
            var request = JsonSerializer.Deserialize(line.Span, OtlpJsonContext.Default.ExportTraceServiceRequest)
                          ?? throw new TraceExportException("Trace line deserialized to null.");

            // Drop out-of-window spans first; if the whole batch is gone there's nothing
            // to summarise or send, so skip it (no empty request, no rate-limit cost).
            if (_filter is not null)
            {
                _filter.Apply(request);
                if (!SpanTimeFilter.HasSpans(request))
                    continue;
            }

            // Enrichment is export-only; it only adds attributes, so it doesn't affect what
            // the inspector measures (span counts, names, timestamps).
            _enricher?.Enrich(request);

            // With a size cap, an oversized batch is broken into several that each fit;
            // otherwise the whole batch goes as one. Splitting runs after enrichment so the
            // measured size matches what is actually sent. The inspector is fed the batches we
            // actually send, so the summary excludes any spans skipped for being too large.
            if (_maxBatchBytes is { } maxBytes)
            {
                var split = BatchSplitter.Split(request, maxBytes);
                skippedSpanCount += split.SkippedSpanCount;
                foreach (var batch in split.Batches)
                {
                    inspector?.Add(batch);
                    await ExportOneAsync(batch).ConfigureAwait(false);
                }
            }
            else
            {
                inspector?.Add(request);
                await ExportOneAsync(request).ConfigureAwait(false);
            }
        }

        return new ImportResult(batchCount, rejectedSpanCount, skippedSpanCount);
    }

    static string FormatProblem(long batchNumber, ExportOutcome outcome)
    {
        var detail = string.IsNullOrEmpty(outcome.ErrorMessage) ? "(no message provided)" : outcome.ErrorMessage;
        return outcome.RejectedSpans > 0
            ? $"batch {batchNumber}: collector rejected {outcome.RejectedSpans} span(s): {detail}"
            : $"batch {batchNumber}: collector reported: {detail}";
    }
}
