using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using OtelImporter.Otlp;

namespace OtelImporter.Pipeline;

// Outcome of splitting one batch: the resulting batches (each serialising to within the
// limit, best effort) plus the number of spans dropped because they exceeded the limit on
// their own and so could not be placed in any batch.
internal sealed record SplitResult(IReadOnlyList<ExportTraceServiceRequest> Batches, long SkippedSpanCount);

// Splits an oversized OTLP batch into smaller ones so each stays within a byte budget
// (measured as OTLP/JSON, the representation the input files use). Spans are the unit of
// division and are packed greedily: a single output batch fills up across as many
// resource/scope groups as fit, so several small scopes share one request rather than each
// getting its own. The data model is preserved -- every span keeps its resource and scope.
// A span larger than the budget on its own can't be placed anywhere, so it is dropped and
// counted in SkippedSpanCount.
internal static class BatchSplitter
{
    public static SplitResult Split(ExportTraceServiceRequest request, long maxBytes)
    {
        // Fast path: the whole batch already fits, so forward it untouched (no re-grouping,
        // no extra allocation) -- the common case once a sensible limit is chosen.
        if (Measure(request, OtlpJsonContext.Default.ExportTraceServiceRequest) <= maxBytes)
            return new SplitResult([request], 0);

        var batches = new List<ExportTraceServiceRequest>();
        long skippedSpanCount = 0;
        var builder = new RequestBuilder();

        foreach (var resourceSpans in request.ResourceSpans ?? [])
        {
            foreach (var scopeSpans in resourceSpans.ScopeSpans ?? [])
            {
                if (scopeSpans.Spans is not { Count: > 0 })
                    continue;

                foreach (var span in scopeSpans.Spans)
                {
                    // Pack into the batch in progress while the span still fits.
                    if (!builder.IsEmpty)
                    {
                        var delta = builder.DeltaFor(resourceSpans, scopeSpans, span);
                        if (builder.Total + delta <= maxBytes)
                        {
                            builder.Add(resourceSpans, scopeSpans, span, delta);
                            continue;
                        }

                        // Full: close it off and try the span in a fresh batch below.
                        batches.Add(builder.Flush()!);
                    }

                    var freshCost = Measure(Wrap(resourceSpans, scopeSpans, span), OtlpJsonContext.Default.ExportTraceServiceRequest);
                    if (freshCost > maxBytes)
                    {
                        skippedSpanCount++; // too big for any batch, even on its own
                        continue;
                    }

                    builder.Start(resourceSpans, scopeSpans, span, freshCost);
                }
            }
        }

        if (!builder.IsEmpty)
            batches.Add(builder.Flush()!);

        return new SplitResult(batches, skippedSpanCount);
    }

    // Accumulates spans into one output request, grouped under their originating resource and
    // scope, tracking the exact serialized size so it never crosses the budget. Spans arrive in
    // resource/scope order, so only the most recently opened group is ever appended to.
    sealed class RequestBuilder
    {
        List<ResourceSpans>? _resourceSpans;
        ResourceSpans? _openResourceKey; // original ref of the resource currently being filled
        ScopeSpans? _openScopeKey;       // original ref of the scope currently being filled
        long _total;

        public bool IsEmpty => _resourceSpans is null;
        public long Total => _total;

        // Exact extra bytes that adding this span to the current output would cost: a whole
        // new resource element, a new scope element, or just the span -- plus its comma.
        public long DeltaFor(ResourceSpans rs, ScopeSpans ss, Span span) =>
            !ReferenceEquals(_openResourceKey, rs) ? Measure(ResourceElement(rs, ss, span), OtlpJsonContext.Default.ResourceSpans) + 1
            : !ReferenceEquals(_openScopeKey, ss) ? Measure(ScopeElement(ss, span), OtlpJsonContext.Default.ScopeSpans) + 1
            : Measure(span, OtlpJsonContext.Default.Span) + 1;

        public void Add(ResourceSpans rs, ScopeSpans ss, Span span, long delta)
        {
            if (!ReferenceEquals(_openResourceKey, rs))
            {
                _resourceSpans!.Add(ResourceElement(rs, ss, span));
                _openResourceKey = rs;
                _openScopeKey = ss;
            }
            else if (!ReferenceEquals(_openScopeKey, ss))
            {
                _resourceSpans![^1].ScopeSpans!.Add(ScopeElement(ss, span));
                _openScopeKey = ss;
            }
            else
            {
                _resourceSpans![^1].ScopeSpans![^1].Spans!.Add(span);
            }

            _total += delta;
        }

        public void Start(ResourceSpans rs, ScopeSpans ss, Span span, long cost)
        {
            _resourceSpans = [ResourceElement(rs, ss, span)];
            _openResourceKey = rs;
            _openScopeKey = ss;
            _total = cost;
        }

        // Hands back the assembled request and resets for the next one.
        public ExportTraceServiceRequest? Flush()
        {
            if (_resourceSpans is null)
                return null;

            var request = new ExportTraceServiceRequest { ResourceSpans = _resourceSpans };
            _resourceSpans = null;
            _openResourceKey = null;
            _openScopeKey = null;
            _total = 0;
            return request;
        }
    }

    // A resource element carrying one scope with one span, mirroring the originals' metadata.
    // The nested lists are mutable so further spans/scopes can be appended as packing continues.
    static ResourceSpans ResourceElement(ResourceSpans rs, ScopeSpans ss, Span span) =>
        new() { Resource = rs.Resource, SchemaUrl = rs.SchemaUrl, ScopeSpans = [ScopeElement(ss, span)] };

    static ScopeSpans ScopeElement(ScopeSpans ss, Span span) =>
        new() { Scope = ss.Scope, SchemaUrl = ss.SchemaUrl, Spans = [span] };

    static ExportTraceServiceRequest Wrap(ResourceSpans rs, ScopeSpans ss, Span span) =>
        new() { ResourceSpans = [ResourceElement(rs, ss, span)] };

    // Serialized UTF-8 byte length, without retaining the bytes.
    static long Measure<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        var counter = new ByteCounter();
        using var writer = new Utf8JsonWriter(counter);
        JsonSerializer.Serialize(writer, value, typeInfo);
        return counter.BytesWritten;
    }

    // An IBufferWriter that throws away what it's given and only tallies the length, so
    // measuring a batch never allocates a copy of it. The scratch buffer is reused across
    // writes because the bytes are never read back.
    sealed class ByteCounter : IBufferWriter<byte>
    {
        byte[] _scratch = new byte[4096];

        public long BytesWritten { get; private set; }

        public void Advance(int count) => BytesWritten += count;

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _scratch;
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _scratch;
        }

        void EnsureCapacity(int sizeHint)
        {
            if (sizeHint > _scratch.Length)
                _scratch = new byte[sizeHint];
        }
    }
}
