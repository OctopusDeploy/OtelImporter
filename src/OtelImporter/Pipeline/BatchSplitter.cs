using System.Buffers;
using System.Text.Json;
using OtelImporter.Otlp;

namespace OtelImporter.Pipeline;

// Outcome of splitting one batch: the resulting batches (each serialising to within the
// limit, best effort) plus the number of spans dropped because they exceeded the limit on
// their own and so could not be placed in any batch.
internal sealed record SplitResult(IReadOnlyList<ExportTraceServiceRequest> Batches, long SkippedSpanCount);

// Splits an oversized OTLP batch into several smaller ones so each stays within a byte
// budget (measured as OTLP/JSON, the representation the input files use). Spans are the
// unit of division; each output batch reproduces the resource/scope wrappers of the spans
// it carries, so the data model is preserved. A single span larger than the budget can't
// be placed anywhere, so it is dropped and counted in SkippedSpanCount.
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

        foreach (var resourceSpans in request.ResourceSpans ?? [])
        {
            foreach (var scopeSpans in resourceSpans.ScopeSpans ?? [])
            {
                if (scopeSpans.Spans is not { Count: > 0 })
                    continue;

                // Size of a batch holding this resource+scope but no spans yet; every output
                // batch for this scope carries that fixed overhead.
                var baseline = Measure(Wrap(resourceSpans, scopeSpans, []), OtlpJsonContext.Default.ExportTraceServiceRequest);

                var current = new List<Span>();
                long currentSpanBytes = 0; // serialized span bytes plus the commas between them

                foreach (var span in scopeSpans.Spans)
                {
                    var spanBytes = Measure(span, OtlpJsonContext.Default.Span);

                    // Even alone this span overflows the budget, so it can never be sent.
                    if (baseline + spanBytes > maxBytes)
                    {
                        skippedSpanCount++;
                        continue;
                    }

                    var comma = current.Count > 0 ? 1 : 0;
                    if (current.Count > 0 && baseline + currentSpanBytes + comma + spanBytes > maxBytes)
                    {
                        batches.Add(Wrap(resourceSpans, scopeSpans, current));
                        current = [];
                        currentSpanBytes = 0;
                        comma = 0;
                    }

                    current.Add(span);
                    currentSpanBytes += comma + spanBytes;
                }

                if (current.Count > 0)
                    batches.Add(Wrap(resourceSpans, scopeSpans, current));
            }
        }

        return new SplitResult(batches, skippedSpanCount);
    }

    // A one-resource, one-scope batch carrying the given spans, mirroring the originals'
    // resource/scope metadata so the split output is indistinguishable bar the grouping.
    static ExportTraceServiceRequest Wrap(ResourceSpans resourceSpans, ScopeSpans scopeSpans, List<Span> spans) =>
        new()
        {
            ResourceSpans =
            [
                new ResourceSpans
                {
                    Resource = resourceSpans.Resource,
                    SchemaUrl = resourceSpans.SchemaUrl,
                    ScopeSpans =
                    [
                        new ScopeSpans { Scope = scopeSpans.Scope, SchemaUrl = scopeSpans.SchemaUrl, Spans = spans },
                    ],
                },
            ],
        };

    // Serialized UTF-8 byte length, without retaining the bytes.
    static long Measure<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
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
