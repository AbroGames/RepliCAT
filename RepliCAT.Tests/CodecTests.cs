using System.Text;
using Godot;
using RepliCAT;
using RepliCAT.Bits;
using RepliCAT.Codecs;

namespace RepliCAT.Tests;

public class CodecTests
{
    private enum Small
    {
        A,
        B,
        C,
        D
    }

    private enum OneValue
    {
        Only
    }

    private enum Empty
    {
    }

    private enum Sparse : byte
    {
        Low = 1,
        High = 200
    }

    private enum Negative : short
    {
        Minus = -5,
        Zero = 0,
        Plus = 7
    }

    [Flags]
    private enum Options : ushort
    {
        None = 0,
        First = 1,
        Second = 2
    }

    private enum Wide : long
    {
        Zero = 0,
        Big = 1L << 40
    }

    private enum HugeUnsigned : ulong
    {
        Max = ulong.MaxValue
    }

    private sealed class Unsupported
    {
    }

    private struct CustomStruct
    {
        public int Value;
    }

    private sealed class ConstantIntCodec : IReplicationCodec<int>
    {
        public void Write(BitWriter writer, int value)
        {
            writer.WriteBits(1, 2);
        }

        public int Read(ref BitReader reader)
        {
            reader.ReadBits(2);
            return 42;
        }

        public bool IsChanged(int lastSent, int current)
        {
            return false;
        }
    }

    private static readonly ReplicationCodecs Codecs = new();

    // ---------- helpers ----------

    private static IReplicationCodec<T> Get<T>()
    {
        Assert.True(Codecs.TryGet(out IReplicationCodec<T> codec), $"No codec for {typeof(T)}");
        return codec;
    }

    private static T RoundTrip<T>(IReplicationCodec<T> codec, T value, out int bitCount)
    {
        var writer = new BitWriter(1);
        // Смещение, чтобы проверить работу с невыровненными данными
        writer.WriteBits(0b101, 3);
        codec.Write(writer, value);
        bitCount = writer.BitPosition - 3;
        writer.WriteBool(true);

        var reader = new BitReader(writer.ToArray());
        Assert.Equal(0b101UL, reader.ReadBits(3));
        T result = codec.Read(ref reader);
        Assert.Equal(3 + bitCount, reader.BitPosition);
        Assert.True(reader.ReadBool());
        return result;
    }

    private static T RoundTrip<T>(T value)
    {
        return RoundTrip(Get<T>(), value, out _);
    }

    private static void AssertRoundTrip<T>(T value, int expectedBits)
    {
        T result = RoundTrip(Get<T>(), value, out int bits);
        Assert.Equal(value, result);
        Assert.Equal(expectedBits, bits);
    }

    // ---------- primitives ----------

    [Fact]
    public void Bool_RoundTripsInOneBit()
    {
        AssertRoundTrip(true, 1);
        AssertRoundTrip(false, 1);
    }

    [Fact]
    public void Integers_RoundTripAtFullWidth()
    {
        foreach (byte v in new byte[] { 0, 1, 127, 128, byte.MaxValue })
        {
            AssertRoundTrip(v, 8);
        }

        foreach (sbyte v in new sbyte[] { 0, 1, -1, sbyte.MinValue, sbyte.MaxValue })
        {
            AssertRoundTrip(v, 8);
        }

        foreach (short v in new short[] { 0, 1, -1, short.MinValue, short.MaxValue })
        {
            AssertRoundTrip(v, 16);
        }

        foreach (ushort v in new ushort[] { 0, 1, ushort.MaxValue })
        {
            AssertRoundTrip(v, 16);
        }

        foreach (int v in new[] { 0, 1, -1, int.MinValue, int.MaxValue })
        {
            AssertRoundTrip(v, 32);
        }

        foreach (uint v in new[] { 0u, 1u, uint.MaxValue })
        {
            AssertRoundTrip(v, 32);
        }

        foreach (long v in new[] { 0L, 1L, -1L, long.MinValue, long.MaxValue })
        {
            AssertRoundTrip(v, 64);
        }

        foreach (ulong v in new[] { 0UL, 1UL, ulong.MaxValue })
        {
            AssertRoundTrip(v, 64);
        }

        foreach (char v in new[] { '\0', 'a', 'Я', '￿' })
        {
            AssertRoundTrip(v, 16);
        }
    }

