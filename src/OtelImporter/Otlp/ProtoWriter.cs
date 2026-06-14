using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace OtelImporter.Otlp;

// A minimal protobuf wire-format writer. We hand-roll protobuf encoding to avoid
// taking a dependency on Google.Protobuf / Grpc.Tools, which are heavyweight and
// add AOT/trim friction. The buffer is rented from the shared ArrayPool so a
// serializer can cheaply spin up temporary writers for nested (length-delimited)
// messages and return them when done.
//
// Wire types (https://protobuf.dev/programming-guides/encoding/):
//   0 = varint, 1 = 64-bit fixed, 2 = length-delimited, 5 = 32-bit fixed
internal sealed class ProtoWriter : IDisposable
{
    const int WireVarint = 0;
    const int WireFixed64 = 1;
    const int WireLengthDelimited = 2;
    const int WireFixed32 = 5;

    byte[] _buffer;
    int _position;

    public ProtoWriter(int initialCapacity = 256)
    {
        _buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);
    }

    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _position);

    public int Length => _position;

    void EnsureCapacity(int additional)
    {
        if (_position + additional <= _buffer.Length)
            return;

        var newSize = _buffer.Length * 2;
        while (newSize < _position + additional)
            newSize *= 2;

        var grown = ArrayPool<byte>.Shared.Rent(newSize);
        Array.Copy(_buffer, grown, _position);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = grown;
    }

    public void WriteRawByte(byte value)
    {
        EnsureCapacity(1);
        _buffer[_position++] = value;
    }

    public void WriteRawBytes(ReadOnlySpan<byte> value)
    {
        EnsureCapacity(value.Length);
        value.CopyTo(_buffer.AsSpan(_position));
        _position += value.Length;
    }

    public void WriteVarint(ulong value)
    {
        EnsureCapacity(10); // a varint is at most 10 bytes
        while (value >= 0x80)
        {
            _buffer[_position++] = (byte)(value | 0x80);
            value >>= 7;
        }
        _buffer[_position++] = (byte)value;
    }

    void WriteTag(int fieldNumber, int wireType) => WriteVarint(((ulong)fieldNumber << 3) | (uint)wireType);

    // proto3 omits scalar fields that hold their default value. The Write* helpers
    // below follow that convention so the output matches a canonical encoding.

    public void WriteString(int fieldNumber, string? value)
    {
        if (string.IsNullOrEmpty(value))
            return;

        var byteCount = Encoding.UTF8.GetByteCount(value);
        WriteTag(fieldNumber, WireLengthDelimited);
        WriteVarint((ulong)byteCount);
        EnsureCapacity(byteCount);
        Encoding.UTF8.GetBytes(value, _buffer.AsSpan(_position));
        _position += byteCount;
    }

    public void WriteBytes(int fieldNumber, ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty)
            return;

        WriteTag(fieldNumber, WireLengthDelimited);
        WriteVarint((ulong)value.Length);
        WriteRawBytes(value);
    }

    public void WriteLengthDelimited(int fieldNumber, ReadOnlySpan<byte> message)
    {
        // Unlike scalars, embedded messages are written even when empty so that
        // presence is preserved.
        WriteTag(fieldNumber, WireLengthDelimited);
        WriteVarint((ulong)message.Length);
        WriteRawBytes(message);
    }

    public void WriteUInt32(int fieldNumber, uint value)
    {
        if (value == 0)
            return;

        WriteTag(fieldNumber, WireVarint);
        WriteVarint(value);
    }

    public void WriteInt64AsVarint(int fieldNumber, long value)
    {
        if (value == 0)
            return;

        WriteTag(fieldNumber, WireVarint);
        WriteVarint((ulong)value);
    }

    public void WriteEnum(int fieldNumber, int value)
    {
        if (value == 0)
            return;

        WriteTag(fieldNumber, WireVarint);
        WriteVarint((ulong)(uint)value);
    }

    public void WriteBool(int fieldNumber, bool value)
    {
        if (!value)
            return;

        WriteTag(fieldNumber, WireVarint);
        WriteVarint(1);
    }

    public void WriteFixed64(int fieldNumber, ulong value)
    {
        if (value == 0)
            return;

        WriteTag(fieldNumber, WireFixed64);
        EnsureCapacity(8);
        BinaryPrimitives.WriteUInt64LittleEndian(_buffer.AsSpan(_position), value);
        _position += 8;
    }

    public void WriteFixed32(int fieldNumber, uint value)
    {
        if (value == 0)
            return;

        WriteTag(fieldNumber, WireFixed32);
        EnsureCapacity(4);
        BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(_position), value);
        _position += 4;
    }

    public void WriteDouble(int fieldNumber, double value)
    {
        if (value == 0)
            return;

        WriteTag(fieldNumber, WireFixed64);
        EnsureCapacity(8);
        BinaryPrimitives.WriteUInt64LittleEndian(_buffer.AsSpan(_position), BitConverter.DoubleToUInt64Bits(value));
        _position += 8;
    }

    public void Dispose()
    {
        if (_buffer.Length == 0)
            return;

        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = [];
        _position = 0;
    }
}
