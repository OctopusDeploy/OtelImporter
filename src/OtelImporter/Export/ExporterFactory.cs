using System.Net;
using OtelImporter.Configuration;

namespace OtelImporter.Export;

internal interface IExporterFactory
{
    ITraceExporter Create(ExporterConfiguration configuration);
}

internal sealed class ExporterFactory : IExporterFactory
{
    public ITraceExporter Create(ExporterConfiguration configuration)
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
            return new OtlpGrpcExporter(grpcClient, configuration.Endpoint, ownsHttpClient: true);
        }

        var httpClient = new HttpClient();
        return new OtlpHttpExporter(httpClient, configuration.Endpoint, ownsHttpClient: true);
    }
}
