namespace OtelImporter.IntegrationTests;

// Thread-safe sink shared by the gRPC service and the HTTP endpoint so a test can
// assert on what the upstream actually received.
public sealed class ReceivedTraces
{
    int _spanCount;
    int _requestCount;

    public int SpanCount => Volatile.Read(ref _spanCount);
    public int RequestCount => Volatile.Read(ref _requestCount);

    public void Record(int spans)
    {
        Interlocked.Increment(ref _requestCount);
        Interlocked.Add(ref _spanCount, spans);
    }
}
