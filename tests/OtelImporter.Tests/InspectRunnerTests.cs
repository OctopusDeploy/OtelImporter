using System.Text;
using OtelImporter.Inspect;
using OtelImporter.Input;
using OtelImporter.Pipeline;

namespace OtelImporter.Tests;

public class InspectRunnerTests
{
    // Two batches of OTLP/JSON, one per line; 3 spans total across two distinct names.
    const string SampleJsonl =
        """{"resourceSpans":[{"scopeSpans":[{"spans":[{"name":"GET","startTimeUnixNano":"1700000001000000000"},{"name":"GET","startTimeUnixNano":"1700000005000000000"}]}]}]}""" + "\n" +
        """{"resourceSpans":[{"scopeSpans":[{"spans":[{"name":"POST","startTimeUnixNano":"1700000003000000000"}]}]}]}""" + "\n";

    [Fact]
    public async Task SummarisesAStreamOfBatches()
    {
        var runner = new InspectRunner(new StubInputStreamFactory(SampleJsonl));
        var inspector = new TraceInspector();

        var result = await runner.RunAsync("ignored", inspector);
        var summary = inspector.BuildSummary(result.BatchCount);

        Assert.Equal(2, summary.BatchCount);
        Assert.Equal(3, summary.SpanCount);
        Assert.Equal(new SpanNameCount("GET", 2), summary.TopSpanNames[0]);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(1_700_000_001), summary.OldestSpan);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(1_700_000_005), summary.NewestSpan);
        Assert.Equal(TimeSpan.FromSeconds(4), summary.Duration);
    }

    [Fact]
    public async Task ReportsProgressPerBatch()
    {
        var runner = new InspectRunner(new StubInputStreamFactory(SampleJsonl));
        var progress = new SynchronousProgress();

        await runner.RunAsync("ignored", new TraceInspector(), progress);

        Assert.Equal([1, 2], progress.Reports);
    }

    [Fact]
    public async Task IgnoresSpansOutsideTheTimeWindow()
    {
        // Sample spans: GET@1700000001, GET@1700000005 (batch 1); POST@1700000003 (batch 2).
        // from = 1700000002 drops the first GET; both batches still contribute a span.
        var filter = SpanTimeFilter.Create(DateTimeOffset.UnixEpoch.AddSeconds(1_700_000_002), null);
        var runner = new InspectRunner(new StubInputStreamFactory(SampleJsonl), filter);
        var inspector = new TraceInspector();

        var result = await runner.RunAsync("ignored", inspector);
        var summary = inspector.BuildSummary(result.BatchCount);

        Assert.Equal(2, summary.BatchCount);
        Assert.Equal(2, summary.SpanCount);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(1_700_000_003), summary.OldestSpan);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(1_700_000_005), summary.NewestSpan);
    }

    [Fact]
    public async Task DropsBatchesWithNoSpansInWindow()
    {
        // from far in the future: every span is dropped, so no batch contributes.
        var filter = SpanTimeFilter.Create(DateTimeOffset.UnixEpoch.AddSeconds(2_000_000_000), null);
        var runner = new InspectRunner(new StubInputStreamFactory(SampleJsonl), filter);
        var inspector = new TraceInspector();

        var result = await runner.RunAsync("ignored", inspector);
        var summary = inspector.BuildSummary(result.BatchCount);

        Assert.Equal(0, summary.BatchCount);
        Assert.Equal(0, summary.SpanCount);
        Assert.Null(summary.OldestSpan);
    }

    [Fact]
    public async Task SharedInspectorFoldsMultipleFilesIntoOneSummary()
    {
        var inspector = new TraceInspector();
        var runner = new InspectRunner(new StubInputStreamFactory(SampleJsonl));

        // Two "files" of the same sample (2 batches / 3 spans each), numbering chained.
        var first = await runner.RunAsync("f1", inspector);
        var second = await runner.RunAsync("f2", inspector, startingBatchCount: first.BatchCount);
        var summary = inspector.BuildSummary(second.BatchCount);

        Assert.Equal(4, second.BatchCount);
        Assert.Equal(6, summary.SpanCount);
        Assert.Equal(new SpanNameCount("GET", 4), summary.TopSpanNames[0]);
    }

    [Fact]
    public async Task CountsSplitBatchesButKeepsTheTrueSpanCount()
    {
        // One line with three padded spans; a small cap makes an export send several batches,
        // while the summary still reports the real span count.
        var pad = new string('x', 200);
        var line = $$"""{"resourceSpans":[{"scopeSpans":[{"spans":[{"name":"{{pad}}1"},{"name":"{{pad}}2"},{"name":"{{pad}}3"}]}]}]}""" + "\n";
        var inspector = new TraceInspector();
        var runner = new InspectRunner(new StubInputStreamFactory(line), maxBatchBytes: 400);

        var result = await runner.RunAsync("ignored", inspector);
        var summary = inspector.BuildSummary(result.BatchCount);

        Assert.True(result.BatchCount > 1, $"expected the batch to be counted as split, got {result.BatchCount}");
        Assert.Equal(0, result.SkippedSpanCount);
        Assert.Equal(3, summary.SpanCount); // nothing skipped here, so all three spans count
    }

    [Fact]
    public async Task SummaryExcludesSpansThatWouldBeSkipped()
    {
        // "a", an oversized span, "b" in one scope at a tiny cap: the oversized span can't fit
        // any batch, so it would be skipped on export and is left out of the summary count.
        var huge = new string('x', 500);
        var line = $$"""{"resourceSpans":[{"scopeSpans":[{"spans":[{"name":"a"},{"name":"{{huge}}"},{"name":"b"}]}]}]}""" + "\n";
        var inspector = new TraceInspector();
        var runner = new InspectRunner(new StubInputStreamFactory(line), maxBatchBytes: 300);

        var result = await runner.RunAsync("ignored", inspector);

        Assert.Equal(1, result.SkippedSpanCount);
        Assert.Equal(2, inspector.BuildSummary(result.BatchCount).SpanCount); // only a and b
    }

    sealed class SynchronousProgress : IProgress<long>
    {
        public List<long> Reports { get; } = [];
        public void Report(long value) => Reports.Add(value);
    }

    sealed class StubInputStreamFactory(string content) : IInputStreamFactory
    {
        public Stream Open(string path) => new MemoryStream(Encoding.UTF8.GetBytes(content));
    }
}
