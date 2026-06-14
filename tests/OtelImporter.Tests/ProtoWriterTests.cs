using OtelImporter.Otlp;

namespace OtelImporter.Tests;

public class ProtoWriterTests
{
    [Theory]
    [InlineData(0UL, new byte[] { 0x00 })]
    [InlineData(1UL, new byte[] { 0x01 })]
    [InlineData(127UL, new byte[] { 0x7F })]
    [InlineData(128UL, new byte[] { 0x80, 0x01 })]
    [InlineData(300UL, new byte[] { 0xAC, 0x02 })]
    [InlineData(ulong.MaxValue, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01 })]
    public void WriteVarintMatchesKnownEncodings(ulong value, byte[] expected)
    {
        using var writer = new ProtoWriter();
        writer.WriteVarint(value);

        Assert.Equal(expected, writer.WrittenSpan.ToArray());
    }

    [Fact]
    public void WriteFixed64IsLittleEndian()
    {
        using var writer = new ProtoWriter();
        writer.WriteFixed64(7, 0x0102030405060708);

        // tag = field 7, wire type 1 => (7 << 3) | 1 = 0x39
        var expected = new byte[] { 0x39, 0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01 };
        Assert.Equal(expected, writer.WrittenSpan.ToArray());
    }

    [Fact]
    public void WriteFixed32IsLittleEndian()
    {
        using var writer = new ProtoWriter();
        writer.WriteFixed32(16, 257);

        // tag = field 16, wire type 5 => (16 << 3) | 5 = 0x85, 0x01 (varint)
        var expected = new byte[] { 0x85, 0x01, 0x01, 0x01, 0x00, 0x00 };
        Assert.Equal(expected, writer.WrittenSpan.ToArray());
    }

    [Fact]
    public void WriteStringWritesTagLengthAndUtf8()
    {
        using var writer = new ProtoWriter();
        writer.WriteString(5, "POST");

        // tag = field 5, wire type 2 => 0x2A; length 4; "POST"
        var expected = new byte[] { 0x2A, 0x04, (byte)'P', (byte)'O', (byte)'S', (byte)'T' };
        Assert.Equal(expected, writer.WrittenSpan.ToArray());
    }

    [Fact]
    public void ScalarDefaultsAreOmitted()
    {
        using var writer = new ProtoWriter();
        writer.WriteString(1, "");
        writer.WriteString(2, null);
        writer.WriteUInt32(3, 0);
        writer.WriteFixed64(4, 0);
        writer.WriteFixed32(5, 0);
        writer.WriteBool(6, false);
        writer.WriteEnum(7, 0);

        Assert.Equal(0, writer.Length);
    }

    [Fact]
    public void WriteLengthDelimitedRoundTripsViaReader()
    {
        using var inner = new ProtoWriter();
        inner.WriteString(1, "hello");

        using var outer = new ProtoWriter();
        outer.WriteLengthDelimited(2, inner.WrittenSpan);

        var reader = new TestProtoReader(outer.WrittenSpan);
        var (field, wireType) = reader.ReadTag();
        Assert.Equal(2, field);
        Assert.Equal(2, wireType);

        var nested = new TestProtoReader(reader.ReadLengthDelimited());
        var (innerField, _) = nested.ReadTag();
        Assert.Equal(1, innerField);
        Assert.Equal("hello", System.Text.Encoding.UTF8.GetString(nested.ReadLengthDelimited()));
    }
}
