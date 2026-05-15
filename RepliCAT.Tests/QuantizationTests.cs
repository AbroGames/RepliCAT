using Godot;
using RepliCAT;
using RepliCAT.Bits;
using RepliCAT.Codecs;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace RepliCAT.Tests;

public class QuantizationTests
{
    private const string MemberPath = "Tests.Owner.Member";

    private sealed class CollectingSink : ILogEventSink
    {
        private readonly List<LogEvent> _events = new();

        public List<LogEvent> Events
        {
            get
            {
                lock (_events)
                {
                    return _events.ToList();
                }
            }
        }

        public void Emit(LogEvent logEvent)
        {
            lock (_events)
            {
                _events.Add(logEvent);
            }
        }
    }

    private static (ILogger Logger, CollectingSink Sink) CreateLogger()
    {
        var sink = new CollectingSink();
        ILogger logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
        return (logger, sink);
    }

    private static IReplicationCodec<T> Build<T>(QuantizeAttribute quantize, double tolerance = 0, ILogger logger = null)
    {
        new ReplicationCodecs().TryGet(out IReplicationCodec<T> baseCodec);
        return CodecOptions.Apply(baseCodec, quantize, tolerance, MemberPath, logger ?? CreateLogger().Logger);
    }

    private static T RoundTrip<T>(IReplicationCodec<T> codec, T value, out int bits)
    {
        var writer = new BitWriter();
        codec.Write(writer, value);
        bits = writer.BitPosition;
        var reader = new BitReader(writer.ToArray());
        T result = codec.Read(ref reader);
        Assert.Equal(bits, reader.BitPosition);
        return result;
    }

    private static T RoundTrip<T>(IReplicationCodec<T> codec, T value)
    {
        return RoundTrip(codec, value, out _);
    }

    // ---------- round-trips ----------

    [Fact]
    public void Bounded_RoundTrip_WithinPrecision()
    {
        IReplicationCodec<float> codec = Build<float>(new QuantizeAttribute(-10, 10, 0.01));
        var random = new Random(1);
        for (int i = 0; i < 1000; i++)
        {
            float value = (float)(random.NextDouble() * 20 - 10);
            float result = RoundTrip(codec, value, out int bits);
            Assert.Equal(11, bits); // 2000 шагов
            Assert.InRange(Math.Abs(result - value), 0, 0.005 + 1e-6);
        }

        Assert.Equal(-10f, RoundTrip(codec, -10f));
        Assert.Equal(10f, RoundTrip(codec, 10f));
    }

    [Fact]
    public void Bounded_Double_RoundTrip_WithinPrecision()
    {
        IReplicationCodec<double> codec = Build<double>(new QuantizeAttribute(0, 1000, 0.001));
        var random = new Random(2);
        for (int i = 0; i < 1000; i++)
        {
            double value = random.NextDouble() * 1000;
            Assert.InRange(Math.Abs(RoundTrip(codec, value) - value), 0, 0.0005 + 1e-9);
        }
    }

    [Fact]
    public void Unbounded_RoundTrip_WithinPrecision()
    {
        IReplicationCodec<double> codec = Build<double>(new QuantizeAttribute(0.01));
        var random = new Random(3);
        for (int i = 0; i < 1000; i++)
        {
            double value = (random.NextDouble() - 0.5) * 2e6;
            Assert.InRange(Math.Abs(RoundTrip(codec, value) - value), 0, 0.005 + 1e-6);
        }
    }

    [Fact]
    public void Unbounded_SizeDependsOnMagnitude()
    {
        IReplicationCodec<float> codec = Build<float>(new QuantizeAttribute(0.1));

        RoundTrip(codec, 0f, out int zeroBits);
        RoundTrip(codec, 1000f, out int bigBits);

        Assert.Equal(8, zeroBits);   // zigzag(0) — одна группа varuint
        Assert.Equal(24, bigBits);   // zigzag(10000) = 20000 — три группы
        Assert.Equal(-3.5f, RoundTrip(codec, -3.5f), 0.0001f);
    }

    // ---------- bit counts ----------

