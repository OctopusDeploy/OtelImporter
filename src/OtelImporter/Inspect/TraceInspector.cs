using OtelImporter.Otlp;

namespace OtelImporter.Inspect;

// Accumulates summary statistics over a stream of trace batches. State is bounded:
// counters plus one entry per *distinct* span name (operation names are inherently
// low-cardinality), so memory stays flat no matter how many spans flow through --
// individual spans are never retained.
internal sealed class TraceInspector
{
    public const string NoName = "<No Name>";

    long _spanCount;
    bool _haveTimestamp;
    ulong _oldestStartUnixNano;
    ulong _newestStartUnixNano;
    readonly Dictionary<string, long> _nameCounts = new(StringComparer.Ordinal);

    public void Add(ExportTraceServiceRequest request)
    {
        if (request.ResourceSpans is null)
            return;

        foreach (var resourceSpans in request.ResourceSpans)
        {
            if (resourceSpans.ScopeSpans is null)
                continue;

            foreach (var scopeSpans in resourceSpans.ScopeSpans)
            {
                if (scopeSpans.Spans is null)
                    continue;

                foreach (var span in scopeSpans.Spans)
                    AddSpan(span);
            }
        }
    }

    void AddSpan(Span span)
    {
        _spanCount++;

        var name = string.IsNullOrEmpty(span.Name) ? NoName : span.Name;
        _nameCounts.TryGetValue(name, out var count);
        _nameCounts[name] = count + 1;

        // Ignore unset (zero) timestamps so they don't drag "oldest" back to the epoch.
        var start = span.StartTimeUnixNano;
        if (start == 0)
            return;

        if (!_haveTimestamp)
        {
            _oldestStartUnixNano = start;
            _newestStartUnixNano = start;
            _haveTimestamp = true;
        }
        else
        {
            if (start < _oldestStartUnixNano) _oldestStartUnixNano = start;
            if (start > _newestStartUnixNano) _newestStartUnixNano = start;
        }
    }

    public InspectionSummary BuildSummary(long batchCount)
    {
        // Highest count first; ties broken by name for deterministic output.
        var top = _nameCounts
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Take(10)
            .Select(pair => new SpanNameCount(pair.Key, pair.Value))
            .ToList();

        DateTimeOffset? oldest = _haveTimestamp ? FromUnixNano(_oldestStartUnixNano) : null;
        DateTimeOffset? newest = _haveTimestamp ? FromUnixNano(_newestStartUnixNano) : null;
        TimeSpan? duration = _haveTimestamp
            ? TimeSpan.FromTicks((long)((_newestStartUnixNano - _oldestStartUnixNano) / NanosPerTick))
            : null;

        return new InspectionSummary(batchCount, _spanCount, oldest, newest, duration, top);
    }

    const ulong NanosPerTick = 100; // 1 tick = 100 ns

    static DateTimeOffset FromUnixNano(ulong nanos) =>
        DateTimeOffset.UnixEpoch.AddTicks((long)(nanos / NanosPerTick));
}
