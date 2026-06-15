using System.Net;
using OtelImporter.Configuration;

namespace OtelImporter.Export;

internal interface IExporterFactory
{
    ITraceExporter Create(ExporterConfiguration configuration, IReadOnlyList<KeyValuePair<string, string>>? headers = null);
}

internal sealed class ExporterFactory : IExporterFactory
{
    public ITraceExporter Create(ExporterConfiguration configuration, IReadOnlyList<KeyValuePair<string, string>>? headers = null)
    {
        if (configuration.Protocol == OtlpProtocol.Grpc)
        {
            // Allow gRPC over cleartext HTTP/2 (h2c) for plain http:// endpoints;
            // https:// endpoints negotiate HTTP/2 via ALPN as usual.
            AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

            var grpcClient = new HttpClient
            {
                DefaultRequestVersion = HttpVersion.Version20,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            };
            ApplyHeaders(grpcClient, headers);
            return new OtlpGrpcExporter(grpcClient, configuration.Endpoint, ownsHttpClient: true);
        }

        var httpClient = new HttpClient();
        ApplyHeaders(httpClient, headers);
        return new OtlpHttpExporter(httpClient, configuration.Endpoint, ownsHttpClient: true);
    }

    // Custom headers are sent on every export request. On the HTTP/2 (gRPC) client they
    // travel as HTTP/2 headers, i.e. gRPC metadata -- which is how OTLP/gRPC carries
    // auth such as Honeycomb's x-honeycomb-team. TryAddWithoutValidation avoids rejecting
    // opaque token values.
    static void ApplyHeaders(HttpClient client, IReadOnlyList<KeyValuePair<string, string>>? headers)
    {
        if (headers is null)
            return;

        foreach (var (key, value) in headers)
            client.DefaultRequestHeaders.TryAddWithoutValidation(key, value);
    }
}
