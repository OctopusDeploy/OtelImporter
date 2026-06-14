using OtelImporter.Export;
using OtelImporter.Input;

namespace OtelImporter.Pipeline;

internal sealed record ImportResult(long BatchCount);

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
        CancellationToken cancellationToken = default)
    {
        await using var stream = _inputStreamFactory.Open(inputFile);

        var batchCount = 0L;
        await foreach (var line in JsonlLineReader.ReadLinesAsync(stream, cancellationToken).ConfigureAwait(false))
        {
            await _exporter.ExportAsync(line, cancellationToken).ConfigureAwait(false);
            batchCount++;
            progress?.Report(batchCount);
        }

        return new ImportResult(batchCount);
    }
}
