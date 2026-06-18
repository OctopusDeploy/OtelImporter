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

        var batchCount = await runner.RunAsync("ignored", inspector);
        var summary = inspector.BuildSummary(batchCount);

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

        var batchCount = await runner.RunAsync("ignored", inspector);
        var summary = inspector.BuildSummary(batchCount);

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

        var batchCount = await runner.RunAsync("ignored", inspector);
        var summary = inspector.BuildSummary(batchCount);

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
        var firstCount = await runner.RunAsync("f1", inspector);
        var secondCount = await runner.RunAsync("f2", inspector, startingBatchCount: firstCount);
        var summary = inspector.BuildSummary(secondCount);

        Assert.Equal(4, secondCount);
        Assert.Equal(6, summary.SpanCount);
        Assert.Equal(new SpanNameCount("GET", 4), summary.TopSpanNames[0]);
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
