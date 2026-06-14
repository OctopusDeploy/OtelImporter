using System.Net.Http.Headers;

namespace OtelImporter.Export;

// Exports over OTLP/HTTP. The input *.jsonl lines are already encoded as
// ExportTraceServiceRequest in OTLP/JSON, which is exactly what the /v1/traces
// endpoint accepts with Content-Type: application/json -- so we forward the raw
// bytes without parsing or re-serializing.
internal sealed class OtlpHttpExporter : ITraceExporter
{
    static readonly MediaTypeHeaderValue Json = new("application/json");

    readonly HttpClient _httpClient;
    readonly Uri _endpoint;
    readonly bool _ownsHttpClient;

    public OtlpHttpExporter(HttpClient httpClient, Uri endpoint, bool ownsHttpClient = false)
    {
        _httpClient = httpClient;
        _endpoint = endpoint;
        _ownsHttpClient = ownsHttpClient;
    }

    public async Task ExportAsync(ReadOnlyMemory<byte> otlpJsonLine, CancellationToken cancellationToken)
    {
        using var content = new ReadOnlyMemoryContent(otlpJsonLine);
        content.Headers.ContentType = Json;

        using var response = await _httpClient.PostAsync(_endpoint, content, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        throw new TraceExportException(
            $"OTLP/HTTP export to {_endpoint} failed with status {(int)response.StatusCode} {response.ReasonPhrase}. {body}".Trim());
    }

    public ValueTask DisposeAsync()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
        return ValueTask.CompletedTask;
    }
}
