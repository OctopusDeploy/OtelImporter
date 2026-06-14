namespace OtelImporter.Inspect;

// The result of a read-only pass over a trace file. Timestamps are null when the
// file contained no spans with a usable start time.
internal sealed record InspectionSummary(
    long BatchCount,
    long SpanCount,
    DateTimeOffset? OldestSpan,
    DateTimeOffset? NewestSpan,
    TimeSpan? Duration,
    IReadOnlyList<SpanNameCount> TopSpanNames);

internal sealed record SpanNameCount(string Name, long Count);