    [Theory]
    [InlineData(0, 1, 1, 1, 1)]
    [InlineData(0, 1, 0.5, 2, 2)]
    [InlineData(0, 255, 1, 255, 8)]
    [InlineData(0, 256, 1, 256, 9)]
    [InlineData(-1, 1, 0.001, 2000, 11)]
    [InlineData(0, 0.1, 1, 0, 0)]
    [InlineData(0, 360, 0.1, 3600, 12)]
    public void Bounded_StepsAndBitCount(double min, double max, double precision, long steps, int bits)
    {
        var quantizer = new BoundedQuantizer(min, max, precision);
        Assert.Equal(steps, quantizer.Steps);
        Assert.Equal(bits, quantizer.BitCount);

        var codec = new QuantizedCodec<double>(GetAdapter<double>(), quantizer);
        RoundTrip(codec, (min + max) / 2, out int written);
        Assert.Equal(bits, written);
    }

    [Fact]
    public void Bounded_ZeroBits_AlwaysReadsMin()
    {
        IReplicationCodec<float> codec = Build<float>(new QuantizeAttribute(5, 5.1, 1));
        Assert.Equal(5f, RoundTrip(codec, 5.1f, out int bits));
        Assert.Equal(0, bits);
    }

    [Fact]
    public void Bounded_MaxSteps_Allowed()
    {
        var quantizer = new BoundedQuantizer(0, BoundedQuantizer.MaxSteps, 1);
        Assert.Equal(63, quantizer.BitCount);
    }

    // ---------- clamping and warnings ----------

    [Fact]
    public void Bounded_OutOfRange_ClampedAndWarnedOnce()
    {
        (ILogger logger, CollectingSink sink) = CreateLogger();
        IReplicationCodec<float> codec = Build<float>(new QuantizeAttribute(0, 10, 0.5), logger: logger);

        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(10f, RoundTrip(codec, 20f));
            Assert.Equal(0f, RoundTrip(codec, -5f));
            Assert.Equal(0f, RoundTrip(codec, float.NaN));
            Assert.Equal(10f, RoundTrip(codec, float.PositiveInfinity));
            Assert.Equal(0f, RoundTrip(codec, float.NegativeInfinity));
        }

