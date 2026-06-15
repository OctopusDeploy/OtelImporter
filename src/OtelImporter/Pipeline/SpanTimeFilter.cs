using OtelImporter.Otlp;

namespace OtelImporter.Pipeline;

// Drops spans whose start time falls outside an inclusive [from, to] window. Either
// bound may be open. Spans without a start time (unset / 0) are kept -- we only ever
// remove a span we can prove is outside the window. Filtering mutates the batch in
// place; the bounds are pre-converted to unix-nanos so each span is a cheap integer
// comparison.
internal sealed class SpanTimeFilter
{
    readonly ulong? _fromUnixNano;
    readonly ulong? _toUnixNano;

    SpanTimeFilter(ulong? fromUnixNano, ulong? toUnixNano)
    {
        _fromUnixNano = fromUnixNano;
        _toUnixNano = toUnixNano;
    }

    // Returns null when no window is configured, so callers can skip filtering entirely.
    public static SpanTimeFilter? Create(DateTimeOffset? from, DateTimeOffset? to) =>
        from is null && to is null ? null : new SpanTimeFilter(ToUnixNano(from), ToUnixNano(to));

    public void Apply(ExportTraceServiceRequest request)
    {
        if (request.ResourceSpans is null)
            return;

        foreach (var resourceSpans in request.ResourceSpans)
        {
            if (resourceSpans.ScopeSpans is null)
                continue;

            foreach (var scopeSpans in resourceSpans.ScopeSpans)
                scopeSpans.Spans?.RemoveAll(IsOutsideWindow);
        }
    }

    bool IsOutsideWindow(Span span)
    {
        var start = span.StartTimeUnixNano;
        if (start == 0)
            return false; // unknown start time -> keep

        if (_fromUnixNano is { } from && start < from)
            return true;
        if (_toUnixNano is { } to && start > to)
            return true;
        return false;
    }

    // True if the batch still has at least one span (e.g. after filtering).
    public static bool HasSpans(ExportTraceServiceRequest request)
    {
        if (request.ResourceSpans is null)
            return false;

        foreach (var resourceSpans in request.ResourceSpans)
        {
            if (resourceSpans.ScopeSpans is null)
                continue;

            foreach (var scopeSpans in resourceSpans.ScopeSpans)
                if (scopeSpans.Spans is { Count: > 0 })
                    return true;
        }

        return false;
    }

    const ulong NanosPerTick = 100; // 1 tick = 100 ns

    static ulong? ToUnixNano(DateTimeOffset? value)
    {
        if (value is null)
            return null;

        var ticks = (value.Value.ToUniversalTime() - DateTimeOffset.UnixEpoch).Ticks;
        return ticks <= 0 ? 0UL : (ulong)ticks * NanosPerTick;
    }
}
