namespace OtelImporter.Pipeline;

// Paces outgoing batches. WaitAsync blocks just long enough to keep within the
// configured rate before each export.
internal interface IRateLimiter
{
    Task WaitAsync(CancellationToken cancellationToken);
}
