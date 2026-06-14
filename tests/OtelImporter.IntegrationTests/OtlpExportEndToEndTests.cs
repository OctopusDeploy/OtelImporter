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
    public async Task ImportsSampleFileToUpstream(string protocol, string fileName)
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

    [Theory]
    [InlineData("http")]
    [InlineData("grpc")]
    public async Task RecoversFromTransientFailuresViaRetry(string protocol)
    {
        // The server fails the first two requests (503 / gRPC UNAVAILABLE); retry-with-backoff
        // should recover so every span still lands.
        await using var server = await TestOtlpServer.StartAsync(failFirstRequests: 2);
        var endpoint = protocol == "grpc" ? server.GrpcEndpoint : server.HttpEndpoint;

        var exitCode = await Importer.RunAsync(
        [
            TestDataPath("sample-traces.jsonl"),
            "--endpoint", endpoint.ToString(),
            "--protocol", protocol,
        ]);

        Assert.Equal(ExitCode.Success, exitCode);
        Assert.Equal(ExpectedSpans, server.Received.SpanCount);
    }

    [Fact]
    public async Task ReturnsRuntimeErrorWhenRetriesAreExhausted()
    {
        // More failures than retries (default 4) => the import ultimately fails.
        await using var server = await TestOtlpServer.StartAsync(failFirstRequests: 99);

        var exitCode = await Importer.RunAsync(
        [
            TestDataPath("sample-traces.jsonl"),
            "--endpoint", server.HttpEndpoint.ToString(),
            "--protocol", "http",
            "--max-retries", "1",
        ]);

        Assert.Equal(ExitCode.RuntimeError, exitCode);
    }

    [Fact]
    public async Task ReturnsRuntimeErrorWhenUpstreamIsUnreachable()
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
    public async Task InspectSummarisesWithoutAnyEndpointConfigured()
    {
        // --inspect is read-only: it must succeed with no endpoint/env var set and
        // without anything listening upstream.
        var exitCode = await Importer.RunAsync([TestDataPath("sample-traces.jsonl"), "--inspect"]);

        Assert.Equal(ExitCode.Success, exitCode);
    }

    [Fact]
    public async Task ReturnsUsageErrorForMissingFile()
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
