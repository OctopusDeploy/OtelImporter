using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Proto.Collector.Trace.V1;

namespace OtelImporter.IntegrationTests;

// A real ASP.NET Core OTLP receiver used for end-to-end tests:
//   * gRPC on a cleartext HTTP/2 (h2c) endpoint, using the proper Grpc.AspNetCore
//     stack and the generated TraceService base -- this exercises (and validates)
//     the importer's hand-rolled protobuf encoding on the wire.
//   * HTTP/JSON on an HTTP/1.1 endpoint at /v1/traces.
// Both feed a shared ReceivedTraces sink.
public sealed class TestOtlpServer : IAsyncDisposable
{
    readonly WebApplication _app;

    public Uri GrpcEndpoint { get; }
    public Uri HttpEndpoint { get; }
    public ReceivedTraces Received { get; }

    TestOtlpServer(WebApplication app, ReceivedTraces received, int grpcPort, int httpPort)
    {
        _app = app;
        Received = received;
        GrpcEndpoint = new Uri($"http://127.0.0.1:{grpcPort}");
        HttpEndpoint = new Uri($"http://127.0.0.1:{httpPort}");
    }

    public const string RejectionMessage = "test rejection";

    public static async Task<TestOtlpServer> StartAsync(bool rejectAll = false)
    {
        var grpcPort = GetFreePort();
        var httpPort = GetFreePort();

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddGrpc();

        var received = new ReceivedTraces();
        builder.Services.AddSingleton(received);
        builder.Services.AddSingleton(new ServerOptions(rejectAll));

        builder.WebHost.ConfigureKestrel(options =>
        {
            // gRPC needs HTTP/2; over cleartext that means an h2c-only endpoint.
            options.Listen(IPAddress.Loopback, grpcPort, listen => listen.Protocols = HttpProtocols.Http2);
            // OTLP/HTTP from this importer uses HTTP/1.1.
            options.Listen(IPAddress.Loopback, httpPort, listen => listen.Protocols = HttpProtocols.Http1);
        });

        var app = builder.Build();
        app.MapGrpcService<TestTraceService>();
        app.MapPost("/v1/traces", async context =>
        {
            using var document = await JsonDocument.ParseAsync(context.Request.Body);
            var spans = CountSpans(document.RootElement);
            context.RequestServices.GetRequiredService<ReceivedTraces>().Record(spans);

            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";

            if (context.RequestServices.GetRequiredService<ServerOptions>().RejectAll)
            {
                // OTLP/JSON encodes int64 (rejectedSpans) as a string.
                await context.Response.WriteAsync(
                    $"{{\"partialSuccess\":{{\"rejectedSpans\":\"{spans}\",\"errorMessage\":\"{RejectionMessage}\"}}}}");
            }
            else
            {
                await context.Response.WriteAsync("{}"); // empty ExportTraceServiceResponse
            }
        });

        var server = new TestOtlpServer(app, received, grpcPort, httpPort);
        await app.StartAsync();
        return server;
    }

    static int CountSpans(JsonElement root)
    {
        var total = 0;
        if (!root.TryGetProperty("resourceSpans", out var resourceSpans))
            return 0;

        foreach (var resourceSpan in resourceSpans.EnumerateArray())
        {
            if (!resourceSpan.TryGetProperty("scopeSpans", out var scopeSpans))
                continue;

            foreach (var scopeSpan in scopeSpans.EnumerateArray())
            {
                if (scopeSpan.TryGetProperty("spans", out var spans))
                    total += spans.GetArrayLength();
            }
        }

        return total;
    }

    static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    sealed record ServerOptions(bool RejectAll);

    // Generated from collector/trace/v1/trace_service.proto (GrpcServices=Server).
    sealed class TestTraceService(ReceivedTraces received, ServerOptions options) : TraceService.TraceServiceBase
    {
        public override Task<ExportTraceServiceResponse> Export(ExportTraceServiceRequest request, ServerCallContext context)
        {
            var spans = 0;
            foreach (var resourceSpans in request.ResourceSpans)
            foreach (var scopeSpans in resourceSpans.ScopeSpans)
                spans += scopeSpans.Spans.Count;

            received.Record(spans);

            var response = new ExportTraceServiceResponse();
            if (options.RejectAll)
            {
                response.PartialSuccess = new ExportTracePartialSuccess
                {
                    RejectedSpans = spans,
                    ErrorMessage = RejectionMessage,
                };
            }

            return Task.FromResult(response);
        }
    }
}
