namespace OtelImporter.Configuration;

// Resolves the upstream endpoint and protocol from command-line options and the
// standard OpenTelemetry environment variables.
//
// Endpoint precedence (highest first):
//   1. --endpoint / -e
//   2. OTEL_EXPORTER_OTLP_TRACES_ENDPOINT   (traces-specific)
//   3. OTEL_EXPORTER_OTLP_ENDPOINT          (generic, all signals)
//
// Protocol precedence (highest first):
//   1. --protocol / -p
//   2. sniffed from the port: 4317 => gRPC, 4318 => HTTP
//   3. otherwise: error (the user must be explicit)
//
// Modelled on Octopus.Logging/OtelExporterConfiguration.cs.
internal static class ExporterConfigurationResolver
{
    public const string TracesEndpointVariable = "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT";
    public const string GenericEndpointVariable = "OTEL_EXPORTER_OTLP_ENDPOINT";

    const string GrpcExportPath = "/opentelemetry.proto.collector.trace.v1.TraceService/Export";
    const string HttpTracesPath = "/v1/traces";

    public static ConfigurationResult Resolve(CommandLineOptions options, Func<string, string?> getEnvironmentVariable)
    {
        var endpointText = options.Endpoint
            ?? NullIfBlank(getEnvironmentVariable(TracesEndpointVariable))
            ?? NullIfBlank(getEnvironmentVariable(GenericEndpointVariable));

        if (endpointText is null)
        {
            return ConfigurationResult.Failure(
                "No upstream endpoint was specified. Pass --endpoint/-e or set " +
                $"{TracesEndpointVariable} or {GenericEndpointVariable}.");
        }

        if (!TryParseEndpoint(endpointText, out var baseUri))
            return ConfigurationResult.Failure($"'{endpointText}' is not a valid endpoint URI.");

        var protocol = options.Protocol ?? SniffProtocol(baseUri);
        if (protocol is null)
        {
            return ConfigurationResult.Failure(
                $"Could not determine the protocol from the endpoint port ({DescribePort(baseUri)}). " +
                "Specify --protocol/-p with 'grpc' or 'http'.");
        }

        var endpoint = protocol == OtlpProtocol.Grpc
            ? BuildGrpcEndpoint(baseUri)
            : BuildHttpEndpoint(baseUri);

        return ConfigurationResult.Success(new ExporterConfiguration(endpoint, protocol.Value));
    }

    static bool TryParseEndpoint(string text, out Uri uri)
    {
        // Accept bare host:port style values (no scheme) by defaulting to http.
        if (!text.Contains("://", StringComparison.Ordinal))
            text = "http://" + text;

        return Uri.TryCreate(text, UriKind.Absolute, out uri!)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    static OtlpProtocol? SniffProtocol(Uri uri) => uri.Port switch
    {
        4317 => OtlpProtocol.Grpc,
        4318 => OtlpProtocol.Http,
        _ => null,
    };

    static Uri BuildGrpcEndpoint(Uri baseUri)
    {
        // gRPC uses the authority only; the method path is fixed.
        var builder = new UriBuilder(baseUri.Scheme, baseUri.Host, baseUri.Port)
        {
            Path = GrpcExportPath,
        };
        return builder.Uri;
    }

    static Uri BuildHttpEndpoint(Uri baseUri)
    {
        var path = baseUri.AbsolutePath;

        // Already pointing at the traces signal endpoint? Use as-is.
        if (path.EndsWith(HttpTracesPath, StringComparison.OrdinalIgnoreCase))
            return baseUri;

        var trimmed = path.TrimEnd('/');
        var builder = new UriBuilder(baseUri)
        {
            Path = trimmed + HttpTracesPath,
        };
        return builder.Uri;
    }

    static string DescribePort(Uri uri) => uri.IsDefaultPort ? "default" : uri.Port.ToString();

    static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
