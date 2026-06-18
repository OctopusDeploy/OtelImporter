using OtelImporter.Otlp;

namespace OtelImporter.Pipeline;

// Appends a fixed set of attributes to every span in a batch. The attribute list is
// built once up front (the automatic log.file.name plus any --attribute values) and
// the same KeyValue instances are shared across all spans -- they are only ever read
// during serialization -- so enrichment adds no per-span allocations regardless of how
// large the file is. Attributes are appended; existing span attributes are left as-is.
internal sealed class SpanEnricher
{
    readonly List<KeyValue> _attributes;

    public SpanEnricher(IEnumerable<KeyValue> attributes)
    {
        _attributes = [.. attributes];
    }

    public void Enrich(ExportTraceServiceRequest request)
    {
        if (_attributes.Count == 0 || request.ResourceSpans is null)
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
                    (span.Attributes ??= []).AddRange(_attributes);
            }
        }
    }

    // Builds the enricher from the resolved options: the automatic log.file.name (unless
    // suppressed) followed by any --attribute values, all as string-valued attributes.
    public static SpanEnricher Create(string? logFileName, IEnumerable<KeyValuePair<string, string>> attributes)
    {
        var list = new List<KeyValue>();
        if (logFileName is not null)
            list.Add(StringAttribute("log.file.name", logFileName));
        foreach (var (key, value) in attributes)
            list.Add(StringAttribute(key, value));
        return new SpanEnricher(list);
    }

    static KeyValue StringAttribute(string key, string value) =>
        new() { Key = key, Value = new AnyValue { StringValue = value } };
}
