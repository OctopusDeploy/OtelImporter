namespace OtelImporter.IntegrationTests;

// End-to-end: the importer reads a real OTLP trace file (plain and zstd-compressed)
// and ships it to a real ASP.NET Core OTLP receiver over both HTTP and gRPC. The
// whole pipeline runs -- CLI parsing, streaming read/decompress, OTLP/JSON forward
// or hand-rolled protobuf encoding, and the wire protocol itself.
public class OtlpExportEndToEndTests
{
    // The committed sample (TestData/sample-traces.jsonl) contains 4 batches / 139 spans.
    const int ExpectedBatches = 4;
    const int ExpectedSpans = 139;

    static string TestDataPath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "TestData", fileName);

    [Theory]
    [InlineData("http", "sample-traces.jsonl")]
    [InlineData("http", "sample-traces.jsonl.zst")]
    [InlineData("grpc", "sample-traces.jsonl")]
    [InlineData("grpc", "sample-traces.jsonl.zst")]
    public async Task Imports_sample_file_to_upstream(string protocol, string fileName)
    {
        await using var server = await TestOtlpServer.StartAsync();
        var endpoint = protocol == "grpc" ? server.GrpcEndpoint : server.HttpEndpoint;

        var exitCode = await Importer.RunAsync(
        [
            TestDataPath(fileName),
            "--endpoint", endpoint.ToString(),
            "--protocol", protocol,
        ]);

        Assert.Equal(ExitCode.Success, exitCode);
        Assert.Equal(ExpectedBatches, server.Received.RequestCount);
        Assert.Equal(ExpectedSpans, server.Received.SpanCount);
    }

    [Fact]
    public async Task Returns_runtime_error_when_upstream_is_unreachable()
    {
        // No server listening on this port => the export should fail, not crash.
        var exitCode = await Importer.RunAsync(
        [
            TestDataPath("sample-traces.jsonl"),
            "--endpoint", "http://127.0.0.1:1",
            "--protocol", "http",
        ]);

        Assert.Equal(ExitCode.RuntimeError, exitCode);
    }

    [Fact]
    public async Task Returns_usage_error_for_missing_file()
    {
        var exitCode = await Importer.RunAsync(
        [
            TestDataPath("does-not-exist.jsonl"),
            "--endpoint", "http://127.0.0.1:4318",
            "--protocol", "http",
        ]);

        Assert.Equal(ExitCode.UsageError, exitCode);
    }
}
