using System.Text;
using OtelImporter.Export;
using OtelImporter.Input;
using OtelImporter.Pipeline;

namespace OtelImporter.Tests;

public class ImportRunnerTests
{
    [Fact]
    public async Task Exports_each_non_empty_line_and_counts_batches()
    {
        var input = new StubInputStreamFactory("{\"a\":1}\n\n{\"b\":2}\n{\"c\":3}\n");
        var exporter = new RecordingExporter();

        var result = await new ImportRunner(input, exporter).RunAsync("ignored");

        Assert.Equal(3, result.BatchCount);
        Assert.Equal(["{\"a\":1}", "{\"b\":2}", "{\"c\":3}"], exporter.Exported);
    }

    [Fact]
    public async Task Reports_progress_per_batch()
    {
        var input = new StubInputStreamFactory("1\n2\n3\n");
        var exporter = new RecordingExporter();
        var progress = new SynchronousProgress();

        await new ImportRunner(input, exporter).RunAsync("ignored", progress);

        Assert.Equal([1, 2, 3], progress.Reports);
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

    sealed class RecordingExporter : ITraceExporter
    {
        public List<string> Exported { get; } = [];

        public Task ExportAsync(ReadOnlyMemory<byte> otlpJsonLine, CancellationToken cancellationToken)
        {
            Exported.Add(Encoding.UTF8.GetString(otlpJsonLine.Span));
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
