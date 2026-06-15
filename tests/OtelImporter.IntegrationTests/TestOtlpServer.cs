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

    // rejectAll        => respond OK but report every span rejected via partial_success.
    // failFirstRequests => fail the first N requests transiently (503 / UNAVAILABLE) before succeeding.
    public static async Task<TestOtlpServer> StartAsync(bool rejectAll = false, int failFirstRequests = 0)
    {
        var grpcPort = GetFreePort();
        var httpPort = GetFreePort();

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddGrpc();

        var received = new ReceivedTraces();
        builder.Services.AddSingleton(received);
        builder.Services.AddSingleton(new ServerOptions { RejectAll = rejectAll, FailuresRemaining = failFirstRequests });

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
            var options = context.RequestServices.GetRequiredService<ServerOptions>();
            if (Interlocked.Decrement(ref options.FailuresRemaining) >= 0)
            {
                context.Response.StatusCode = 503; // transient failure, before recording anything
                return;
            }

            using var document = await JsonDocument.ParseAsync(context.Request.Body);
            var received = context.RequestServices.GetRequiredService<ReceivedTraces>();
            foreach (var header in context.Request.Headers)
                received.RecordHeader(header.Key, header.Value!);
            var spans = CountSpans(document.RootElement, received);
            received.Record(spans);

            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";

            if (options.RejectAll)
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

    static int CountSpans(JsonElement root, ReceivedTraces received)
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
                if (!scopeSpan.TryGetProperty("spans", out var spans))
                    continue;

                total += spans.GetArrayLength();
                foreach (var span in spans.EnumerateArray())
                    RecordAttributes(span, received);
            }
        }

        return total;
    }

    static void RecordAttributes(JsonElement span, ReceivedTraces received)
    {
        if (!span.TryGetProperty("attributes", out var attributes))
            return;

        foreach (var attribute in attributes.EnumerateArray())
        {
            if (attribute.TryGetProperty("key", out var key) &&
                attribute.TryGetProperty("value", out var value) &&
                value.TryGetProperty("stringValue", out var stringValue))
            {
                received.RecordAttribute(key.GetString()!, stringValue.GetString()!);
            }
        }
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

    sealed class ServerOptions
    {
        public bool RejectAll { get; init; }
        public int FailuresRemaining; // mutated atomically across requests
    }

    // Generated from collector/trace/v1/trace_service.proto (GrpcServices=Server).
    sealed class TestTraceService(ReceivedTraces received, ServerOptions options) : TraceService.TraceServiceBase
    {
        public override Task<ExportTraceServiceResponse> Export(ExportTraceServiceRequest request, ServerCallContext context)
        {
            if (Interlocked.Decrement(ref options.FailuresRemaining) >= 0)
                throw new RpcException(new Status(StatusCode.Unavailable, "transient failure"));

            // gRPC metadata is just HTTP/2 headers; skip binary (-bin) entries.
            foreach (var entry in context.RequestHeaders)
                if (!entry.IsBinary)
                    received.RecordHeader(entry.Key, entry.Value);

            var spans = 0;
            foreach (var resourceSpans in request.ResourceSpans)
            foreach (var scopeSpans in resourceSpans.ScopeSpans)
            {
                spans += scopeSpans.Spans.Count;
                foreach (var span in scopeSpans.Spans)
                foreach (var attribute in span.Attributes)
                {
                    if (attribute.Value.ValueCase == OpenTelemetry.Proto.Common.V1.AnyValue.ValueOneofCase.StringValue)
                        received.RecordAttribute(attribute.Key, attribute.Value.StringValue);
                }
            }

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