    [Fact]
    public void Integers_IsChanged_ComparesValues()
    {
        Assert.False(Get<int>().IsChanged(5, 5));
        Assert.True(Get<int>().IsChanged(5, 6));
        Assert.False(Get<long>().IsChanged(-1, -1));
        Assert.True(Get<ulong>().IsChanged(0, ulong.MaxValue));
        Assert.True(Get<bool>().IsChanged(false, true));
        Assert.False(Get<bool>().IsChanged(true, true));
        Assert.True(Get<char>().IsChanged('a', 'b'));
        Assert.False(Get<byte>().IsChanged(3, 3));
        Assert.True(Get<sbyte>().IsChanged(-3, 3));
        Assert.True(Get<short>().IsChanged(1, 2));
        Assert.False(Get<ushort>().IsChanged(9, 9));
        Assert.True(Get<uint>().IsChanged(1, 2));
    }

    [Fact]
    public void Floats_RoundTripRawBits()
    {
        foreach (float v in new[] { 0f, -0f, 1.5f, -3.25f, float.NaN, float.PositiveInfinity, float.NegativeInfinity, float.Epsilon, float.MaxValue })
        {
            float result = RoundTrip(Get<float>(), v, out int bits);
            Assert.Equal(32, bits);
            Assert.Equal(BitConverter.SingleToUInt32Bits(v), BitConverter.SingleToUInt32Bits(result));
        }

        foreach (double v in new[] { 0d, -0d, 1.5d, -3.25d, double.NaN, double.PositiveInfinity, double.NegativeInfinity, double.Epsilon, double.MaxValue })
        {
            double result = RoundTrip(Get<double>(), v, out int bits);
            Assert.Equal(64, bits);
            Assert.Equal(BitConverter.DoubleToUInt64Bits(v), BitConverter.DoubleToUInt64Bits(result));
        }
    }

    [Fact]
    public void Floats_IsChanged_UsesEqualsSemantics()
    {
        IReplicationCodec<float> f = Get<float>();
        Assert.False(f.IsChanged(float.NaN, float.NaN));
        Assert.True(f.IsChanged(float.NaN, 1f));
        Assert.True(f.IsChanged(1f, float.NaN));
        Assert.False(f.IsChanged(0f, -0f));
        Assert.False(f.IsChanged(2.5f, 2.5f));
        Assert.True(f.IsChanged(2.5f, 2.50001f));
        Assert.True(f.IsChanged(float.PositiveInfinity, float.NegativeInfinity));

        IReplicationCodec<double> d = Get<double>();
        Assert.False(d.IsChanged(double.NaN, double.NaN));
        Assert.True(d.IsChanged(double.NaN, 1d));
        Assert.False(d.IsChanged(0d, -0d));
        Assert.False(d.IsChanged(2.5d, 2.5d));
        Assert.True(d.IsChanged(2.5d, 2.5000001d));
    }

    // ---------- string ----------

    [Fact]
    public void String_RoundTrips()
    {
        AssertRoundTrip("hello", 8 + 5 * 8);
        AssertRoundTrip("", 8);
        AssertRoundTrip("Привет, 世界 🎮", 8 + Encoding.UTF8.GetByteCount("Привет, 世界 🎮") * 8);

        // Длинная строка: varuint из двух групп и буфер из пула
        string longString = new('x', 1000);
        AssertRoundTrip(longString, 16 + 1000 * 8);
    }

    [Fact]
    public void String_Null_IsWrittenAsZero()
    {
        string result = RoundTrip(Get<string>(), null, out int bits);

        Assert.Null(result);
        Assert.Equal(8, bits);

        var writer = new BitWriter();
        Get<string>().Write(writer, null);
        Assert.Equal(new byte[] { 0 }, writer.ToArray());

        Assert.Equal("", RoundTrip(""));
        Assert.NotNull(RoundTrip(""));
    }

    [Fact]
    public void String_IsChanged_IsOrdinal()
    {
        IReplicationCodec<string> codec = Get<string>();

        Assert.False(codec.IsChanged(null, null));
        Assert.True(codec.IsChanged(null, ""));
        Assert.True(codec.IsChanged("", null));
        Assert.False(codec.IsChanged("abc", new string("abc".ToCharArray())));
        Assert.True(codec.IsChanged("abc", "ABC"));
        // Канонически эквивалентные, но ординально разные строки
        Assert.True(codec.IsChanged("é", "é"));
    }

    [Fact]
    public void String_DefaultLimit_Is64KiB()
    {
        Assert.Equal(64 * 1024, StringCodec.DefaultMaxByteCount);
        Assert.Equal(64 * 1024, StringCodec.Default.MaxByteCount);
        Assert.Same(StringCodec.Default, Get<string>());
    }

