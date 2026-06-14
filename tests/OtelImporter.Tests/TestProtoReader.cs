using System.Buffers.Binary;

namespace OtelImporter.Tests;

// A tiny independent protobuf reader used to decode and assert against the bytes
// produced by the production (hand-rolled) writer. Deliberately separate code so a
// bug in the writer can't be masked by sharing a reader implementation.
internal ref struct TestProtoReader
{
    readonly ReadOnlySpan<byte> _data;
    int _position;

    public TestProtoReader(ReadOnlySpan<byte> data)
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

    public ulong ReadFixed64()
    {
        var value = BinaryPrimitives.ReadUInt64LittleEndian(_data.Slice(_position, 8));
        _position += 8;
        return value;
    }

    public uint ReadFixed32()
    {
        var value = BinaryPrimitives.ReadUInt32LittleEndian(_data.Slice(_position, 4));
        _position += 4;
        return value;
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
            case 2: ReadLengthDelimited(); break;
            case 5: _position += 4; break;
            default: throw new InvalidOperationException($"Unsupported wire type {wireType}.");
        }
    }
}
