using OtelImporter.Export;
using OtelImporter.Input;

namespace OtelImporter.Pipeline;

internal sealed record ImportResult(long BatchCount, long RejectedSpanCount);

// Drives the import: open the (optionally compressed) input stream, read it line by
// line, and hand each OTLP/JSON batch to the exporter. Everything is streamed, so
// memory use stays flat regardless of file size.
internal sealed class ImportRunner
{
    readonly IInputStreamFactory _inputStreamFactory;
    readonly ITraceExporter _exporter;

    public ImportRunner(IInputStreamFactory inputStreamFactory, ITraceExporter exporter)
    {
        _inputStreamFactory = inputStreamFactory;
        _exporter = exporter;
    }

    public async Task<ImportResult> RunAsync(
        string inputFile,
        IProgress<long>? progress = null,
        Action<string>? onDiagnostic = null,
        CancellationToken cancellationToken = default)
    {
        await using var stream = _inputStreamFactory.Open(inputFile);

        var batchCount = 0L;
        var rejectedSpanCount = 0L;
        await foreach (var line in JsonlLineReader.ReadLinesAsync(stream, cancellationToken).ConfigureAwait(false))
        {
            var outcome = await _exporter.ExportAsync(line, cancellationToken).ConfigureAwait(false);
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
