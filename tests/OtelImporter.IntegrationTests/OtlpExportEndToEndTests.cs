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

    [Theory]
    [InlineData("http")]
    [InlineData("grpc")]
    public async Task ImportsEveryFileInADirectoryWithPerFileLogName(string protocol)
    {
        await using var server = await TestOtlpServer.StartAsync();
        var endpoint = protocol == "grpc" ? server.GrpcEndpoint : server.HttpEndpoint;

        var dir = Directory.CreateTempSubdirectory("otelimporter-e2e").FullName;
        try
        {
            // Two copies of the sample under different names: one combined import.
            File.Copy(TestDataPath("sample-traces.jsonl"), Path.Combine(dir, "a.jsonl"));
            File.Copy(TestDataPath("sample-traces.jsonl"), Path.Combine(dir, "b.jsonl"));

            var exitCode = await Importer.RunAsync(
            [
                dir,
                "--endpoint", endpoint.ToString(),
                "--protocol", protocol,
            ]);

            Assert.Equal(ExitCode.Success, exitCode);
            Assert.Equal(2 * ExpectedBatches, server.Received.RequestCount);
            Assert.Equal(2 * ExpectedSpans, server.Received.SpanCount);
            // Each file's spans carry that file's own name.
            Assert.Equal(ExpectedSpans, server.Received.CountAttribute("log.file.name", "a.jsonl"));
            Assert.Equal(ExpectedSpans, server.Received.CountAttribute("log.file.name", "b.jsonl"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ContinuesPastAFailingFileAndReportsPartialSuccess()
    {
        await using var server = await TestOtlpServer.StartAsync();

        var dir = Directory.CreateTempSubdirectory("otelimporter-e2e").FullName;
        try
        {
            // "bad.jsonl" sorts first and is unparseable; "good.jsonl" must still export in full.
            File.WriteAllText(Path.Combine(dir, "bad.jsonl"), "this is not valid otlp json\n");
            File.Copy(TestDataPath("sample-traces.jsonl"), Path.Combine(dir, "good.jsonl"));

            var exitCode = await Importer.RunAsync(
            [
                dir,
                "--endpoint", server.HttpEndpoint.ToString(),
                "--protocol", "http",
            ]);

            // One of two files failed -> partial success, and the good file landed in full.
            Assert.Equal(ExitCode.PartialSuccess, exitCode);
            Assert.Equal(ExpectedSpans, server.Received.SpanCount);
            Assert.Equal(ExpectedSpans, server.Received.CountAttribute("log.file.name", "good.jsonl"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task AbortsAfterThreeConsecutiveFailures()
    {
        await using var server = await TestOtlpServer.StartAsync();

        var dir = Directory.CreateTempSubdirectory("otelimporter-e2e").FullName;
        try
        {
            // Three unparseable files sort ahead of the good one; the run should give up
            // on the third failure and never reach "4-good.jsonl".
            File.WriteAllText(Path.Combine(dir, "1-bad.jsonl"), "not valid\n");
            File.WriteAllText(Path.Combine(dir, "2-bad.jsonl"), "not valid\n");
            File.WriteAllText(Path.Combine(dir, "3-bad.jsonl"), "not valid\n");
            File.Copy(TestDataPath("sample-traces.jsonl"), Path.Combine(dir, "4-good.jsonl"));

            var exitCode = await Importer.RunAsync(
            [
                dir,
                "--endpoint", server.HttpEndpoint.ToString(),
                "--protocol", "http",
            ]);

            Assert.Equal(ExitCode.RuntimeError, exitCode);
            // The good file was never reached, so nothing landed upstream.
            Assert.Equal(0, server.Received.SpanCount);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task DoesNotAbortWhenFailuresAreBrokenUpBySuccesses()
    {
        await using var server = await TestOtlpServer.StartAsync();

        var dir = Directory.CreateTempSubdirectory("otelimporter-e2e").FullName;
        try
        {
            // Four failures, but never three in a row (a good file resets the streak), so
            // the run continues to completion and both good files export.
            File.WriteAllText(Path.Combine(dir, "1-bad.jsonl"), "not valid\n");
            File.WriteAllText(Path.Combine(dir, "2-bad.jsonl"), "not valid\n");
            File.Copy(TestDataPath("sample-traces.jsonl"), Path.Combine(dir, "3-good.jsonl"));
            File.WriteAllText(Path.Combine(dir, "4-bad.jsonl"), "not valid\n");
            File.WriteAllText(Path.Combine(dir, "5-bad.jsonl"), "not valid\n");
            File.Copy(TestDataPath("sample-traces.jsonl"), Path.Combine(dir, "6-good.jsonl"));

            var exitCode = await Importer.RunAsync(
            [
                dir,
                "--endpoint", server.HttpEndpoint.ToString(),
                "--protocol", "http",
            ]);

            // Some files failed but the run finished -> partial success, both good files landed.
            Assert.Equal(ExitCode.PartialSuccess, exitCode);
            Assert.Equal(2 * ExpectedSpans, server.Received.SpanCount);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task InspectSkipsUnreadableFilesAndReportsPartialSuccess()
    {
        // Inspect is read-only, so no endpoint is configured here at all.
        var dir = Directory.CreateTempSubdirectory("otelimporter-e2e").FullName;
        try
        {
            // One unparseable file alongside a good one: inspect should report on the good
            // file and flag the bad one rather than dying on it.
            File.WriteAllText(Path.Combine(dir, "bad.jsonl"), "this is not valid otlp json\n");
            File.Copy(TestDataPath("sample-traces.jsonl"), Path.Combine(dir, "good.jsonl"));

            var exitCode = await Importer.RunAsync([dir, "--inspect"]);

            Assert.Equal(ExitCode.PartialSuccess, exitCode);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task SplitsLargeBatchesFromAJsonFileAcrossRequests()
    {
        await using var server = await TestOtlpServer.StartAsync();

        var dir = Directory.CreateTempSubdirectory("otelimporter-e2e").FullName;
        try
        {
            // A single .json batch of 20 spans (~3 KB once serialised); a 2 KB cap forces it
            // to be sent as several requests. Also exercises .json input acceptance end to end.
            var spans = string.Join(",", Enumerable.Range(0, 20).Select(i => $$"""{"name":"span-{{i}}"}"""));
            var batch = $$"""{"resourceSpans":[{"scopeSpans":[{"spans":[{{spans}}]}]}]}""";
            File.WriteAllText(Path.Combine(dir, "traces.json"), batch + "\n");

            var exitCode = await Importer.RunAsync(
            [
                Path.Combine(dir, "traces.json"),
                "--endpoint", server.HttpEndpoint.ToString(),
                "--protocol", "http",
                "--max-batch-size", "2",
            ]);

            Assert.Equal(ExitCode.Success, exitCode);
            // More requests than the single input batch == it was split, and no span was lost.
            Assert.True(server.Received.RequestCount > 1, $"expected splitting, got {server.Received.RequestCount} request(s)");
            Assert.Equal(20, server.Received.SpanCount);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task SplitsAMultiScopeJsonBatchPackingScopesAcrossRequests()
    {
        await using var server = await TestOtlpServer.StartAsync();

        var dir = Directory.CreateTempSubdirectory("otelimporter-e2e").FullName;
        try
        {
            // One .json batch: one resource, 8 scopes, each with a single large span (~6 KB total).
            // A 4 KB cap must split it, and several scopes should share each request (packing),
            // so the request count lands between 1 and the scope count.
            File.WriteAllText(Path.Combine(dir, "traces.json"), MultiScopeBatch(scopes: 8, spanPadding: 600) + "\n");

            var exitCode = await Importer.RunAsync(
            [
                Path.Combine(dir, "traces.json"),
                "--endpoint", server.HttpEndpoint.ToString(),
                "--protocol", "http",
                "--max-batch-size", "4",
            ]);

            Assert.Equal(ExitCode.Success, exitCode);
            Assert.Equal(8, server.Received.SpanCount);                                   // every span lands
            Assert.True(server.Received.RequestCount > 1, $"expected splitting, got {server.Received.RequestCount} request(s)");
            // Fewer requests than scopes proves scopes were packed together, not sent one-per-scope.
            Assert.True(server.Received.RequestCount < 8, $"expected scopes to share requests, got {server.Received.RequestCount}");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // One OTLP batch (one JSONL line): a single resource carrying several instrumentation
    // scopes, each with one padded span, so the batch is large and genuinely multi-scope.
    // Built by concatenation rather than a raw string literal because the nested OTLP JSON
    // contains "}}" runs that a $$"""...""" literal would read as interpolation.
    static string MultiScopeBatch(int scopes, int spanPadding)
    {
        var pad = new string('x', spanPadding);
        var scopeJson = string.Join(",", Enumerable.Range(0, scopes).Select(s =>
            "{\"scope\":{\"name\":\"scope-" + s + "\"},\"spans\":[{\"name\":\"" + pad + "-s" + s + "\"}]}"));
        return "{\"resourceSpans\":[{\"resource\":{\"attributes\":[{\"key\":\"service.name\","
             + "\"value\":{\"stringValue\":\"svc\"}}]},\"scopeSpans\":[" + scopeJson + "]}]}";
    }
}
