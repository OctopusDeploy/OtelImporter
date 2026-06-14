namespace OtelImporter.Export;

internal sealed class TraceExportException : Exception
{
    // Whether the failure is transient and the export is worth retrying.
    public bool IsRetryable { get; init; }

    // A server-provided hint (e.g. HTTP Retry-After) for how long to wait before retrying.
    public TimeSpan? RetryAfter { get; init; }

    public TraceExportException(string message) : base(message)
    {
    }

    public TraceExportException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
