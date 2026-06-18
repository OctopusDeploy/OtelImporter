using System.Text;
using System.Text.Json;
using OtelImporter.Configuration;
using OtelImporter.Export;
using OtelImporter.Otlp;

namespace OtelImporter.IntegrationTests;

// Verifies that when the collector accepts a request at the transport level (HTTP 2xx
// / gRPC OK) but reports rejected spans via partial_success, the importer surfaces it
// instead of reporting a clean success. The gRPC case also validates that we correctly
// parse a real protobuf ExportTraceServiceResponse.
public class PartialSuccessTests
{
    static string SamplePath => Path.Combine(AppContext.BaseDirectory, "TestData", "sample-traces.jsonl");

    static (ExportTraceServiceRequest Request, int SpanCount) FirstBatch()
    {
        var line = File.ReadLines(SamplePath).First();
        using var document = JsonDocument.Parse(line);
        var spans = 0;
        foreach (var rs in document.RootElement.GetProperty("resourceSpans").EnumerateArray())
            foreach (var ss in rs.GetProperty("scopeSpans").EnumerateArray())
                if (ss.TryGetProperty("spans", out var s))
                    spans += s.GetArrayLength();
        var request = JsonSerializer.Deserialize(Encoding.UTF8.GetBytes(line), OtlpJsonContext.Default.ExportTraceServiceRequest)!;
        return (request, spans);
    }

    static ITraceExporter CreateExporter(Uri endpoint, OtlpProtocol protocol)
    {
        var config = ExporterConfigurationResolver
            .Resolve(new CommandLineOptions { Endpoint = endpoint.ToString(), Protocol = protocol }, _ => null)
            .Configuration!;
        return new ExporterFactory().Create(config);
    }

    // No size limit configured, so the batch prepares to a single frame; send it and return
    // the outcome.
    static async Task<ExportOutcome> ExportAsync(ITraceExporter exporter, ExportTraceServiceRequest request)
    {
        var prepared = exporter.Prepare(request);
        return await exporter.SendAsync(prepared.Frames.Single(), CancellationToken.None);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("grpc")]
    public async Task SurfacesRejectedSpansFromPartialSuccess(string protocol)
    {
        await using var server = await TestOtlpServer.StartAsync(rejectAll: true);
        var (request, spanCount) = FirstBatch();
        var endpoint = protocol == "grpc" ? server.GrpcEndpoint : server.HttpEndpoint;

        await using var exporter = CreateExporter(endpoint, protocol == "grpc" ? OtlpProtocol.Grpc : OtlpProtocol.Http);
        var outcome = await ExportAsync(exporter, request);

        Assert.True(outcome.HasProblem);
        Assert.Equal(spanCount, outcome.RejectedSpans);
        Assert.Equal(TestOtlpServer.RejectionMessage, outcome.ErrorMessage);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("grpc")]
    public async Task ReportsCleanAcceptanceWhenCollectorAccepts(string protocol)
    {
        await using var server = await TestOtlpServer.StartAsync(rejectAll: false);
        var (request, _) = FirstBatch();
        var endpoint = protocol == "grpc" ? server.GrpcEndpoint : server.HttpEndpoint;

        await using var exporter = CreateExporter(endpoint, protocol == "grpc" ? OtlpProtocol.Grpc : OtlpProtocol.Http);
        var outcome = await ExportAsync(exporter, request);

        Assert.False(outcome.HasProblem);
        Assert.Equal(0, outcome.RejectedSpans);
    }

    [Fact]
    public async Task ImporterReturnsPartialSuccessExitCodeWhenSpansAreRejected()
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
