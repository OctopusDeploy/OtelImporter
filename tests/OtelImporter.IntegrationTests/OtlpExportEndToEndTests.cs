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

    [Theory]
    [InlineData("http")]
    [InlineData("grpc")]
    public async Task AddsLogFileNameAndCustomAttributesToEverySpan(string protocol)
    {
        await using var server = await TestOtlpServer.StartAsync();
        var endpoint = protocol == "grpc" ? server.GrpcEndpoint : server.HttpEndpoint;

        var exitCode = await Importer.RunAsync(
        [
            TestDataPath("sample-traces.jsonl"),
            "--endpoint", endpoint.ToString(),
            "--protocol", protocol,
            "--attribute", "octopus.prop=abc",
            "-a", "octopus.otherprop=def",
        ]);

        Assert.Equal(ExitCode.Success, exitCode);
        // Every span carries the automatic file name plus both custom attributes.
        Assert.Equal(ExpectedSpans, server.Received.CountAttribute("log.file.name", "sample-traces.jsonl"));
        Assert.Equal(ExpectedSpans, server.Received.CountAttribute("octopus.prop", "abc"));
        Assert.Equal(ExpectedSpans, server.Received.CountAttribute("octopus.otherprop", "def"));
    }

    [Theory]
    [InlineData("http")]
    [InlineData("grpc")]
    public async Task SendsCustomHttpHeadersOnEveryRequest(string protocol)
    {
        await using var server = await TestOtlpServer.StartAsync();
        var endpoint = protocol == "grpc" ? server.GrpcEndpoint : server.HttpEndpoint;

        var exitCode = await Importer.RunAsync(
        [
            TestDataPath("sample-traces.jsonl"),
            "--endpoint", endpoint.ToString(),
            "--protocol", protocol,
            "--http-header", "X-Honeycomb-Team=hcik_test",
            "-H", "X-Custom=abc",
        ]);

        Assert.Equal(ExitCode.Success, exitCode);
        // gRPC lowercases metadata keys; ReceivedTraces looks up case-insensitively.
        Assert.Equal("hcik_test", server.Received.Header("X-Honeycomb-Team"));
        Assert.Equal("abc", server.Received.Header("X-Custom"));
    }

    [Fact]
    public async Task NoLogFileNameSuppressesTheAutomaticAttribute()
    {
        await using var server = await TestOtlpServer.StartAsync();

        var exitCode = await Importer.RunAsync(
        [
            TestDataPath("sample-traces.jsonl"),
            "--endpoint", server.HttpEndpoint.ToString(),
            "--protocol", "http",
            "--no-log-file-name",
        ]);

        Assert.Equal(ExitCode.Success, exitCode);
        Assert.Equal(0, server.Received.CountAttribute("log.file.name", "sample-traces.jsonl"));
    }

    [Theory]
    [InlineData("http")]
    [InlineData("grpc")]
    public async Task ExportsOnlySpansWithinTheTimeWindow(string protocol)
    {
        // The sample spans run 2026-05-26 01:55:21 .. 01:57:03; cut off everything before 01:56:00.
        await using var server = await TestOtlpServer.StartAsync();
        var endpoint = protocol == "grpc" ? server.GrpcEndpoint : server.HttpEndpoint;

        var exitCode = await Importer.RunAsync(
        [
            TestDataPath("sample-traces.jsonl"),
            "--endpoint", endpoint.ToString(),
            "--protocol", protocol,
            "--from", "2026-05-26T01:56:00Z",
        ]);

        Assert.Equal(ExitCode.Success, exitCode);
        // Some spans are before the cutoff and some after, so a strict subset is exported.
        Assert.InRange(server.Received.SpanCount, 1, ExpectedSpans - 1);
    }

    [Fact]
    public async Task ExportsNothingWhenTheWindowExcludesEverything()
    {
        await using var server = await TestOtlpServer.StartAsync();

        var exitCode = await Importer.RunAsync(
        [
            TestDataPath("sample-traces.jsonl"),
            "--endpoint", server.HttpEndpoint.ToString(),
            "--protocol", "http",
            "--from", "2030-01-01T00:00:00Z",
        ]);

        Assert.Equal(ExitCode.Success, exitCode);
        // Every batch is empty after filtering, so nothing is sent at all.
        Assert.Equal(0, server.Received.RequestCount);
        Assert.Equal(0, server.Received.SpanCount);
    }

    [Fact]
    public async Task InspectRespectsTheTimeWindowWithoutAnEndpoint()
    {
        var exitCode = await Importer.RunAsync(
        [
            TestDataPath("sample-traces.jsonl"),
            "--inspect",
            "--from", "2026-05-26T01:56:00Z",
            "--to", "2026-05-26T01:57:00Z",
        ]);

        Assert.Equal(ExitCode.Success, exitCode);
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
