using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using OtelImporter.Otlp;

namespace OtelImporter.Export;

// Exports over OTLP/gRPC without taking a dependency on Grpc.Net.Client or the
// protobuf tooling. gRPC-over-HTTP/2 is simple enough to speak directly:
//   * one length-prefixed message frame: [1-byte compression flag][4-byte big-endian length][payload]
//   * Content-Type: application/grpc
//   * the call result is carried in the grpc-status trailer (0 == OK)
//
// We parse the OTLP/JSON line into the object model and re-encode it as OTLP
// protobuf (see OtlpProtobufSerializer) for the payload.
internal sealed class OtlpGrpcExporter : ITraceExporter
{
    static readonly MediaTypeHeaderValue GrpcContentType = new("application/grpc");

    readonly HttpClient _httpClient;
    readonly Uri _endpoint;
    readonly bool _ownsHttpClient;

    public OtlpGrpcExporter(HttpClient httpClient, Uri endpoint, bool ownsHttpClient = false)
    {
        _httpClient = httpClient;
        _endpoint = endpoint;
        _ownsHttpClient = ownsHttpClient;
    }

    public async Task<ExportOutcome> ExportAsync(ReadOnlyMemory<byte> otlpJsonLine, CancellationToken cancellationToken)
    {
        var request = JsonSerializer.Deserialize(otlpJsonLine.Span, OtlpJsonContext.Default.ExportTraceServiceRequest)
                      ?? throw new TraceExportException("Trace line deserialized to null.");

        var payload = OtlpProtobufSerializer.Serialize(request);
        var frame = Frame(payload);

        using var content = new ByteArrayContent(frame);
        content.Headers.ContentType = GrpcContentType;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = content,
        };
        httpRequest.Headers.TE.Add(new TransferCodingWithQualityHeaderValue("trailers"));

        using var response = await _httpClient
            .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        // The body must be drained before trailing headers (grpc-status) are available.
        var responseBody = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        if (response.StatusCode != HttpStatusCode.OK)
            throw new TraceExportException($"OTLP/gRPC export to {_endpoint} returned HTTP {(int)response.StatusCode}.");

        var (status, message) = ReadGrpcStatus(response);
        if (status != GrpcStatusOk)
        {
            throw new TraceExportException(
                $"OTLP/gRPC export to {_endpoint} failed with grpc-status {status}" +
                (string.IsNullOrEmpty(message) ? "." : $": {message}"));
        }

        return ParsePartialSuccess(responseBody);
    }

    // The gRPC response is a length-prefixed protobuf ExportTraceServiceResponse:
    //   ExportTraceServiceResponse { ExportTracePartialSuccess partial_success = 1; }
    //   ExportTracePartialSuccess { int64 rejected_spans = 1; string error_message = 2; }
    static ExportOutcome ParsePartialSuccess(ReadOnlySpan<byte> framedResponse)
    {
        if (framedResponse.Length <= 5) // 1-byte flag + 4-byte length, no message
            return ExportOutcome.Accepted;

        var length = (int)ProtoReader.ReadFixed32BigEndian(framedResponse.Slice(1, 4));
        var message = framedResponse.Slice(5, Math.Min(length, framedResponse.Length - 5));

        var partial = FindLengthDelimited(message, fieldNumber: 1);
        if (partial.IsEmpty)
            return ExportOutcome.Accepted;

        long rejectedSpans = 0;
        string? errorMessage = null;
        var reader = new ProtoReader(partial);
        while (!reader.End)
        {
            var (field, wireType) = reader.ReadTag();
            switch (field, wireType)
            {
                case (1, 0): rejectedSpans = (long)reader.ReadVarint(); break;
                case (2, 2): errorMessage = System.Text.Encoding.UTF8.GetString(reader.ReadLengthDelimited()); break;
                default: reader.Skip(wireType); break;
            }
        }

        return new ExportOutcome(rejectedSpans, errorMessage);
    }

    static ReadOnlySpan<byte> FindLengthDelimited(ReadOnlySpan<byte> message, int fieldNumber)
    {
        var reader = new ProtoReader(message);
        while (!reader.End)
        {
            var (field, wireType) = reader.ReadTag();
            if (field == fieldNumber && wireType == 2)
                return reader.ReadLengthDelimited();
            reader.Skip(wireType);
        }
        return default;
    }

    const int GrpcStatusOk = 0;

    static byte[] Frame(ReadOnlySpan<byte> payload)
    {
        var frame = new byte[5 + payload.Length];
        frame[0] = 0; // not compressed
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(1, 4), (uint)payload.Length);
        payload.CopyTo(frame.AsSpan(5));
        return frame;
    }

    static (int Status, string? Message) ReadGrpcStatus(HttpResponseMessage response)
    {
        // grpc-status normally arrives as a trailer, but a "Trailers-Only" response
        // carries it in the leading headers instead.
        var statusText = GetHeader(response.TrailingHeaders, "grpc-status")
                         ?? GetHeader(response.Headers, "grpc-status");
        var message = GetHeader(response.TrailingHeaders, "grpc-message")
                      ?? GetHeader(response.Headers, "grpc-message");

        // Absent grpc-status on an HTTP 200 response means success.
        var status = statusText is not null && int.TryParse(statusText, out var parsed)
            ? parsed
            : GrpcStatusOk;

        return (status, message);
    }

    static string? GetHeader(HttpHeaders headers, string name) =>
        headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    public ValueTask DisposeAsync()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
        return ValueTask.CompletedTask;
    }
}
