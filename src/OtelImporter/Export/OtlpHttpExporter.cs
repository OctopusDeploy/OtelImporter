using System.Net.Http.Headers;
using System.Text.Json;
using OtelImporter.Otlp;

namespace OtelImporter.Export;

// Exports over OTLP/HTTP. The batch arrives already parsed into the object model
// (the runner deserializes each input line once); we serialize it to OTLP/JSON for
// the /v1/traces endpoint. This mirrors the gRPC path, which encodes the same model
// as protobuf.
internal sealed class OtlpHttpExporter : ITraceExporter
{
    static readonly MediaTypeHeaderValue Json = new("application/json");

    readonly HttpClient _httpClient;
    readonly Uri _endpoint;
    readonly bool _ownsHttpClient;
    readonly long? _maxBatchBytes;

    public OtlpHttpExporter(HttpClient httpClient, Uri endpoint, bool ownsHttpClient = false, long? maxBatchBytes = null)
    {
        _httpClient = httpClient;
        _endpoint = endpoint;
        _ownsHttpClient = ownsHttpClient;
        _maxBatchBytes = maxBatchBytes;
    }

    // Serialize to OTLP/JSON. With no size limit the whole batch is one frame (a single
    // serialization); otherwise spans are packed into frames measured as real JSON bytes.
    public PreparedBatches Prepare(ExportTraceServiceRequest request)
    {
        // Serialize the whole batch once. With no limit, or when it already fits, that single
        // frame is the result; only an oversized batch pays the span-by-span packing cost.
        var whole = JsonSerializer.SerializeToUtf8Bytes(request, OtlpJsonContext.Default.ExportTraceServiceRequest);
        if (_maxBatchBytes is not { } maxBytes || whole.Length <= maxBytes)
            return new PreparedBatches([whole], 0);

        return SpanBatcher.Pack(request, maxBytes, new JsonBatchBuilder());
    }

    public async Task<ExportOutcome> SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
    {
        using var content = new ReadOnlyMemoryContent(frame);
        content.Headers.ContentType = Json;

        using var response = await _httpClient.PostAsync(_endpoint, content, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new TraceExportException(
                $"OTLP/HTTP export to {_endpoint} failed with status {(int)response.StatusCode} {response.ReasonPhrase}. " +
                System.Text.Encoding.UTF8.GetString(body).Trim())
            {
                IsRetryable = IsRetryableStatus(response.StatusCode),
                RetryAfter = GetRetryAfter(response),
            };
        }

        return ParsePartialSuccess(body);
    }

    // Per the OTLP/HTTP spec these statuses are transient and should be retried.
    static bool IsRetryableStatus(System.Net.HttpStatusCode status) => (int)status switch
    {
        408 => true, // Request Timeout
        429 => true, // Too Many Requests
        502 => true, // Bad Gateway
        503 => true, // Service Unavailable
        504 => true, // Gateway Timeout
        _ => false,
    };

    static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter is null)
            return null;

        if (retryAfter.Delta is { } delta)
            return delta;

        if (retryAfter.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return null;
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
