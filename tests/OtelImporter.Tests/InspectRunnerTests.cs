using System.Text;
using OtelImporter.Inspect;
using OtelImporter.Input;

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

        var summary = await runner.RunAsync("ignored");

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

        await runner.RunAsync("ignored", progress);

        Assert.Equal([1, 2], progress.Reports);
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
