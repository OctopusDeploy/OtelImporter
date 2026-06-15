using System.Collections.Concurrent;

namespace OtelImporter.IntegrationTests;

// Thread-safe sink shared by the gRPC service and the HTTP endpoint so a test can
// assert on what the upstream actually received.
public sealed class ReceivedTraces
{
    int _spanCount;
    int _requestCount;
    readonly ConcurrentBag<KeyValuePair<string, string>> _attributes = [];
    readonly ConcurrentDictionary<string, string> _headers = new(StringComparer.OrdinalIgnoreCase);

    public int SpanCount => Volatile.Read(ref _spanCount);
    public int RequestCount => Volatile.Read(ref _requestCount);

    public void Record(int spans)
    {
        Interlocked.Increment(ref _requestCount);
        Interlocked.Add(ref _spanCount, spans);
    }

    // Records a request header seen by the server (case-insensitive; last value wins).
    public void RecordHeader(string key, string value) => _headers[key] = value;

    // The value of a request header, or null if it was never sent.
    public string? Header(string key) => _headers.TryGetValue(key, out var value) ? value : null;

    // Records one string-valued span attribute (across all spans of all requests).
    public void RecordAttribute(string key, string value) => _attributes.Add(new(key, value));

    // How many spans carried exactly this attribute key=value.
    public int CountAttribute(string key, string value) =>
        _attributes.Count(a => a.Key == key && a.Value == value);
}
