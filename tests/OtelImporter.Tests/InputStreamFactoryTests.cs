using System.Text;
using OtelImporter.Input;
using ZstdSharp;

namespace OtelImporter.Tests;

public class InputStreamFactoryTests : IDisposable
{
    readonly List<string> _tempFiles = [];

    string TempFile(string suffix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"otelimporter-test-{Guid.NewGuid():N}{suffix}");
        _tempFiles.Add(path);
        return path;
    }

    [Fact]
    public void HasZstdMagicDetectsMagicNumber()
    {
        Assert.True(InputStreamFactory.HasZstdMagic([0x28, 0xB5, 0x2F, 0xFD, 0x00]));
        Assert.False(InputStreamFactory.HasZstdMagic("{\"resourceSpans\""u8));
        Assert.False(InputStreamFactory.HasZstdMagic([0x28, 0xB5]));
    }

    [Fact]
    public async Task OpensPlainJsonlFile()
    {
        var path = TempFile(".jsonl");
        await File.WriteAllTextAsync(path, "line1\nline2\n");

        var lines = await ReadLines(path);

        Assert.Equal(["line1", "line2"], lines);
    }

    [Fact]
    public async Task TransparentlyDecompressesZstdFileByMagicNumber()
    {
        // Note: a .jsonl extension but zstd content -- detection is by magic, not extension.
        var path = TempFile(".jsonl");
        var payload = Encoding.UTF8.GetBytes("compressed1\ncompressed2\n");
        using (var compressor = new Compressor())
            await File.WriteAllBytesAsync(path, compressor.Wrap(payload).ToArray());

        var lines = await ReadLines(path);

        Assert.Equal(["compressed1", "compressed2"], lines);
    }

    [Fact]
    public async Task DecompressesLargeZstdContent()
    {
        var path = TempFile(".jsonl.zst");
        var builder = new StringBuilder();
        for (var i = 0; i < 5000; i++)
            builder.Append($"{{\"line\":{i}}}\n");
        var payload = Encoding.UTF8.GetBytes(builder.ToString());
        using (var compressor = new Compressor())
            await File.WriteAllBytesAsync(path, compressor.Wrap(payload).ToArray());

        var lines = await ReadLines(path);

        Assert.Equal(5000, lines.Count);
        Assert.Equal("{\"line\":0}", lines[0]);
        Assert.Equal("{\"line\":4999}", lines[^1]);
    }

    static async Task<List<string>> ReadLines(string path)
    {
        var factory = new InputStreamFactory();
        await using var stream = factory.Open(path);
        var lines = new List<string>();
        await foreach (var line in JsonlLineReader.ReadLinesAsync(stream))
            lines.Add(Encoding.UTF8.GetString(line.Span));
        return lines;
    }

    public void Dispose()
    {
        foreach (var file in _tempFiles)
        {
            try { File.Delete(file); } catch { /* best effort */ }
        }
    }
}
