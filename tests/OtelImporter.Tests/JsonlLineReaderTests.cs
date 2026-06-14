using System.Text;
using OtelImporter.Input;

namespace OtelImporter.Tests;

public class JsonlLineReaderTests
{
    static async Task<List<string>> ReadAll(Stream stream)
    {
        var lines = new List<string>();
        await foreach (var line in JsonlLineReader.ReadLinesAsync(stream))
            lines.Add(Encoding.UTF8.GetString(line.Span));
        return lines;
    }

    static MemoryStream Bytes(string text) => new(Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task Splits_lines_on_newline()
    {
        var lines = await ReadAll(Bytes("a\nb\nc\n"));
        Assert.Equal(["a", "b", "c"], lines);
    }

    [Fact]
    public async Task Handles_missing_trailing_newline()
    {
        var lines = await ReadAll(Bytes("a\nb\nc"));
        Assert.Equal(["a", "b", "c"], lines);
    }

    [Fact]
    public async Task Handles_crlf_line_endings()
    {
        var lines = await ReadAll(Bytes("a\r\nb\r\n"));
        Assert.Equal(["a", "b"], lines);
    }

    [Fact]
    public async Task Skips_blank_lines()
    {
        var lines = await ReadAll(Bytes("a\n\n\nb\n"));
        Assert.Equal(["a", "b"], lines);
    }

    [Fact]
    public async Task Empty_stream_yields_nothing()
    {
        var lines = await ReadAll(Bytes(""));
        Assert.Empty(lines);
    }

    [Fact]
    public async Task Reassembles_lines_split_across_read_boundaries()
    {
        // A drip-fed stream (1 byte per read) exercises the partial-line carry-over logic.
        var content = "{\"a\":1}\n{\"b\":2}\n{\"c\":3}";
        var lines = await ReadAll(new ChunkedStream(Encoding.UTF8.GetBytes(content), chunkSize: 1));
        Assert.Equal(["{\"a\":1}", "{\"b\":2}", "{\"c\":3}"], lines);
    }

    [Fact]
    public async Task Preserves_unicode_bytes()
    {
        var lines = await ReadAll(new ChunkedStream(Encoding.UTF8.GetBytes("café\n☃\n"), chunkSize: 2));
        Assert.Equal(["café", "☃"], lines);
    }

    // Stream that returns at most chunkSize bytes per read, simulating network/decompression streams.
    sealed class ChunkedStream(byte[] data, int chunkSize) : Stream
    {
        int _position;

        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = data.Length - _position;
            if (remaining <= 0)
                return 0;

            var toCopy = Math.Min(Math.Min(count, chunkSize), remaining);
            Array.Copy(data, _position, buffer, offset, toCopy);
            _position += toCopy;
            return toCopy;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
