using ZstdSharp;

namespace OtelImporter.Input;

// Opens trace files for streaming reads. zstd-compressed input is detected by its
// 4-byte magic number (and, as a fallback, the .zst extension) and wrapped in a
// streaming decompressor so arbitrarily large files never need to be buffered in
// full. Plain .jsonl is read directly.
internal sealed class InputStreamFactory : IInputStreamFactory
{
    // zstd frame magic number: 0xFD2FB528, little-endian on disk.
    static readonly byte[] ZstdMagic = [0x28, 0xB5, 0x2F, 0xFD];

    const int FileBufferSize = 1 << 16;

    public Stream Open(string path)
    {
        var fileStream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            FileBufferSize,
            FileOptions.SequentialScan);

        try
        {
            if (LooksLikeZstd(fileStream, path))
                return new DecompressionStream(fileStream);

            return fileStream;
        }
        catch
        {
            fileStream.Dispose();
            throw;
        }
    }

    static bool LooksLikeZstd(FileStream stream, string path)
    {
        Span<byte> header = stackalloc byte[ZstdMagic.Length];
        var read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        stream.Position = 0;

        if (read == ZstdMagic.Length && header.SequenceEqual(ZstdMagic))
            return true;

        // Fall back to the extension when the file is too short to sniff.
        return read < ZstdMagic.Length
               && path.EndsWith(".zst", StringComparison.OrdinalIgnoreCase);
    }

    public static bool HasZstdMagic(ReadOnlySpan<byte> header) =>
        header.Length >= ZstdMagic.Length && header[..ZstdMagic.Length].SequenceEqual(ZstdMagic);
}
