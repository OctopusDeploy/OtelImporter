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

    // One batch (one line) whose single scope carries a span per name, for split tests.
    static string BatchOf(params string[] spanNames) =>
        $$"""{"resourceSpans":[{"scopeSpans":[{"spans":[{{string.Join(",", spanNames.Select(n => $$"""{"name":"{{n}}"}"""))}}]}]}]}""";

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

    [Fact]
    public async Task ChainsBatchNumberingAcrossFilesViaStartingBatchCount()
    {
        // Simulates a second file in a directory: numbering continues from the first.
        var input = new StubInputStreamFactory(Lines(Batch("a"), Batch("b")));
        var progress = new SynchronousProgress();

        var result = await new ImportRunner(input, new RecordingExporter())
            .RunAsync("ignored", progress, startingBatchCount: 5);

        Assert.Equal(7, result.BatchCount);
        Assert.Equal([6, 7], progress.Reports);
    }

    [Fact]
    public async Task SharedInspectorAccumulatesAcrossRuns()
    {
        var inspector = new TraceInspector();
        var first = new StubInputStreamFactory(Lines(Batch("a"), Batch("a")));
        var second = new StubInputStreamFactory(Lines(Batch("b")));

        var r1 = await new ImportRunner(first, new RecordingExporter()).RunAsync("f1", inspector: inspector);
        var r2 = await new ImportRunner(second, new RecordingExporter())
            .RunAsync("f2", inspector: inspector, startingBatchCount: r1.BatchCount);

        Assert.Equal(3, r2.BatchCount);
        var summary = inspector.BuildSummary(r2.BatchCount);
        Assert.Equal(3, summary.SpanCount);
        Assert.Equal(new SpanNameCount("a", 2), summary.TopSpanNames[0]);
    }

    [Fact]
    public async Task SplitsAnOversizedBatchAcrossMultipleExports()
    {
        // Six padded spans (~240 bytes each once re-serialised); an 800-byte cap fits a
        // couple per batch, so the single input batch must be split into several.
        var pad = new string('x', 200);
        var input = new StubInputStreamFactory(BatchOf(pad + "a", pad + "b", pad + "c", pad + "d", pad + "e", pad + "f") + "\n");
        var exporter = new SpanCollectingExporter();

        var result = await new ImportRunner(input, exporter, maxBatchBytes: 800).RunAsync("ignored");

        Assert.True(result.BatchCount > 1, $"expected a split, got {result.BatchCount} batch(es)");
        Assert.Equal(0, result.SkippedSpanCount);
        // Every span survives the split, in order, across the multiple exports.
        Assert.Equal([pad + "a", pad + "b", pad + "c", pad + "d", pad + "e", pad + "f"], exporter.AllSpanNames);
    }

    [Fact]
    public async Task SkipsSpansLargerThanTheMaxBatchSizeButSendsTheRest()
    {
        // The middle span alone exceeds the cap; the small ones still go.
        var huge = new string('x', 500);
        var input = new StubInputStreamFactory(BatchOf("a", huge, "b") + "\n");
        var exporter = new SpanCollectingExporter();

        var result = await new ImportRunner(input, exporter, maxBatchBytes: 300).RunAsync("ignored");

        Assert.Equal(1, result.SkippedSpanCount);
        Assert.Equal(["a", "b"], exporter.AllSpanNames);
    }

    [Fact]
    public async Task SummaryExcludesSkippedSpans()
    {
        // "a", an oversized span, then "b" in one scope; the oversized span is skipped, so the
        // inspector (and thus the end-of-run summary) should count only the two that were sent.
        var huge = new string('x', 500);
        var input = new StubInputStreamFactory(BatchOf("a", huge, "b") + "\n");
        var inspector = new TraceInspector();

        var result = await new ImportRunner(input, new SpanCollectingExporter(), maxBatchBytes: 300)
            .RunAsync("ignored", inspector: inspector);

        Assert.Equal(1, result.SkippedSpanCount);
        Assert.Equal(2, inspector.BuildSummary(result.BatchCount).SpanCount);
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

    // Records the names of every span across every exported batch, so split output can be
    // checked end to end (RecordingExporter only keeps each batch's first span).
    sealed class SpanCollectingExporter : ITraceExporter
    {
        public List<string> AllSpanNames { get; } = [];

        public Task<ExportOutcome> ExportAsync(ExportTraceServiceRequest request, CancellationToken cancellationToken)
        {
            foreach (var rs in request.ResourceSpans ?? [])
                foreach (var ss in rs.ScopeSpans ?? [])
                    foreach (var s in ss.Spans ?? [])
                        AllSpanNames.Add(s.Name ?? "");
            return Task.FromResult(ExportOutcome.Accepted);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
