using System.Buffers.Binary;

namespace OtelImporter.Otlp;

// A minimal protobuf wire-format reader, used to decode the collector's
// ExportTraceServiceResponse (to surface partial_success). Mirrors ProtoWriter's
// hand-rolled approach so we avoid the protobuf runtime dependency.
internal ref struct ProtoReader
{
    readonly ReadOnlySpan<byte> _data;
    int _position;

    public ProtoReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _position = 0;
    }

    public bool End => _position >= _data.Length;

    public (int FieldNumber, int WireType) ReadTag()
    {
        var tag = ReadVarint();
        return ((int)(tag >> 3), (int)(tag & 0x7));
    }

    public ulong ReadVarint()
    {
        ulong result = 0;
        var shift = 0;
        while (true)
        {
            var b = _data[_position++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                break;
            shift += 7;
        }
        return result;
    }

    public ReadOnlySpan<byte> ReadLengthDelimited()
    {
        var length = (int)ReadVarint();
        var slice = _data.Slice(_position, length);
        _position += length;
        return slice;
    }

    public void Skip(int wireType)
    {
        switch (wireType)
        {
            case 0: ReadVarint(); break;
            case 1: _position += 8; break;
            case 2: _position += (int)ReadVarint(); break;
            case 5: _position += 4; break;
            default: throw new InvalidOperationException($"Unsupported wire type {wireType}.");
        }
    }

    public static uint ReadFixed32BigEndian(ReadOnlySpan<byte> data) => BinaryPrimitives.ReadUInt32BigEndian(data);
}
