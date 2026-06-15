using System.Text;
using OtelImporter.Export;
using OtelImporter.Input;
using OtelImporter.Inspect;
using OtelImporter.Otlp;
using OtelImporter.Pipeline;

namespace OtelImporter.Tests;

public class ImportRunnerTests
{
    // One OTLP/JSON batch (one line) containing a single span with the given name.
    static string Batch(string spanName) =>
        $$"""{"resourceSpans":[{"scopeSpans":[{"spans":[{"name":"{{spanName}}"}]}]}]}""";

    static string Lines(params string[] batches) => string.Join('\n', batches) + '\n';

    [Fact]
    public async Task ExportsEachNonEmptyLineAndCountsBatches()
    {
        // A blank line between batches must be skipped.
        var input = new StubInputStreamFactory(Batch("a") + "\n\n" + Batch("b") + "\n" + Batch("c") + "\n");
        var exporter = new RecordingExporter();

        var result = await new ImportRunner(input, exporter).RunAsync("ignored");

        Assert.Equal(3, result.BatchCount);
        Assert.Equal(["a", "b", "c"], exporter.SpanNames);
    }

    [Fact]
    public async Task ReportsProgressPerBatch()
    {
        var input = new StubInputStreamFactory(Lines(Batch("a"), Batch("b"), Batch("c")));
        var exporter = new RecordingExporter();
        var progress = new SynchronousProgress();

        await new ImportRunner(input, exporter).RunAsync("ignored", progress);

        Assert.Equal([1, 2, 3], progress.Reports);
    }

    [Fact]
    public async Task FeedsTheInspectorWhenSupplied()
    {
        var input = new StubInputStreamFactory(Lines(Batch("a"), Batch("a"), Batch("b")));
        var inspector = new TraceInspector();

        await new ImportRunner(input, new RecordingExporter()).RunAsync("ignored", inspector: inspector);

        var summary = inspector.BuildSummary(batchCount: 3);
        Assert.Equal(3, summary.SpanCount);
        Assert.Equal(new SpanNameCount("a", 2), summary.TopSpanNames[0]);
    }

    [Fact]
    public async Task FiltersOutOfWindowSpansAndSkipsEmptyBatches()
    {
        // One span per batch at t = 10s, 20s, 30s (unix-nanos).
        static string SpanAt(ulong seconds) =>
            $$"""{"resourceSpans":[{"scopeSpans":[{"spans":[{"name":"s","startTimeUnixNano":"{{seconds * 1_000_000_000UL}}"}]}]}]}""";

        var input = new StubInputStreamFactory(Lines(SpanAt(10), SpanAt(20), SpanAt(30)));
        var exporter = new RecordingExporter();
        var inspector = new TraceInspector();
        var filter = SpanTimeFilter.Create(DateTimeOffset.UnixEpoch.AddSeconds(15), DateTimeOffset.UnixEpoch.AddSeconds(25));

        var result = await new ImportRunner(input, exporter, filter: filter).RunAsync("ignored", inspector: inspector);

        // Only the 20s batch is within [15s, 25s]; the others are dropped entirely.
        Assert.Equal(1, result.BatchCount);
        Assert.Single(exporter.SpanNames);
        Assert.Equal(1, inspector.BuildSummary(result.BatchCount).SpanCount);
    }

    [Fact]
    public async Task AggregatesRejectedSpansAndEmitsADiagnosticPerProblemBatch()
    {
        var input = new StubInputStreamFactory(Lines(Batch("one"), Batch("two"), Batch("three")));
        // Batches "one" and "three" report rejections; "two" is clean.
        var exporter = new RecordingExporter(name => name switch
        {
            "one" => new ExportOutcome(5, "schema mismatch"),
            "three" => new ExportOutcome(2, null),
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
        var input = new StubInputStreamFactory(Lines(Batch("a"), Batch("b")));
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

        public List<string> SpanNames { get; } = [];

        public Task<ExportOutcome> ExportAsync(ExportTraceServiceRequest request, CancellationToken cancellationToken)
        {
            var name = request.ResourceSpans?[0].ScopeSpans?[0].Spans?[0].Name ?? "";
            SpanNames.Add(name);
            return Task.FromResult(_outcome(name));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
