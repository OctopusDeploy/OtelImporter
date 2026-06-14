using System.Text.Json;
using OtelImporter.Export;
using OtelImporter.Input;
using OtelImporter.Inspect;
using OtelImporter.Otlp;

namespace OtelImporter.Pipeline;

internal sealed record ImportResult(long BatchCount, long RejectedSpanCount);

// Drives the import: open the (optionally compressed) input stream, read it line by
// line, deserialize each OTLP/JSON batch once, and hand the model to the exporter
// (and, when supplied, to an inspector for the end-of-run summary). Everything is
// streamed, so memory use stays flat regardless of file size.
internal sealed class ImportRunner
{
    readonly IInputStreamFactory _inputStreamFactory;
    readonly ITraceExporter _exporter;
    readonly IRateLimiter? _rateLimiter;

    public ImportRunner(IInputStreamFactory inputStreamFactory, ITraceExporter exporter, IRateLimiter? rateLimiter = null)
    {
        _inputStreamFactory = inputStreamFactory;
        _exporter = exporter;
        _rateLimiter = rateLimiter;
    }

    public async Task<ImportResult> RunAsync(
        string inputFile,
        IProgress<long>? progress = null,
        Action<string>? onDiagnostic = null,
        TraceInspector? inspector = null,
        CancellationToken cancellationToken = default)
    {
        await using var stream = _inputStreamFactory.Open(inputFile);

        var batchCount = 0L;
        var rejectedSpanCount = 0L;
        await foreach (var line in JsonlLineReader.ReadLinesAsync(stream, cancellationToken).ConfigureAwait(false))
        {
            var request = JsonSerializer.Deserialize(line.Span, OtlpJsonContext.Default.ExportTraceServiceRequest)
                          ?? throw new TraceExportException("Trace line deserialized to null.");

            // (trace manipulation will hook in here, operating on `request`)

            inspector?.Add(request);

            if (_rateLimiter is not null)
                await _rateLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);

            var outcome = await _exporter.ExportAsync(request, cancellationToken).ConfigureAwait(false);
            batchCount++;

            if (outcome.HasProblem)
            {
                rejectedSpanCount += outcome.RejectedSpans;
                onDiagnostic?.Invoke(FormatProblem(batchCount, outcome));
            }

            progress?.Report(batchCount);
        }

        return new ImportResult(batchCount, rejectedSpanCount);
    }

    static string FormatProblem(long batchNumber, ExportOutcome outcome)
    {
        var detail = string.IsNullOrEmpty(outcome.ErrorMessage) ? "(no message provided)" : outcome.ErrorMessage;
        return outcome.RejectedSpans > 0
            ? $"batch {batchNumber}: collector rejected {outcome.RejectedSpans} span(s): {detail}"
            : $"batch {batchNumber}: collector reported: {detail}";
    }
}
