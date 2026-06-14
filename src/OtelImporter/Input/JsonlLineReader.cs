using System.Buffers;
using System.Runtime.CompilerServices;

namespace OtelImporter.Input;

// Streams a JSONL stream into one ReadOnlyMemory<byte> per non-empty line, working
// at the byte level so the raw UTF-8 of each line is preserved (the HTTP exporter
// forwards these bytes verbatim, no re-encoding). Lines are read incrementally so
// the whole file is never held in memory; only a single line plus the current read
// chunk is buffered at a time.
internal static class JsonlLineReader
{
    const byte LineFeed = (byte)'\n';
    const byte CarriageReturn = (byte)'\r';
    const int ReadBufferSize = 1 << 16;

    public static async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadLinesAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var readBuffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize);
        var pending = new ArrayBufferWriter<byte>(256);
        var completed = new List<byte[]>();

        try
        {
            int bytesRead;
            while ((bytesRead = await stream.ReadAsync(readBuffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                // Span work happens inside this synchronous call so nothing is held across the yields below.
                ExtractLines(readBuffer.AsSpan(0, bytesRead), pending, completed);

                foreach (var line in completed)
                    yield return line;
                completed.Clear();
            }

            // Emit any trailing line that was not newline-terminated.
            var tail = TrimCarriageReturn(pending.WrittenSpan);
            if (!tail.IsEmpty)
                yield return tail.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(readBuffer);
        }
    }

    // Splits a freshly-read chunk into complete lines (appended to <paramref name="completed"/>
    // as owned copies), carrying any unterminated remainder into <paramref name="pending"/>.
    static void ExtractLines(ReadOnlySpan<byte> chunk, ArrayBufferWriter<byte> pending, List<byte[]> completed)
    {
        var lineStart = 0;
        for (var i = 0; i < chunk.Length; i++)
        {
            if (chunk[i] != LineFeed)
                continue;

            var segment = chunk[lineStart..i];
            ReadOnlySpan<byte> line;
            if (pending.WrittenCount == 0)
            {
                line = TrimCarriageReturn(segment);
            }
            else
            {
                pending.Write(segment);
                line = TrimCarriageReturn(pending.WrittenSpan);
            }

            if (!line.IsEmpty)
                completed.Add(line.ToArray());

            pending.Clear();
            lineStart = i + 1;
        }

        if (lineStart < chunk.Length)
            pending.Write(chunk[lineStart..]);
    }

    static ReadOnlySpan<byte> TrimCarriageReturn(ReadOnlySpan<byte> line) =>
        line.Length > 0 && line[^1] == CarriageReturn ? line[..^1] : line;
}