    [Fact]
    public void String_WriteOverLimit_Throws()
    {
        var codec = new StringCodec(4);
        var writer = new BitWriter();

        codec.Write(writer, "abcd");
        // "ёё" = 4 байта в UTF-8, "ёёё" = 6
        codec.Write(writer, "ёё");
        Assert.Throws<ReplicationException>(() => codec.Write(writer, "abcde"));
        Assert.Throws<ReplicationException>(() => codec.Write(writer, "ёёё"));
    }

    [Fact]
    public void String_ReadOverLimit_ThrowsFormatException()
    {
        var writer = new BitWriter();
        new StringCodec(10).Write(writer, "0123456789");
        byte[] data = writer.ToArray();

        var small = new StringCodec(9);
        Assert.Throws<ReplicationFormatException>(() =>
        {
            var reader = new BitReader(data);
            small.Read(ref reader);
        });

        var exact = new StringCodec(10);
        var okReader = new BitReader(data);
        Assert.Equal("0123456789", exact.Read(ref okReader));
    }

    [Fact]
    public void String_HugeDeclaredLength_ThrowsFormatException()
    {
        var writer = new BitWriter();
        writer.WriteVarUInt(ulong.MaxValue);
        byte[] data = writer.ToArray();

        Assert.Throws<ReplicationFormatException>(() =>
        {
            var reader = new BitReader(data);
            new StringCodec(int.MaxValue).Read(ref reader);
        });
    }

    [Fact]
    public void String_TruncatedData_ThrowsFormatException()
    {
        var writer = new BitWriter();
        Get<string>().Write(writer, "hello");
        byte[] data = writer.ToArray()[..3];

        Assert.Throws<ReplicationFormatException>(() =>
        {
            var reader = new BitReader(data);
            Get<string>().Read(ref reader);
        });
    }