        List<LogEvent> events = sink.Events;
        LogEvent warning = Assert.Single(events);
        Assert.Equal(LogEventLevel.Warning, warning.Level);
        Assert.Contains(MemberPath, warning.RenderMessage());
        Assert.True(((QuantizedCodec<float>)codec).Quantizer.HasReportedOutOfRange);
    }

    [Fact]
    public void Bounded_InRange_DoesNotWarn()
    {
        (ILogger logger, CollectingSink sink) = CreateLogger();
        IReplicationCodec<float> codec = Build<float>(new QuantizeAttribute(0, 10, 0.5), logger: logger);

        RoundTrip(codec, 0f);
        RoundTrip(codec, 10f);
        RoundTrip(codec, 3.3f);

        Assert.Empty(sink.Events);
    }

    [Fact]
    public void Warnings_ArePerMember()
    {
        (ILogger logger, CollectingSink sink) = CreateLogger();
        IReplicationCodec<float> first = Build<float>(new QuantizeAttribute(0, 1, 0.1), logger: logger);
        IReplicationCodec<float> second = Build<float>(new QuantizeAttribute(0, 1, 0.1), logger: logger);

        RoundTrip(first, 5f);
        RoundTrip(first, 5f);
        RoundTrip(second, 5f);
        RoundTrip(second, 5f);

        Assert.Equal(2, sink.Events.Count);
    }

    [Fact]
    public void Unbounded_OutOfRange_ClampedAndWarnedOnce()
    {
        (ILogger logger, CollectingSink sink) = CreateLogger();
        IReplicationCodec<double> codec = Build<double>(new QuantizeAttribute(1), logger: logger);

        double limit = UnboundedQuantizer.MaxAbsStep;
        for (int i = 0; i < 10; i++)
        {
            Assert.Equal(limit, RoundTrip(codec, 1e30));
            Assert.Equal(-limit, RoundTrip(codec, -1e30));
            Assert.Equal(limit, RoundTrip(codec, double.PositiveInfinity));
            Assert.Equal(0, RoundTrip(codec, double.NaN));
        }

        Assert.Single(sink.Events);
        Assert.Equal(12345, RoundTrip(codec, 12345.2));
    }

    [Fact]
    public void NullLogger_ClampsWithoutThrowing()
    {
        new ReplicationCodecs().TryGet(out IReplicationCodec<float> baseCodec);
        IReplicationCodec<float> codec = CodecOptions.Apply(baseCodec, new QuantizeAttribute(0, 1, 0.1), 0, MemberPath, null);
        Assert.Equal(1f, RoundTrip(codec, 3f));
    }

    // ---------- NaN ----------

    [Fact]
    public void Quantized_NaN_IsChanged()
    {
        IReplicationCodec<float> bounded = Build<float>(new QuantizeAttribute(0, 10, 1));
        Assert.False(bounded.IsChanged(float.NaN, float.NaN));
        Assert.False(bounded.IsChanged(float.NaN, 0f)); // NaN прижимается к min
        Assert.True(bounded.IsChanged(float.NaN, 5f));

        IReplicationCodec<float> unbounded = Build<float>(new QuantizeAttribute(1));
        Assert.False(unbounded.IsChanged(float.NaN, float.NaN));
        Assert.True(unbounded.IsChanged(float.NaN, 5f));
    }

    [Fact]
    public void Tolerance_NaN_FallsBackToExactComparison()
    {
        IReplicationCodec<float> codec = Build<float>(null, tolerance: 0.5);
        Assert.False(codec.IsChanged(float.NaN, float.NaN));
        Assert.True(codec.IsChanged(float.NaN, 1f));
        Assert.True(codec.IsChanged(1f, float.NaN));
        Assert.False(codec.IsChanged(float.PositiveInfinity, float.PositiveInfinity));
        Assert.True(codec.IsChanged(float.PositiveInfinity, float.NegativeInfinity));
        Assert.True(codec.IsChanged(0f, float.PositiveInfinity));

        IReplicationCodec<Vector2> vector = Build<Vector2>(null, tolerance: 0.5);
        Assert.False(vector.IsChanged(new Vector2(float.NaN, 1), new Vector2(float.NaN, 1)));
        Assert.True(vector.IsChanged(new Vector2(float.NaN, 1), new Vector2(float.NaN, 1.1f)));
        Assert.True(vector.IsChanged(new Vector2(float.NaN, 1), new Vector2(0, 1)));
    }

    [Fact]
    public void Adapter_MaxDelta_PropagatesNaN()
    {
        ComponentAdapter<Vector3> adapter = GetAdapter<Vector3>();
        Assert.True(double.IsNaN(adapter.MaxDelta(new Vector3(float.NaN, 0, 0), new Vector3(0, 100, 0))));
        Assert.True(double.IsNaN(adapter.MaxDelta(new Vector3(float.PositiveInfinity, 0, 0), new Vector3(float.PositiveInfinity, 0, 0))));
        Assert.Equal(3, adapter.MaxDelta(new Vector3(1, 2, 3), new Vector3(2, -1, 3)), 6);

        Assert.True(double.IsNaN(GetAdapter<float>().MaxDelta(float.NaN, 1)));
        Assert.True(double.IsNaN(GetAdapter<double>().MaxDelta(double.NaN, double.NaN)));
        Assert.Equal(5, GetAdapter<int>().MaxDelta(-2, 3));
    }

    // ---------- change detection ----------

    [Fact]
    public void Quantized_ChangeBelowOneQuantum_IsNotChanged()
    {
        IReplicationCodec<float> codec = Build<float>(new QuantizeAttribute(0.1));
        Assert.False(codec.IsChanged(1.0f, 1.04f));
        Assert.False(codec.IsChanged(1.0f, 0.96f));
        Assert.True(codec.IsChanged(1.0f, 1.06f));
        Assert.True(codec.IsChanged(1.0f, 1.1f));
    }

    [Fact]
    public void Quantized_SlowDrift_IsEventuallySent()
    {
        IReplicationCodec<double> codec = Build<double>(new QuantizeAttribute(0, 100, 0.1));
        double lastSent = 0;
        double current = 0;
        int sent = 0;
        for (int frame = 0; frame < 1000; frame++)
        {
            current += 0.003;
            if (codec.IsChanged(lastSent, current))
            {
                sent++;
                lastSent = current;
            }

            // Между последним отправленным и текущим — меньше одного шага
            Assert.InRange(Math.Abs(current - lastSent), 0, 0.1);
        }

        Assert.InRange(sent, 25, 35); // дрейф 3.0 при шаге 0.1
    }

    [Fact]
    public void Tolerance_Semantics()
    {
        IReplicationCodec<float> codec = Build<float>(null, tolerance: 0.5);
        Assert.False(codec.IsChanged(0f, 0.4f));
        Assert.False(codec.IsChanged(0f, -0.5f));
        Assert.True(codec.IsChanged(0f, 0.6f));
        Assert.True(codec.IsChanged(0f, -0.6f));

        // Медленный дрейф накапливается относительно последнего отправленного значения
        float lastSent = 0;
        float current = 0;
        int sent = 0;
        for (int i = 0; i < 100; i++)
        {
            current += 0.1f;
            if (codec.IsChanged(lastSent, current))
            {
                sent++;
                lastSent = current;
            }
        }

        Assert.InRange(sent, 15, 20);
        Assert.InRange(current - lastSent, 0, 0.5f);
    }

    [Fact]
    public void Tolerance_OnVector_UsesMaxComponentDelta()
    {
        IReplicationCodec<Vector3> codec = Build<Vector3>(null, tolerance: 0.5);
        Assert.False(codec.IsChanged(Vector3.Zero, new Vector3(0.4f, -0.4f, 0.4f)));
        Assert.True(codec.IsChanged(Vector3.Zero, new Vector3(0, 0, 0.6f)));
    }

    [Fact]
    public void Tolerance_WriteAndRead_UseBaseCodec()
    {
        IReplicationCodec<float> codec = Build<float>(null, tolerance: 0.5);
        Assert.IsType<ToleranceCodec<float>>(codec);
        Assert.Equal(1.2345f, RoundTrip(codec, 1.2345f, out int bits));
        Assert.Equal(32, bits);
    }

    [Fact]
    public void Tolerance_CombinedWithQuantize_RequiresBothConditions()
    {
        // Квантование шагом 1 и допуск 1.5: изменение на один шаг не превышает допуск
        IReplicationCodec<double> coarse = Build<double>(new QuantizeAttribute(1), tolerance: 1.5);
        Assert.False(coarse.IsChanged(0, 1));
        Assert.True(coarse.IsChanged(0, 2));

        // Допуск 0.1 и шаг 1: разница больше допуска, но квантованный шаг тот же
        IReplicationCodec<double> fine = Build<double>(new QuantizeAttribute(1), tolerance: 0.1);
        Assert.False(fine.IsChanged(0, 0.4));
        Assert.True(fine.IsChanged(0, 0.6));

        // Запись идет через квантование
        Assert.Equal(3, RoundTrip(fine, 3.2));
    }

    [Fact]
    public void NoOptions_ReturnsBaseCodec()
    {
        new ReplicationCodecs().TryGet(out IReplicationCodec<float> baseCodec);
        Assert.Same(baseCodec, CodecOptions.Apply(baseCodec, null, 0, MemberPath, null));

        // Без квантования и допуска поддержка типа не требуется
        new ReplicationCodecs().TryGet(out IReplicationCodec<string> stringCodec);
        Assert.Same(stringCodec, CodecOptions.Apply(stringCodec, null, 0, MemberPath, null));
    }

    // ---------- integers ----------

    [Fact]
    public void Integer_BoundedQuantize_RoundsAndClamps()
    {
        (ILogger logger, CollectingSink sink) = CreateLogger();
        IReplicationCodec<int> codec = Build<int>(new QuantizeAttribute(0, 100, 10), logger: logger);

        Assert.Equal(40, RoundTrip(codec, 44, out int bits));
        Assert.Equal(4, bits); // 10 шагов
        Assert.Equal(50, RoundTrip(codec, 46));
        Assert.Equal(50, RoundTrip(codec, 45)); // половина — от нуля
        Assert.Equal(100, RoundTrip(codec, 150));
        Assert.Equal(0, RoundTrip(codec, -7));
        Assert.Single(sink.Events);

        Assert.False(codec.IsChanged(41, 44));
        Assert.True(codec.IsChanged(44, 46));
    }

    [Fact]
    public void Integer_UnboundedQuantize_ExactForUnitPrecision()
    {
        IReplicationCodec<int> codec = Build<int>(new QuantizeAttribute(1));
        Assert.Equal(int.MaxValue, RoundTrip(codec, int.MaxValue));
        Assert.Equal(int.MinValue, RoundTrip(codec, int.MinValue));
        Assert.Equal(-17, RoundTrip(codec, -17));

        IReplicationCodec<long> longCodec = Build<long>(new QuantizeAttribute(100));
        Assert.Equal(-12300L, RoundTrip(longCodec, -12345L));
        Assert.Equal(12300L, RoundTrip(longCodec, 12349L));
    }

    [Fact]
    public void Integer_DequantizedValue_ClampedToTypeRange()
    {
        // Диапазон шире типа: -128 квантуется в шаг -150 и прижимается к sbyte.MinValue
        IReplicationCodec<sbyte> codec = Build<sbyte>(new QuantizeAttribute(-200, 200, 50));
        Assert.Equal(sbyte.MinValue, RoundTrip(codec, sbyte.MinValue));
        Assert.Equal(sbyte.MaxValue, RoundTrip(codec, sbyte.MaxValue));

        IReplicationCodec<byte> bytes = Build<byte>(new QuantizeAttribute(0, 1000, 300));
        Assert.Equal(byte.MaxValue, RoundTrip(bytes, (byte)250));
    }

    [Fact]
    public void Integer_Adapters_RoundAndClamp()
    {
        Assert.Equal((byte)255, GetAdapter<byte>().Compose([300]));
        Assert.Equal((byte)0, GetAdapter<byte>().Compose([-5]));
        Assert.Equal((byte)3, GetAdapter<byte>().Compose([2.5]));
        Assert.Equal((sbyte)-3, GetAdapter<sbyte>().Compose([-2.5]));
        Assert.Equal((short)-2, GetAdapter<short>().Compose([-2.4]));
        Assert.Equal((ushort)65535, GetAdapter<ushort>().Compose([1e9]));
        Assert.Equal(0, GetAdapter<int>().Compose([double.NaN]));
        Assert.Equal(int.MinValue, GetAdapter<int>().Compose([double.NegativeInfinity]));
        Assert.Equal(uint.MaxValue, GetAdapter<uint>().Compose([1e20]));
        Assert.Equal(0u, GetAdapter<uint>().Compose([-1]));
        Assert.Equal(long.MaxValue, GetAdapter<long>().Compose([1e30]));
        Assert.Equal(long.MinValue, GetAdapter<long>().Compose([-1e30]));
        Assert.True(GetAdapter<long>().IsInteger);
        Assert.False(GetAdapter<float>().IsInteger);
    }

    [Fact]
    public void Integer_Tolerance()
    {
        IReplicationCodec<int> codec = Build<int>(null, tolerance: 2);
        Assert.False(codec.IsChanged(10, 12));
        Assert.False(codec.IsChanged(10, 8));
        Assert.True(codec.IsChanged(10, 13));
        Assert.Equal(123456, RoundTrip(codec, 123456));
    }

    // ---------- vectors, quaternion, color ----------

    [Fact]
    public void Vector_BoundedQuantize_ComponentWise()
    {
        IReplicationCodec<Vector3> codec = Build<Vector3>(new QuantizeAttribute(-100, 100, 0.01));
        var value = new Vector3(12.345f, -99.999f, 0.004f);

        Vector3 result = RoundTrip(codec, value, out int bits);

        Assert.Equal(45, bits); // 3 × bitLength(20000)
        Assert.Equal(value.X, result.X, 0.0051f);
        Assert.Equal(value.Y, result.Y, 0.0051f);
        Assert.Equal(value.Z, result.Z, 0.0051f);

        Assert.False(codec.IsChanged(value, new Vector3(12.346f, -99.999f, 0.004f)));
        Assert.True(codec.IsChanged(value, new Vector3(12.345f, -99.999f, 0.02f)));
    }

    [Fact]
    public void Vector2And4_RoundTrip()
    {
        IReplicationCodec<Vector2> v2 = Build<Vector2>(new QuantizeAttribute(0.5));
        Assert.Equal(new Vector2(1.5f, -2f), RoundTrip(v2, new Vector2(1.4f, -2.1f)));

        IReplicationCodec<Vector4> v4 = Build<Vector4>(new QuantizeAttribute(0, 10, 1));
        Assert.Equal(new Vector4(1, 2, 3, 10), RoundTrip(v4, new Vector4(1.2f, 2.4f, 2.6f, 11f)));
    }

    [Fact]
    public void Quaternion_Quantize_ComponentWise()
    {
        IReplicationCodec<Quaternion> codec = Build<Quaternion>(new QuantizeAttribute(-1, 1, 0.001));
        var value = new Quaternion(new Vector3(0, 1, 0), 0.7f);

        Quaternion result = RoundTrip(codec, value, out int bits);

        Assert.Equal(44, bits); // 4 × bitLength(2000)
        Assert.Equal(value.X, result.X, 0.0006f);
        Assert.Equal(value.Y, result.Y, 0.0006f);
        Assert.Equal(value.Z, result.Z, 0.0006f);
        Assert.Equal(value.W, result.W, 0.0006f);
    }

    [Fact]
    public void Color_Quantize_ToBytes()
    {
        IReplicationCodec<Color> codec = Build<Color>(new QuantizeAttribute(0, 1, 1.0 / 255));
        var value = new Color(0.2f, 0.4f, 0.6f, 1f);

        Color result = RoundTrip(codec, value, out int bits);

        Assert.Equal(32, bits);
        Assert.Equal(value.R, result.R, 1f / 510 + 1e-6f);
        Assert.Equal(value.G, result.G, 1f / 510 + 1e-6f);
        Assert.Equal(value.B, result.B, 1f / 510 + 1e-6f);
        Assert.Equal(1f, result.A);
    }

    [Fact]
    public void Color_Tolerance()
    {
        IReplicationCodec<Color> codec = Build<Color>(null, tolerance: 0.01);
        Assert.False(codec.IsChanged(new Color(0.5f, 0.5f, 0.5f), new Color(0.505f, 0.5f, 0.5f)));
        Assert.True(codec.IsChanged(new Color(0.5f, 0.5f, 0.5f), new Color(0.5f, 0.5f, 0.5f, 0.9f)));
    }

    [Fact]
    public void Adapters_Components()
    {
        ComponentAdapter<Quaternion> quaternion = GetAdapter<Quaternion>();
        var q = new Quaternion(1, 2, 3, 4);
        Assert.Equal(4, quaternion.Count);
        Assert.Equal([1.0, 2, 3, 4], Enumerable.Range(0, 4).Select(i => quaternion.Get(q, i)));
        Assert.Equal(q, quaternion.Compose([1, 2, 3, 4]));

        ComponentAdapter<Color> color = GetAdapter<Color>();
        var c = new Color(0.1f, 0.2f, 0.3f, 0.4f);
        Assert.Equal(c, color.Compose([color.Get(c, 0), color.Get(c, 1), color.Get(c, 2), color.Get(c, 3)]));

        Assert.Throws<ArgumentOutOfRangeException>(() => quaternion.Get(q, 4));
        Assert.Throws<ArgumentException>(() => quaternion.Compose([1, 2, 3]));
    }

    [Fact]
    public void Adapter_Registry()
    {
        Type[] supported =
        [
            typeof(float), typeof(double), typeof(Vector2), typeof(Vector3), typeof(Vector4), typeof(Quaternion), typeof(Color),
            typeof(sbyte), typeof(byte), typeof(short), typeof(ushort), typeof(int), typeof(uint), typeof(long)
        ];
        foreach (Type type in supported)
        {
            Assert.True(ComponentAdapter.IsSupported(type), type.Name);
        }

        Assert.False(ComponentAdapter.IsSupported(typeof(ulong)));
        Assert.False(ComponentAdapter.IsSupported(typeof(Vector2I)));
        Assert.False(ComponentAdapter.IsSupported(typeof(float?)));
        Assert.False(ComponentAdapter.TryGet(out ComponentAdapter<string> _));
    }

    // ---------- malformed input ----------

    [Fact]
    public void Bounded_Read_StepAboveMaximum_Throws()
    {
        IReplicationCodec<float> codec = Build<float>(new QuantizeAttribute(0, 10, 1)); // 10 шагов, 4 бита
        var writer = new BitWriter();
        writer.WriteBits(15, 4);
        byte[] data = writer.ToArray();

        Assert.Throws<ReplicationFormatException>(() =>
        {
            var reader = new BitReader(data);
            codec.Read(ref reader);
        });
    }

    [Fact]
    public void Unbounded_Read_StepTooLarge_Throws()
    {
        IReplicationCodec<float> codec = Build<float>(new QuantizeAttribute(1));
        var writer = new BitWriter();
        writer.WriteVarInt(UnboundedQuantizer.MaxAbsStep + 1);
        byte[] data = writer.ToArray();

        Assert.Throws<ReplicationFormatException>(() =>
        {
            var reader = new BitReader(data);
            codec.Read(ref reader);
        });
    }

    [Fact]
    public void Truncated_Read_Throws()
    {
        IReplicationCodec<Vector3> codec = Build<Vector3>(new QuantizeAttribute(0, 1000, 1));
        byte[] data = [0xFF];

        Assert.Throws<ReplicationFormatException>(() =>
        {
            var reader = new BitReader(data);
            codec.Read(ref reader);
        });
    }

    // ---------- invalid parameters ----------

    public static TheoryData<QuantizeAttribute> InvalidQuantize => new()
    {
        new QuantizeAttribute(0),
        new QuantizeAttribute(-1),
        new QuantizeAttribute(double.NaN),
        new QuantizeAttribute(double.PositiveInfinity),
        new QuantizeAttribute(0, 10, 0),
        new QuantizeAttribute(0, 10, -0.5),
        new QuantizeAttribute(10, 10, 1),
        new QuantizeAttribute(10, 0, 1),
        new QuantizeAttribute(double.NaN, 10, 1),
        new QuantizeAttribute(0, double.PositiveInfinity, 1),
        new QuantizeAttribute(0, 1e20, 1e-3),
        new QuantizeAttribute(-1e308, 1e308, 1),
    };

    [Theory]
    [MemberData(nameof(InvalidQuantize))]
    public void InvalidQuantizeParameters_Throw(QuantizeAttribute quantize)
    {
        var exception = Assert.Throws<ReplicationException>(() => Build<float>(quantize));
        Assert.Contains(MemberPath, exception.Message);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidTolerance_Throws(double tolerance)
    {
        var exception = Assert.Throws<ReplicationException>(() => Build<float>(null, tolerance));
        Assert.Contains(MemberPath, exception.Message);
    }

    [Fact]
    public void UnsupportedTypes_Throw()
    {
        Assert.Contains(MemberPath, Assert.Throws<ReplicationException>(() => Build<string>(new QuantizeAttribute(1))).Message);
        Assert.Contains(MemberPath, Assert.Throws<ReplicationException>(() => Build<Vector2I>(null, 1)).Message);
        Assert.Contains(MemberPath, Assert.Throws<ReplicationException>(() => Build<float?>(new QuantizeAttribute(1))).Message);
        Assert.Contains(MemberPath, Assert.Throws<ReplicationException>(() => Build<ulong>(new QuantizeAttribute(1))).Message);
        Assert.Contains(MemberPath, Assert.Throws<ReplicationException>(() => Build<bool>(null, 0.5)).Message);
    }

    [Fact]
    public void Quantizer_Constructors_ValidateArguments()
    {
        Assert.Throws<ArgumentException>(() => new BoundedQuantizer(0, 0, 1));
        Assert.Throws<ArgumentException>(() => new UnboundedQuantizer(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ToleranceCodec<float>(SingleCodec.Instance, GetAdapter<float>(), -1));
    }

    // ---------- allocations ----------

    [Fact]
    public void QuantizedAndTolerance_DoNotAllocate()
    {
        IReplicationCodec<Vector3> quantized = Build<Vector3>(new QuantizeAttribute(-100, 100, 0.01), tolerance: 0.05);
        IReplicationCodec<float> unbounded = Build<float>(new QuantizeAttribute(0.01));
        IReplicationCodec<int> integer = Build<int>(null, tolerance: 1);
        var writer = new BitWriter(4096);
        var value = new Vector3(1, 2, 3);

        // Прогрев (JIT)
        Exercise();

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool changed = false;
        for (int i = 0; i < 1000; i++)
        {
            changed |= Exercise();
        }

        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.True(changed);
        Assert.Equal(0, after - before);

        bool Exercise()
        {
            writer.Reset();
            quantized.Write(writer, value);
            unbounded.Write(writer, 12.5f);
            integer.Write(writer, 7);
            var reader = new BitReader(writer.AsSpan());
            Vector3 read = quantized.Read(ref reader);
            float f = unbounded.Read(ref reader);
            int n = integer.Read(ref reader);
            return quantized.IsChanged(value, read + new Vector3(1, 0, 0))
                | unbounded.IsChanged(f, 13f)
                | integer.IsChanged(n, 9);
        }
    }

    private static ComponentAdapter<T> GetAdapter<T>()
    {
        Assert.True(ComponentAdapter.TryGet(out ComponentAdapter<T> adapter));
        return adapter;
    }
}
