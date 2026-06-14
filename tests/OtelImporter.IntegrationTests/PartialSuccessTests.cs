using System.Text;
using System.Text.Json;
using OtelImporter.Configuration;
using OtelImporter.Export;

namespace OtelImporter.IntegrationTests;

// Verifies that when the collector accepts a request at the transport level (HTTP 2xx
// / gRPC OK) but reports rejected spans via partial_success, the importer surfaces it
// instead of reporting a clean success. The gRPC case also validates that we correctly
// parse a real protobuf ExportTraceServiceResponse.
public class PartialSuccessTests
{
    static string SamplePath => Path.Combine(AppContext.BaseDirectory, "TestData", "sample-traces.jsonl");

    static (byte[] Line, int SpanCount) FirstBatch()
    {
        var line = File.ReadLines(SamplePath).First();
        using var document = JsonDocument.Parse(line);
        var spans = 0;
        foreach (var rs in document.RootElement.GetProperty("resourceSpans").EnumerateArray())
            foreach (var ss in rs.GetProperty("scopeSpans").EnumerateArray())
                if (ss.TryGetProperty("spans", out var s))
                    spans += s.GetArrayLength();
        return (Encoding.UTF8.GetBytes(line), spans);
    }

    static ITraceExporter CreateExporter(Uri endpoint, OtlpProtocol protocol)
    {
        var config = ExporterConfigurationResolver
            .Resolve(new CommandLineOptions { Endpoint = endpoint.ToString(), Protocol = protocol }, _ => null)
            .Configuration!;
        return new ExporterFactory().Create(config);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("grpc")]
    public async Task Surfaces_rejected_spans_from_partial_success(string protocol)
    {
        await using var server = await TestOtlpServer.StartAsync(rejectAll: true);
        var (line, spanCount) = FirstBatch();
        var endpoint = protocol == "grpc" ? server.GrpcEndpoint : server.HttpEndpoint;

        await using var exporter = CreateExporter(endpoint, protocol == "grpc" ? OtlpProtocol.Grpc : OtlpProtocol.Http);
        var outcome = await exporter.ExportAsync(line, CancellationToken.None);

        Assert.True(outcome.HasProblem);
        Assert.Equal(spanCount, outcome.RejectedSpans);
        Assert.Equal(TestOtlpServer.RejectionMessage, outcome.ErrorMessage);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("grpc")]
    public async Task Reports_clean_acceptance_when_collector_accepts(string protocol)
    {
        await using var server = await TestOtlpServer.StartAsync(rejectAll: false);
        var (line, _) = FirstBatch();
        var endpoint = protocol == "grpc" ? server.GrpcEndpoint : server.HttpEndpoint;

        await using var exporter = CreateExporter(endpoint, protocol == "grpc" ? OtlpProtocol.Grpc : OtlpProtocol.Http);
        var outcome = await exporter.ExportAsync(line, CancellationToken.None);

        Assert.False(outcome.HasProblem);
        Assert.Equal(0, outcome.RejectedSpans);
    }

    [Fact]
    public async Task Importer_returns_partial_success_exit_code_when_spans_are_rejected()
    {
        await using var server = await TestOtlpServer.StartAsync(rejectAll: true);

        var exitCode = await Importer.RunAsync(
        [
            SamplePath,
            "--endpoint", server.HttpEndpoint.ToString(),
            "--protocol", "http",
        ]);

        Assert.Equal(ExitCode.PartialSuccess, exitCode);
    }
}
