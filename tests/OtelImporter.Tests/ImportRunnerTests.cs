using System.Text;
using OtelImporter.Export;
using OtelImporter.Input;
using OtelImporter.Pipeline;

namespace OtelImporter.Tests;

public class ImportRunnerTests
{
    [Fact]
    public async Task ExportsEachNonEmptyLineAndCountsBatches()
    {
        var input = new StubInputStreamFactory("{\"a\":1}\n\n{\"b\":2}\n{\"c\":3}\n");
        var exporter = new RecordingExporter();

        var result = await new ImportRunner(input, exporter).RunAsync("ignored");

        Assert.Equal(3, result.BatchCount);
        Assert.Equal(["{\"a\":1}", "{\"b\":2}", "{\"c\":3}"], exporter.Exported);
    }

    [Fact]
    public async Task ReportsProgressPerBatch()
    {
        var input = new StubInputStreamFactory("1\n2\n3\n");
        var exporter = new RecordingExporter();
        var progress = new SynchronousProgress();

        await new ImportRunner(input, exporter).RunAsync("ignored", progress);

        Assert.Equal([1, 2, 3], progress.Reports);
    }

    [Fact]
    public async Task AggregatesRejectedSpansAndEmitsADiagnosticPerProblemBatch()
    {
        var input = new StubInputStreamFactory("1\n2\n3\n");
        // Batches 1 and 3 report rejections; batch 2 is clean.
        var exporter = new RecordingExporter(line => line switch
        {
            "1" => new ExportOutcome(5, "schema mismatch"),
            "3" => new ExportOutcome(2, null),
            _ => ExportOutcome.Accepted,
        });
        var diagnostics = new List<string>();

        var result = await new ImportRunner(input, exporter).RunAsync("ignored", onDiagnostic: diagnostics.Add);

        Assert.Equal(3, result.BatchCount);
        Assert.Equal(7, result.RejectedSpanCount);
        Assert.Equal(2, diagnostics.Count);
        Assert.Contains("rejected 5 span(s): schema mismatch", diagnostics[0]);
        Assert.Contains("rejected 2 span(s)", diagnostics[1]);
    }

    [Fact]
    public async Task NoDiagnosticsWhenEverythingIsAccepted()
    {
        var input = new StubInputStreamFactory("1\n2\n");
        var exporter = new RecordingExporter();
        var diagnostics = new List<string>();

        var result = await new ImportRunner(input, exporter).RunAsync("ignored", onDiagnostic: diagnostics.Add);

        Assert.Equal(0, result.RejectedSpanCount);
        Assert.Empty(diagnostics);
    }

    // Deterministic, synchronous progress sink (Progress<T> dispatches asynchronously).
    sealed class SynchronousProgress : IProgress<long>
    {
        public List<long> Reports { get; } = [];
        public void Report(long value) => Reports.Add(value);
    }

    sealed class StubInputStreamFactory(string content) : IInputStreamFactory
    {
        public Stream Open(string path) => new MemoryStream(Encoding.UTF8.GetBytes(content));
    }

    sealed class RecordingExporter(Func<string, ExportOutcome>? outcome = null) : ITraceExporter
    {
        readonly Func<string, ExportOutcome> _outcome = outcome ?? (_ => ExportOutcome.Accepted);

        public List<string> Exported { get; } = [];

        public Task<ExportOutcome> ExportAsync(ReadOnlyMemory<byte> otlpJsonLine, CancellationToken cancellationToken)
        {
            var line = Encoding.UTF8.GetString(otlpJsonLine.Span);
            Exported.Add(line);
            return Task.FromResult(_outcome(line));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
