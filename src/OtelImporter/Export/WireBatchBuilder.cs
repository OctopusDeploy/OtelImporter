using OtelImporter.Otlp;

namespace OtelImporter.Export;

// Accumulates spans into one wire frame, grouped under their originating resource and scope,
// and reports the frame's exact serialized size so the batcher can decide when to cut. Spans
// arrive in resource/scope order, so only the most recently opened group is ever appended to.
// Subclasses supply the transport's encoding; the grouping and span bookkeeping are shared.
internal abstract class WireBatchBuilder
{
    protected sealed class ScopeGroup
    {
        public required ScopeSpans Scope { get; init; }
        public List<byte[]> Spans { get; } = [];
    }

    protected sealed class ResourceGroup
    {
        public required ResourceSpans Resource { get; init; }
        public List<ScopeGroup> Scopes { get; } = [];
    }

    protected List<ResourceGroup> Groups { get; } = [];

    public int SpanCount { get; private set; }

    public bool IsEmpty => SpanCount == 0;

    // Serialize one span to its on-the-wire bytes; the result is cached and reused, so each
    // span is encoded exactly once regardless of how the batcher probes the frame size.
    public abstract byte[] SerializeSpan(Span span);

    // Exact serialized size of the frame as it currently stands.
    public long CurrentSize => Assemble().Length;

    public void Add(ResourceSpans resourceSpans, ScopeSpans scopeSpans, byte[] spanWire)
    {
        var resourceGroup = Groups.Count > 0 && ReferenceEquals(Groups[^1].Resource, resourceSpans)
            ? Groups[^1]
            : AddGroup(resourceSpans);

        var scopeGroup = resourceGroup.Scopes.Count > 0 && ReferenceEquals(resourceGroup.Scopes[^1].Scope, scopeSpans)
            ? resourceGroup.Scopes[^1]
            : AddScope(resourceGroup, scopeSpans);

        scopeGroup.Spans.Add(spanWire);
        SpanCount++;
    }

    ResourceGroup AddGroup(ResourceSpans resourceSpans)
    {
        var group = new ResourceGroup { Resource = resourceSpans };
        Groups.Add(group);
        return group;
    }

    static ScopeGroup AddScope(ResourceGroup resourceGroup, ScopeSpans scopeSpans)
    {
        var group = new ScopeGroup { Scope = scopeSpans };
        resourceGroup.Scopes.Add(group);
        return group;
    }

    // Undo the most recent Add, pruning the scope/resource group it may have opened. Lets the
    // batcher add a span speculatively, check the size, and back it out if it overflows.
    public void RemoveLast()
    {
        var resourceGroup = Groups[^1];
        var scopeGroup = resourceGroup.Scopes[^1];

        scopeGroup.Spans.RemoveAt(scopeGroup.Spans.Count - 1);
        if (scopeGroup.Spans.Count == 0)
            resourceGroup.Scopes.RemoveAt(resourceGroup.Scopes.Count - 1);
        if (resourceGroup.Scopes.Count == 0)
            Groups.RemoveAt(Groups.Count - 1);

        SpanCount--;
    }

    // Serialize the current frame and reset for the next one.
    public ReadOnlyMemory<byte> Finish()
    {
        var frame = Assemble();
        Groups.Clear();
        SpanCount = 0;
        return frame;
    }

    // Encode the current groups into the transport's wire format, reusing the cached span bytes.
    protected abstract ReadOnlyMemory<byte> Assemble();
}

// Packs a batch's spans into wire frames that each stay within maxBytes, measuring the real
// serialized size via the supplied builder. Greedy and order-preserving: a span is added to
// the frame in progress while it fits; when it would overflow, the frame is cut and the span
// starts the next one. A span that exceeds the limit even on its own can't be sent, so it is
// dropped and counted.
internal static class SpanBatcher
{
    public static PreparedBatches Pack(ExportTraceServiceRequest request, long maxBytes, WireBatchBuilder builder)
    {
        var frames = new List<ReadOnlyMemory<byte>>();
        long skippedSpanCount = 0;

        foreach (var resourceSpans in request.ResourceSpans ?? [])
        {
            foreach (var scopeSpans in resourceSpans.ScopeSpans ?? [])
            {
                if (scopeSpans.Spans is not { Count: > 0 })
                    continue;

                foreach (var span in scopeSpans.Spans)
                {
                    var wire = builder.SerializeSpan(span);

                    // Try to keep the span in the frame in progress; if it overflows, cut.
                    if (!builder.IsEmpty)
                    {
                        builder.Add(resourceSpans, scopeSpans, wire);
                        if (builder.CurrentSize <= maxBytes)
                            continue;
                        builder.RemoveLast();
                        frames.Add(builder.Finish());
                    }

                    // Start a fresh frame with the span; if it doesn't fit even alone, drop it.
                    builder.Add(resourceSpans, scopeSpans, wire);
                    if (builder.CurrentSize > maxBytes)
                    {
                        builder.RemoveLast();
                        skippedSpanCount++;
                    }
                }
            }
        }

        if (!builder.IsEmpty)
            frames.Add(builder.Finish());

        return new PreparedBatches(frames, skippedSpanCount);
    }
}
