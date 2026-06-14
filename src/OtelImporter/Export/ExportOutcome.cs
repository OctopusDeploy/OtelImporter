namespace OtelImporter.Export;

// The result of exporting one batch. The collector can accept a request at the
// transport level (HTTP 2xx / gRPC OK) yet still reject some or all spans, reporting
// that via partial_success. We capture it so the importer can surface it rather than
// silently claim success.
internal readonly record struct ExportOutcome(long RejectedSpans, string? ErrorMessage)
{
    public static readonly ExportOutcome Accepted = new(0, null);

    // A partial success with rejected_spans == 0 but a non-empty message is a warning.
    public bool HasProblem => RejectedSpans > 0 || !string.IsNullOrEmpty(ErrorMessage);
}
