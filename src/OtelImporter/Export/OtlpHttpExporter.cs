using System.Net.Http.Headers;
using System.Text.Json;
using OtelImporter.Otlp;

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

    public async Task<ExportOutcome> ExportAsync(ReadOnlyMemory<byte> otlpJsonLine, CancellationToken cancellationToken)
    {
        using var content = new ReadOnlyMemoryContent(otlpJsonLine);
        content.Headers.ContentType = Json;

        using var response = await _httpClient.PostAsync(_endpoint, content, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new TraceExportException(
                $"OTLP/HTTP export to {_endpoint} failed with status {(int)response.StatusCode} {response.ReasonPhrase}. " +
                System.Text.Encoding.UTF8.GetString(body).Trim());
        }

        return ParsePartialSuccess(body);
    }

    // The OTLP/HTTP success response is a JSON ExportTraceServiceResponse, usually "{}".
    static ExportOutcome ParsePartialSuccess(ReadOnlySpan<byte> body)
    {
        if (body.IsEmpty)
            return ExportOutcome.Accepted;

        ExportTraceServiceResponse? response;
        try
        {
            response = JsonSerializer.Deserialize(body, OtlpJsonContext.Default.ExportTraceServiceResponse);
        }
        catch (JsonException)
        {
            // An unexpected/non-JSON body shouldn't fail the export; treat as accepted.
            return ExportOutcome.Accepted;
        }

        return response?.PartialSuccess is { } partial
            ? new ExportOutcome(partial.RejectedSpans, partial.ErrorMessage)
            : ExportOutcome.Accepted;
    }

    public ValueTask DisposeAsync()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
        return ValueTask.CompletedTask;
    }
}
