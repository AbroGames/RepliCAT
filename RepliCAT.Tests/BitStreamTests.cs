using Godot;
using RepliCAT;
using RepliCAT.Bits;

namespace RepliCAT.Tests;

public class BitStreamTests
{
    [Fact]
    public void RepliCATAndGodotTypes_LoadWithoutEngine()
    {
        Vector2 vector = new Vector2(3f, 4f);

        Assert.Equal(5f, vector.Length(), 5);
        Assert.Equal(new Vector2(6f, 8f), vector * 2);
        Assert.Equal(ReplicationLimits.DefaultMaxDepth, new ReplicationLimits().MaxDepth);
    }

    [Fact]
    public void Limits_HaveDocumentedDefaults()
    {
        var limits = new ReplicationLimits();

        Assert.Equal(64, limits.MaxDepth);
        Assert.Equal(65536, limits.MaxCollectionCount);
    }

    [Fact]
    public void FormatException_IsReplicationException()
    {
        Assert.IsAssignableFrom<ReplicationException>(new ReplicationFormatException("x"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(13)]
    public void Bits_AllWidths_RoundTripAtOffset(int offset)
    {
        var random = new Random(12345 + offset);
        var writer = new BitWriter(1);
        ulong prefix = NextULong(random) & Mask(offset);
        writer.WriteBits(prefix, offset);

        var expected = new ulong[65];
        for (int width = 0; width <= 64; width++)
        {
            expected[width] = NextULong(random) & Mask(width);
            // Лишние старшие биты значения должны игнорироваться
            writer.WriteBits(expected[width] | ~Mask(width), width);
        }

        int totalBits = offset + Enumerable.Range(0, 65).Sum();
        Assert.Equal(totalBits, writer.BitPosition);
        Assert.Equal((totalBits + 7) / 8, writer.ByteLength);

        byte[] data = writer.ToArray();
        var reader = new BitReader(data);
        Assert.Equal(prefix, reader.ReadBits(offset));
        for (int width = 0; width <= 64; width++)
        {
            Assert.Equal(expected[width], reader.ReadBits(width));
        }

        Assert.Equal(totalBits, reader.BitPosition);
        Assert.True(reader.RemainingBits < 8);
    }

    [Fact]
    public void Bits_AreLsbFirst()
    {
        var writer = new BitWriter();
        writer.WriteBits(0b101, 3);
        writer.WriteBits(0b11111, 5);
        writer.WriteBits(0x1, 1);

        Assert.Equal(new byte[] { 0b11111101, 0b00000001 }, writer.ToArray());
    }

    [Fact]
    public void Bits_InvalidWidth_Throws()
    {
        var writer = new BitWriter();

        Assert.Throws<ArgumentOutOfRangeException>(() => writer.WriteBits(0, 65));
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.WriteBits(0, -1));
    }

    [Fact]
    public void Bools_RoundTrip()
    {
        bool[] values = { true, false, false, true, true, true, false, true, false, true, true };
        var writer = new BitWriter();
        foreach (bool value in values)
        {
            writer.WriteBool(value);
        }

        Assert.Equal(values.Length, writer.BitPosition);
        Assert.Equal(2, writer.ByteLength);

        var reader = new BitReader(writer.ToArray());
        foreach (bool value in values)
        {
            Assert.Equal(value, reader.ReadBool());
        }
    }

    [Theory]
    [InlineData(0UL, 1)]
    [InlineData(1UL, 1)]
    [InlineData(127UL, 1)]
    [InlineData(128UL, 2)]
    [InlineData(16383UL, 2)]
    [InlineData(16384UL, 3)]
    [InlineData(uint.MaxValue, 5)]
    [InlineData(long.MaxValue, 9)]
    [InlineData(ulong.MaxValue, 10)]
    public void VarUInt_RoundTrip(ulong value, int expectedGroups)
    {
        foreach (int offset in new[] { 0, 5 })
        {
            var writer = new BitWriter();
            writer.WriteBits(0, offset);
            writer.WriteVarUInt(value);

            Assert.Equal(offset + expectedGroups * 8, writer.BitPosition);

            var reader = new BitReader(writer.ToArray());
            reader.ReadBits(offset);
            Assert.Equal(value, reader.ReadVarUInt());
            Assert.Equal(offset + expectedGroups * 8, reader.BitPosition);
        }
    }

    [Theory]
    [InlineData(0L, 1)]
    [InlineData(-1L, 1)]
    [InlineData(1L, 1)]
    [InlineData(63L, 1)]
    [InlineData(-64L, 1)]
    [InlineData(64L, 2)]
    [InlineData(-65L, 2)]
    [InlineData(127L, 2)]
    [InlineData(128L, 2)]
    [InlineData(-128L, 2)]
    [InlineData(int.MinValue, 5)]
    [InlineData(int.MaxValue, 5)]
    [InlineData(long.MaxValue, 10)]
    [InlineData(long.MinValue, 10)]
    public void VarInt_RoundTrip(long value, int expectedGroups)
    {
        var writer = new BitWriter();
        writer.WriteBool(true);
        writer.WriteVarInt(value);

        Assert.Equal(1 + expectedGroups * 8, writer.BitPosition);

        var reader = new BitReader(writer.ToArray());
        Assert.True(reader.ReadBool());
        Assert.Equal(value, reader.ReadVarInt());
    }

    [Theory]
    [InlineData(0L, 0UL)]
    [InlineData(-1L, 1UL)]
    [InlineData(1L, 2UL)]
    [InlineData(-2L, 3UL)]
    [InlineData(long.MaxValue, ulong.MaxValue - 1)]
    [InlineData(long.MinValue, ulong.MaxValue)]
    public void ZigZag_MatchesSpec(long value, ulong encoded)
    {
        Assert.Equal(encoded, BitWriter.ZigZagEncode(value));
        Assert.Equal(value, BitReader.ZigZagDecode(encoded));
    }

    [Fact]
    public void Floats_RoundTripBitExact()
    {
        float[] floats =
        {
            0f, -0f, 1.5f, -123.456f, float.NaN, float.PositiveInfinity, float.NegativeInfinity,
            float.Epsilon, float.MaxValue, float.MinValue, BitConverter.UInt32BitsToSingle(0x7FC00001)
        };
        double[] doubles =
        {
            0d, -0d, 1.5d, -123.456d, double.NaN, double.PositiveInfinity, double.NegativeInfinity,
            double.Epsilon, double.MaxValue, double.MinValue, BitConverter.UInt64BitsToDouble(0x7FF8000000000001)
        };

        var writer = new BitWriter();
        writer.WriteBits(1, 3);
        foreach (float value in floats)
        {
            writer.WriteSingle(value);
        }
        foreach (double value in doubles)
        {
            writer.WriteDouble(value);
        }

        Assert.Equal(3 + floats.Length * 32 + doubles.Length * 64, writer.BitPosition);

        var reader = new BitReader(writer.ToArray());
        reader.ReadBits(3);
        foreach (float value in floats)
        {
            Assert.Equal(BitConverter.SingleToUInt32Bits(value), BitConverter.SingleToUInt32Bits(reader.ReadSingle()));
        }
        foreach (double value in doubles)
        {
            Assert.Equal(BitConverter.DoubleToUInt64Bits(value), BitConverter.DoubleToUInt64Bits(reader.ReadDouble()));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    public void Bytes_RoundTrip(int offset)
    {
        byte[] payload = new byte[300];
        new Random(offset).NextBytes(payload);

        var writer = new BitWriter(2);
        writer.WriteBits(ulong.MaxValue, offset);
        writer.WriteBytes(ReadOnlySpan<byte>.Empty);
        writer.WriteBytes(payload);
        writer.WriteBool(true);

        Assert.Equal(offset + payload.Length * 8 + 1, writer.BitPosition);

        var reader = new BitReader(writer.ToArray());
        Assert.Equal(Mask(offset), reader.ReadBits(offset));
        byte[] read = new byte[payload.Length];
        reader.ReadBytes(read);
        Assert.Equal(payload, read);
        Assert.True(reader.ReadBool());
    }

    [Fact]
    public void Rewind_ThenWrite_ProducesSameBytesAsWithoutDiscardedWrites()
    {
        var expectedWriter = new BitWriter();
        WritePrefix(expectedWriter);
        WriteSuffix(expectedWriter);
        byte[] expected = expectedWriter.ToArray();

        var writer = new BitWriter(1);
        WritePrefix(writer);
        int mark = writer.BitPosition;
        // Отбрасываемые данные из единиц, чтобы грязные биты точно остались в буфере
        writer.WriteBits(ulong.MaxValue, 64);
        writer.WriteBytes(Enumerable.Repeat((byte)0xFF, 40).ToArray());
        writer.WriteVarUInt(ulong.MaxValue);
        writer.Rewind(mark);
        Assert.Equal(mark, writer.BitPosition);
        WriteSuffix(writer);

        Assert.Equal(expected, writer.ToArray());
        Assert.True(expected.AsSpan().SequenceEqual(writer.AsSpan()));
    }

    [Fact]
    public void Rewind_ToCurrentOrShorter_WithoutNewWrites_TruncatesData()
    {
        var writer = new BitWriter();
        writer.WriteBits(0b1011, 4);
        writer.WriteBits(0xFFFF, 16);
        writer.Rewind(4);

        Assert.Equal(1, writer.ByteLength);
        Assert.Equal(new byte[] { 0b1011 }, writer.ToArray());

        writer.Rewind(4);
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.Rewind(5));
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.Rewind(-1));
    }

    [Fact]
    public void SetBit_PatchesWrittenBits()
    {
        var writer = new BitWriter();
        writer.WriteBits(0, 3);
        int maskStart = writer.BitPosition;
        writer.WriteBits(0, 10); // резерв под маску
        writer.WriteBits(0xABCD, 16);

        writer.SetBit(maskStart + 0, true);
        writer.SetBit(maskStart + 5, true);
        writer.SetBit(maskStart + 9, true);
        writer.SetBit(maskStart + 5, false);
        writer.SetBit(maskStart + 7, true);

        var reader = new BitReader(writer.ToArray());
        Assert.Equal(0UL, reader.ReadBits(3));
        Assert.Equal((1UL << 0) | (1UL << 7) | (1UL << 9), reader.ReadBits(10));
        Assert.Equal(0xABCDUL, reader.ReadBits(16));

        Assert.Throws<ArgumentOutOfRangeException>(() => writer.SetBit(writer.BitPosition, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.SetBit(-1, true));
    }

    [Fact]
    public void TrailingBits_AreZeroed()
    {
        var writer = new BitWriter();
        writer.WriteBits(ulong.MaxValue, 16);
        writer.Rewind(3);

        Assert.Equal(new byte[] { 0b111 }, writer.ToArray());
        Assert.Equal(new byte[] { 0b111 }, writer.AsSpan().ToArray());

        writer.WriteBits(ulong.MaxValue, 9);
        writer.Rewind(11);
        Assert.Equal(new byte[] { 0xFF, 0b111 }, writer.ToArray());
    }

    [Fact]
    public void Reset_StartsFromScratch()
    {
        var writer = new BitWriter();
        writer.WriteBits(ulong.MaxValue, 64);
        writer.Reset();

        Assert.Equal(0, writer.BitPosition);
        Assert.Equal(0, writer.ByteLength);
        Assert.Empty(writer.ToArray());

        writer.WriteBits(0b10, 2);
        Assert.Equal(new byte[] { 0b10 }, writer.ToArray());
    }

    [Fact]
    public void Writer_GrowsBeyondInitialCapacity()
    {
        var writer = new BitWriter(0);
        for (int i = 0; i < 10000; i++)
        {
            writer.WriteVarUInt((ulong)i);
        }

        var reader = new BitReader(writer.ToArray());
        for (int i = 0; i < 10000; i++)
        {
            Assert.Equal((ulong)i, reader.ReadVarUInt());
        }
        Assert.Equal(0, reader.RemainingBits);
    }

    [Fact]
    public void Reader_EmptyData_HasNoBits()
    {
        var reader = new BitReader(ReadOnlySpan<byte>.Empty);

        Assert.Equal(0, reader.RemainingBits);
        Assert.Equal(0UL, reader.ReadBits(0));
    }

    [Fact]
    public void Overrun_Throws()
    {
        byte[] data = { 0xFF, 0x01 };

        Assert.Throws<ReplicationFormatException>(() => new BitReader(data).ReadBits(17));
        Assert.Throws<ReplicationFormatException>(() =>
        {
            var reader = new BitReader(data);
            reader.ReadBits(10);
            reader.ReadBits(7);
        });
        Assert.Throws<ReplicationFormatException>(() => new BitReader(data).ReadSingle());
        Assert.Throws<ReplicationFormatException>(() => new BitReader(new byte[7]).ReadDouble());
        Assert.Throws<ReplicationFormatException>(() => new BitReader(data).ReadBytes(new byte[3]));
        Assert.Throws<ReplicationFormatException>(() =>
        {
            var reader = new BitReader(data);
            reader.ReadBool();
            reader.ReadBytes(new byte[2]);
        });
        Assert.Throws<ReplicationFormatException>(() => new BitReader(ReadOnlySpan<byte>.Empty).ReadBool());
        // Незавершенный varuint: бит продолжения установлен, а данных больше нет
        Assert.Throws<ReplicationFormatException>(() => new BitReader(new byte[] { 0x80, 0x80 }).ReadVarUInt());
    }

    [Fact]
    public void Overrun_DoesNotMovePosition()
    {
        var reader = new BitReader(new byte[] { 0xFF });
        reader.ReadBits(5);

        try
        {
            reader.ReadBits(4);
            Assert.Fail("Expected ReplicationFormatException");
        }
        catch (ReplicationFormatException)
        {
        }

        Assert.Equal(5, reader.BitPosition);
        Assert.Equal(3, reader.RemainingBits);
    }

    [Fact]
    public void MalformedVarUInt_Throws()
    {
        // 11 групп с битом продолжения
        byte[] tooLong = Enumerable.Repeat((byte)0xFF, 11).Append((byte)0x00).ToArray();
        Assert.Throws<ReplicationFormatException>(() => new BitReader(tooLong).ReadVarUInt());
        Assert.Throws<ReplicationFormatException>(() => new BitReader(tooLong).ReadVarInt());

        // 10 групп, но последняя несет больше одного бита данных (переполнение 64 бит)
        byte[] overflow = Enumerable.Repeat((byte)0xFF, 9).Append((byte)0x02).ToArray();
        Assert.Throws<ReplicationFormatException>(() => new BitReader(overflow).ReadVarUInt());

        // 10 групп с максимальным допустимым значением читаются корректно
        byte[] max = Enumerable.Repeat((byte)0xFF, 9).Append((byte)0x01).ToArray();
        Assert.Equal(ulong.MaxValue, new BitReader(max).ReadVarUInt());
    }

    [Fact]
    public void Reader_InvalidWidth_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BitReader(new byte[16]).ReadBits(65));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BitReader(new byte[16]).ReadBits(-1));
    }

    [Fact]
    public void RandomMixedStream_RoundTrips()
    {
        var random = new Random(42);
        var ops = new List<(int Kind, ulong Value, int Width)>();
        var writer = new BitWriter(1);

        for (int i = 0; i < 5000; i++)
        {
            int kind = random.Next(4);
            int width = random.Next(65);
            ulong value = NextULong(random);
            switch (kind)
            {
                case 0:
                    value &= Mask(width);
                    writer.WriteBits(value, width);
                    break;
                case 1:
                    value >>= random.Next(64);
                    writer.WriteVarUInt(value);
                    break;
                case 2:
                    value = (ulong)((long)value >> random.Next(64));
                    writer.WriteVarInt((long)value);
                    break;
                default:
                    value &= 1;
                    writer.WriteBool(value != 0);
                    break;
            }
            ops.Add((kind, value, width));
        }

        var reader = new BitReader(writer.ToArray());
        foreach (var (kind, value, width) in ops)
        {
            switch (kind)
            {
                case 0:
                    Assert.Equal(value, reader.ReadBits(width));
                    break;
                case 1:
                    Assert.Equal(value, reader.ReadVarUInt());
                    break;
                case 2:
                    Assert.Equal((long)value, reader.ReadVarInt());
                    break;
                default:
                    Assert.Equal(value != 0, reader.ReadBool());
                    break;
            }
        }

        Assert.Equal(writer.BitPosition, reader.BitPosition);
    }

    private static void WritePrefix(BitWriter writer)
    {
        writer.WriteBits(0b101, 3);
        writer.WriteVarInt(-77);
        writer.WriteBool(false);
    }

    private static void WriteSuffix(BitWriter writer)
    {
        writer.WriteBits(0, 13);
        writer.WriteSingle(1.25f);
        writer.WriteBytes(new byte[] { 0, 1, 2, 0 });
        writer.WriteBits(0b10, 2);
    }

    private static ulong Mask(int width)
    {
        return width >= 64 ? ulong.MaxValue : (1UL << width) - 1;
    }

    private static ulong NextULong(Random random)
    {
        return (ulong)random.NextInt64() ^ ((ulong)random.Next(2) << 63);
    }
}