    [Fact]
    public void String_NegativeLimit_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new StringCodec(-1));
    }

    // ---------- enums ----------

    [Fact]
    public void Enum_Compact_UsesMinimalBits()
    {
        Assert.Equal(2, ((EnumCodec<Small>)Get<Small>()).BitCount);
        Assert.Equal(1, ((EnumCodec<OneValue>)Get<OneValue>()).BitCount);
        Assert.Equal(1, ((EnumCodec<Empty>)Get<Empty>()).BitCount);
        Assert.Equal(8, ((EnumCodec<Sparse>)Get<Sparse>()).BitCount);
        Assert.Equal(41, ((EnumCodec<Wide>)Get<Wide>()).BitCount);
        Assert.Equal(64, ((EnumCodec<HugeUnsigned>)Get<HugeUnsigned>()).BitCount);

        Assert.True(((EnumCodec<Small>)Get<Small>()).IsCompact);

        AssertRoundTrip(Small.A, 2);
        AssertRoundTrip(Small.D, 2);
        AssertRoundTrip(OneValue.Only, 1);
        AssertRoundTrip(Sparse.Low, 8);
        AssertRoundTrip(Sparse.High, 8);
        AssertRoundTrip(Wide.Big, 41);
        AssertRoundTrip(HugeUnsigned.Max, 64);
    }

    [Fact]
    public void Enum_UndefinedValueInRange_RoundTrips()
    {
        AssertRoundTrip((Sparse)100, 8);
        AssertRoundTrip((Wide)12345, 41);
    }

    [Fact]
    public void Enum_OutOfRangeWrite_Throws()
    {
        var writer = new BitWriter();

        Assert.Throws<ReplicationException>(() => Get<Small>().Write(writer, (Small)4));
        Assert.Throws<ReplicationException>(() => Get<Small>().Write(writer, (Small)(-1)));
        Assert.Throws<ReplicationException>(() => Get<Sparse>().Write(writer, (Sparse)201));
        Assert.Throws<ReplicationException>(() => Get<Empty>().Write(writer, (Empty)1));
    }

    [Fact]
    public void Enum_OutOfRangeRead_ThrowsFormatException()
    {
        var writer = new BitWriter();
        writer.WriteBits(201, 8);
        byte[] data = writer.ToArray();

        Assert.Throws<ReplicationFormatException>(() =>
        {
            var reader = new BitReader(data);
            Get<Sparse>().Read(ref reader);
        });
    }

    [Fact]
    public void Enum_FlagsAndNegative_UseFullWidth()
    {
        var flags = (EnumCodec<Options>)Get<Options>();
        Assert.False(flags.IsCompact);
        Assert.Equal(16, flags.BitCount);
        AssertRoundTrip(Options.First | Options.Second, 16);
        AssertRoundTrip((Options)0xFFFF, 16);

        var negative = (EnumCodec<Negative>)Get<Negative>();
        Assert.False(negative.IsCompact);
        Assert.Equal(16, negative.BitCount);
        AssertRoundTrip(Negative.Minus, 16);
        AssertRoundTrip(Negative.Plus, 16);
        AssertRoundTrip((Negative)short.MinValue, 16);
    }

    [Fact]
    public void Enum_IsChanged_ComparesValues()
    {
        Assert.False(Get<Small>().IsChanged(Small.B, Small.B));
        Assert.True(Get<Small>().IsChanged(Small.B, Small.C));
        Assert.True(Get<Negative>().IsChanged(Negative.Minus, Negative.Plus));
        Assert.False(Get<Wide>().IsChanged(Wide.Big, Wide.Big));
        Assert.True(Get<Wide>().IsChanged(Wide.Zero, Wide.Big));
    }

    [Fact]
    public void Enum_WriteAndIsChanged_DoNotAllocate()
    {
        IReplicationCodec<Small> small = Get<Small>();
        IReplicationCodec<Negative> negative = Get<Negative>();
        var writer = new BitWriter(4096);
        // Прогрев (JIT)
        small.Write(writer, Small.C);
        negative.Write(writer, Negative.Minus);
        small.IsChanged(Small.A, Small.B);
        negative.IsChanged(Negative.Minus, Negative.Plus);
        writer.Reset();

        bool changed = false;
        AllocationAssert.DoesNotAllocate(() =>
        {
            for (int i = 0; i < 1000; i++)
            {
                small.Write(writer, Small.C);
                negative.Write(writer, Negative.Minus);
                changed |= small.IsChanged(Small.A, Small.B);
                changed |= negative.IsChanged(Negative.Minus, Negative.Plus);
                writer.Reset();
            }
        });

        Assert.True(changed);
    }

    // ---------- nullable ----------

    [Fact]
    public void Nullable_RoundTrips()
    {
        AssertRoundTrip<int?>(null, 1);
        AssertRoundTrip<int?>(-7, 33);
        AssertRoundTrip<Small?>(Small.D, 3);
        AssertRoundTrip<Small?>(null, 1);
        AssertRoundTrip<Vector2?>(new Vector2(1, 2), 65);
        AssertRoundTrip<bool?>(false, 2);
    }

    [Fact]
    public void Nullable_IsChanged()
    {
        IReplicationCodec<float?> codec = Get<float?>();

        Assert.False(codec.IsChanged(null, null));
        Assert.True(codec.IsChanged(null, 0f));
        Assert.True(codec.IsChanged(0f, null));
        Assert.False(codec.IsChanged(0f, -0f));
        Assert.False(codec.IsChanged(float.NaN, float.NaN));
        Assert.True(codec.IsChanged(1f, 2f));
    }

    [Fact]
    public void Nullable_DirectConstruction_RequiresInner()
    {
        Assert.Throws<ArgumentNullException>(() => new NullableCodec<int>(null));
        Assert.Same(Int32Codec.Instance, new NullableCodec<int>(Int32Codec.Instance).Inner);
    }

    // ---------- Godot ----------

    [Fact]
    public void Godot_RoundTripComponentWise()
    {
        AssertRoundTrip(new Vector2(1.5f, -2.25f), 64);
        AssertRoundTrip(new Vector2I(int.MinValue, int.MaxValue), 64);
        AssertRoundTrip(new Vector3(1, -2, 3.5f), 96);
        AssertRoundTrip(new Vector3I(-1, 0, 7), 96);
        AssertRoundTrip(new Vector4(1, 2, 3, -4), 128);
        AssertRoundTrip(new Vector4I(1, -2, 3, int.MinValue), 128);
        AssertRoundTrip(new Color(0.1f, 0.2f, 0.3f, 0.4f), 128);
        AssertRoundTrip(new Quaternion(0.5f, -0.5f, 0.5f, 0.5f), 128);
        AssertRoundTrip(new Rect2(new Vector2(1, 2), new Vector2(3, 4)), 128);
        AssertRoundTrip(new Rect2I(new Vector2I(-1, 2), new Vector2I(30, 40)), 128);
    }

    [Fact]
    public void Godot_RoundTripPreservesNaNAndNegativeZero()
    {
        Vector3 result = RoundTrip(new Vector3(float.NaN, -0f, float.NegativeInfinity));

        Assert.True(float.IsNaN(result.X));
        Assert.True(float.IsNegative(result.Y) && result.Y == 0f);
        Assert.Equal(float.NegativeInfinity, result.Z);
    }

    [Fact]
    public void Godot_IsChanged_UsesFloatEqualsSemantics()
    {
        IReplicationCodec<Vector2> v2 = Get<Vector2>();
        Assert.False(v2.IsChanged(new Vector2(float.NaN, 1), new Vector2(float.NaN, 1)));
        Assert.False(v2.IsChanged(new Vector2(0f, 1), new Vector2(-0f, 1)));
        Assert.False(v2.IsChanged(new Vector2(3, 4), new Vector2(3, 4)));
        Assert.True(v2.IsChanged(new Vector2(3, 4), new Vector2(3, 5)));
        Assert.True(v2.IsChanged(new Vector2(float.NaN, 4), new Vector2(3, 4)));

        IReplicationCodec<Vector3> v3 = Get<Vector3>();
        Assert.False(v3.IsChanged(new Vector3(float.NaN, -0f, 1), new Vector3(float.NaN, 0f, 1)));
        Assert.True(v3.IsChanged(new Vector3(1, 2, 3), new Vector3(1, 2, 4)));

        IReplicationCodec<Vector4> v4 = Get<Vector4>();
        Assert.False(v4.IsChanged(new Vector4(float.NaN, 0, 0, 0), new Vector4(float.NaN, -0f, 0, 0)));
        Assert.True(v4.IsChanged(new Vector4(1, 2, 3, 4), new Vector4(1, 2, 3, 5)));

        IReplicationCodec<Color> color = Get<Color>();
        Assert.False(color.IsChanged(new Color(float.NaN, 0, 0, 1), new Color(float.NaN, -0f, 0, 1)));
        Assert.True(color.IsChanged(new Color(1, 0, 0, 1), new Color(1, 0, 0, 0.5f)));

        IReplicationCodec<Quaternion> quaternion = Get<Quaternion>();
        Assert.False(quaternion.IsChanged(new Quaternion(float.NaN, 0, 0, 1), new Quaternion(float.NaN, -0f, 0, 1)));
        Assert.True(quaternion.IsChanged(new Quaternion(0, 0, 0, 1), new Quaternion(0, 0, 1, 0)));

        IReplicationCodec<Rect2> rect = Get<Rect2>();
        Assert.False(rect.IsChanged(new Rect2(float.NaN, 0, 1, 1), new Rect2(float.NaN, -0f, 1, 1)));
        Assert.True(rect.IsChanged(new Rect2(0, 0, 1, 1), new Rect2(0, 0, 1, 2)));
        Assert.True(rect.IsChanged(new Rect2(0, 0, 1, 1), new Rect2(1, 0, 1, 1)));

        Assert.False(Get<Vector2I>().IsChanged(new Vector2I(1, 2), new Vector2I(1, 2)));
        Assert.True(Get<Vector2I>().IsChanged(new Vector2I(1, 2), new Vector2I(1, 3)));
        Assert.True(Get<Vector3I>().IsChanged(new Vector3I(1, 2, 3), new Vector3I(0, 2, 3)));
        Assert.True(Get<Vector4I>().IsChanged(new Vector4I(1, 2, 3, 4), new Vector4I(1, 2, 3, 0)));
        Assert.False(Get<Rect2I>().IsChanged(new Rect2I(1, 2, 3, 4), new Rect2I(1, 2, 3, 4)));
        Assert.True(Get<Rect2I>().IsChanged(new Rect2I(1, 2, 3, 4), new Rect2I(1, 2, 3, 5)));
    }

    // ---------- registry ----------

    [Fact]
    public void Registry_HasAllDefaultCodecs()
    {
        Type[] types =
        [
            typeof(bool), typeof(byte), typeof(sbyte), typeof(short), typeof(ushort), typeof(int), typeof(uint),
            typeof(long), typeof(ulong), typeof(char), typeof(float), typeof(double), typeof(string),
            typeof(Vector2), typeof(Vector2I), typeof(Vector3), typeof(Vector3I), typeof(Vector4), typeof(Vector4I),
            typeof(Color), typeof(Quaternion), typeof(Rect2), typeof(Rect2I)
        ];

        foreach (Type type in types)
        {
            Assert.True(Codecs.CanEncode(type), type.Name);
        }
    }

    [Fact]
    public void Registry_CanEncode()
    {
        Assert.True(Codecs.CanEncode(typeof(Small)));
        Assert.True(Codecs.CanEncode(typeof(int?)));
        Assert.True(Codecs.CanEncode(typeof(Small?)));
        Assert.True(Codecs.CanEncode(typeof(Vector3?)));

        Assert.False(Codecs.CanEncode(typeof(Unsupported)));
        Assert.False(Codecs.CanEncode(typeof(CustomStruct)));
        Assert.False(Codecs.CanEncode(typeof(CustomStruct?)));
        Assert.False(Codecs.CanEncode(typeof(object)));
        Assert.False(Codecs.CanEncode(typeof(List<int>)));
        Assert.False(Codecs.CanEncode(typeof(int[])));
        Assert.False(Codecs.CanEncode(typeof(Nullable<>)));
        Assert.Throws<ArgumentNullException>(() => Codecs.CanEncode(null));
    }

    [Fact]
    public void Registry_TryGet_UnsupportedType_ReturnsFalse()
    {
        Assert.False(Codecs.TryGet(out IReplicationCodec<Unsupported> codec));
        Assert.Null(codec);
        Assert.False(Codecs.TryGet(out IReplicationCodec<CustomStruct?> nullable));
        Assert.Null(nullable);
    }

    [Fact]
    public void Registry_LazyCodecs_AreCached()
    {
        var codecs = new ReplicationCodecs();

        Assert.True(codecs.TryGet(out IReplicationCodec<Small> first));
        Assert.True(codecs.TryGet(out IReplicationCodec<Small> second));
        Assert.Same(first, second);
        Assert.IsType<EnumCodec<Small>>(first);

        Assert.True(codecs.TryGet(out IReplicationCodec<Small?> nullableFirst));
        Assert.True(codecs.TryGet(out IReplicationCodec<Small?> nullableSecond));
        Assert.Same(nullableFirst, nullableSecond);
        Assert.Same(first, ((NullableCodec<Small>)nullableFirst).Inner);
    }

    [Fact]
    public void Registry_CustomCodec_OverridesDefault()
    {
        var codecs = new ReplicationCodecs();
        var custom = new ConstantIntCodec();

        Assert.True(codecs.TryGet(out IReplicationCodec<int?> nullableBefore));
        codecs.Register<int>(custom);

        Assert.True(codecs.TryGet(out IReplicationCodec<int> codec));
        Assert.Same(custom, codec);
        Assert.Equal(42, RoundTrip(codec, 7, out int bits));
        Assert.Equal(2, bits);

        // Nullable от переопределенного типа использует новый кодек
        Assert.True(codecs.TryGet(out IReplicationCodec<int?> nullableAfter));
        Assert.NotSame(nullableBefore, nullableAfter);
        Assert.Same(custom, ((NullableCodec<int>)nullableAfter).Inner);
        Assert.Equal(42, RoundTrip(nullableAfter, 7, out bits));
        Assert.Equal(3, bits);

        // Реестр по умолчанию не затронут
        Assert.IsType<Int32Codec>(Get<int>());
    }

    [Fact]
    public void Registry_CustomCodec_ForEnumAndStruct()
    {
        var codecs = new ReplicationCodecs();
        var enumCodec = new EnumCodec<Small>();
        codecs.Register(enumCodec);
        Assert.True(codecs.TryGet(out IReplicationCodec<Small> small));
        Assert.Same(enumCodec, small);

        Assert.False(codecs.CanEncode(typeof(CustomStruct)));
        codecs.Register(new CustomStructCodec());
        Assert.True(codecs.CanEncode(typeof(CustomStruct)));
        Assert.True(codecs.CanEncode(typeof(CustomStruct?)));
        Assert.True(codecs.TryGet(out IReplicationCodec<CustomStruct?> nullable));
        Assert.Equal(5, RoundTrip(nullable, new CustomStruct { Value = 5 }, out _)!.Value.Value);
    }

    [Fact]
    public void Registry_RegisterNull_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new ReplicationCodecs().Register<int>(null));
    }

    private sealed class CustomStructCodec : IReplicationCodec<CustomStruct>
    {
        public void Write(BitWriter writer, CustomStruct value)
        {
            writer.WriteVarInt(value.Value);
        }

        public CustomStruct Read(ref BitReader reader)
        {
            return new CustomStruct { Value = (int)reader.ReadVarInt() };
        }

        public bool IsChanged(CustomStruct lastSent, CustomStruct current)
        {
            return lastSent.Value != current.Value;
        }
    }
}
