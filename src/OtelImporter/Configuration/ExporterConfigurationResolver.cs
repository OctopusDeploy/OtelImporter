namespace OtelImporter.Configuration;

// Resolves the upstream endpoint, protocol and headers from command-line options and
// the standard OpenTelemetry environment variables. The command line always wins;
// signal-specific (TRACES) variables take precedence over the generic ones.
//
// Endpoint precedence (highest first):
//   1. --endpoint / -e
//   2. OTEL_EXPORTER_OTLP_TRACES_ENDPOINT   (traces-specific)
//   3. OTEL_EXPORTER_OTLP_ENDPOINT          (generic, all signals)
//
// Protocol precedence (highest first):
//   1. --protocol / -p
//   2. OTEL_EXPORTER_OTLP_TRACES_PROTOCOL
//   3. OTEL_EXPORTER_OTLP_PROTOCOL
//   4. sniffed from the port: 4317 => gRPC, 4318 => HTTP
//   5. otherwise: error (the user must be explicit)
//
// Headers are merged across all sources (each layer overrides same-named keys from the
// layer below), highest precedence last:
//   1. OTEL_EXPORTER_OTLP_HEADERS           (generic)
//   2. OTEL_EXPORTER_OTLP_TRACES_HEADERS    (traces-specific)
//   3. --http-header / -H
//
// Modelled on Octopus.Logging/OtelExporterConfiguration.cs.
internal static class ExporterConfigurationResolver
{
    public const string TracesEndpointVariable = "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT";
    public const string GenericEndpointVariable = "OTEL_EXPORTER_OTLP_ENDPOINT";
    public const string TracesProtocolVariable = "OTEL_EXPORTER_OTLP_TRACES_PROTOCOL";
    public const string GenericProtocolVariable = "OTEL_EXPORTER_OTLP_PROTOCOL";
    public const string TracesHeadersVariable = "OTEL_EXPORTER_OTLP_TRACES_HEADERS";
    public const string GenericHeadersVariable = "OTEL_EXPORTER_OTLP_HEADERS";

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

        var protocolResult = ResolveProtocol(options, getEnvironmentVariable, baseUri);
        if (protocolResult.Error is not null)
            return ConfigurationResult.Failure(protocolResult.Error);

        var protocol = protocolResult.Protocol!.Value;
        var endpoint = protocol == OtlpProtocol.Grpc
            ? BuildGrpcEndpoint(baseUri)
            : BuildHttpEndpoint(baseUri);

        var headers = ResolveHeaders(options, getEnvironmentVariable);

        return ConfigurationResult.Success(new ExporterConfiguration(endpoint, protocol, headers));
    }

    static (OtlpProtocol? Protocol, string? Error) ResolveProtocol(
        CommandLineOptions options, Func<string, string?> getEnvironmentVariable, Uri baseUri)
    {
        if (options.Protocol is { } cliProtocol)
            return (cliProtocol, null);

        var tracesProtocol = NullIfBlank(getEnvironmentVariable(TracesProtocolVariable));
        var envProtocolText = tracesProtocol ?? NullIfBlank(getEnvironmentVariable(GenericProtocolVariable));
        if (envProtocolText is not null)
        {
            var sourceVariable = tracesProtocol is not null ? TracesProtocolVariable : GenericProtocolVariable;
            if (!CommandLineParser.TryParseProtocol(envProtocolText, out var envProtocol))
            {
                return (null,
                    $"Invalid protocol '{envProtocolText}' in {sourceVariable}. " +
                    "Expected 'grpc', 'http', 'http/protobuf', or 'http/json'.");
            }
            return (envProtocol, null);
        }

        var sniffed = SniffProtocol(baseUri);
        if (sniffed is null)
        {
            return (null,
                $"Could not determine the protocol from the endpoint port ({DescribePort(baseUri)}). " +
                $"Specify --protocol/-p with 'grpc' or 'http', or set {TracesProtocolVariable}/{GenericProtocolVariable}.");
        }

        return (sniffed, null);
    }

    // Layered merge, lowest precedence first; same-named header keys are overridden by
    // higher layers (HTTP header names are case-insensitive).
    static IReadOnlyList<KeyValuePair<string, string>> ResolveHeaders(
        CommandLineOptions options, Func<string, string?> getEnvironmentVariable)
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var header in ParseHeaderList(getEnvironmentVariable(GenericHeadersVariable)))
            merged[header.Key] = header.Value;
        foreach (var header in ParseHeaderList(getEnvironmentVariable(TracesHeadersVariable)))
            merged[header.Key] = header.Value;
        foreach (var header in options.HttpHeaders)
            merged[header.Key] = header.Value;

        return [.. merged];
    }

    // Parses the OTLP header list format: comma-separated key=value pairs, e.g.
    // "x-honeycomb-team=KEY,x-other=value". Whitespace is trimmed; malformed entries
    // (no '=' or empty key) are skipped.
    static IEnumerable<KeyValuePair<string, string>> ParseHeaderList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            yield break;

        foreach (var entry in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = entry.IndexOf('=');
            if (separator <= 0)
                continue;

            var key = entry[..separator].Trim();
            if (key.Length == 0)
                continue;

            yield return new KeyValuePair<string, string>(key, entry[(separator + 1)..].Trim());
        }
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
