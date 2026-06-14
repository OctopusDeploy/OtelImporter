namespace OtelImporter.Configuration;

// The fully-resolved upstream target. Endpoint is the exact URI an exporter posts to:
//   * HTTP: includes the /v1/traces signal path
//   * gRPC: includes the TraceService/Export method path
internal sealed record ExporterConfiguration(Uri Endpoint, OtlpProtocol Protocol);

internal sealed record ConfigurationResult(ExporterConfiguration? Configuration, string? Error)
{
    public static ConfigurationResult Success(ExporterConfiguration configuration) => new(configuration, null);
    public static ConfigurationResult Failure(string error) => new(null, error);
}
