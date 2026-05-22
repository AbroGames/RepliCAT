using Godot;
using RepliCAT;
using RepliCAT.Bits;
using RepliCAT.Codecs;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace RepliCAT.Tests;

public class ReplicatorTests
{
    // ---------- test infrastructure ----------

    private sealed class CollectingSink : ILogEventSink
    {
        private readonly List<LogEvent> _events = new();

        public int Count
        {
            get
            {
                lock (_events)
                {
                    return _events.Count;
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

    private static Replicator CreateReplicator(out CollectingSink sink, ReplicationCodecs codecs = null)
    {
        sink = new CollectingSink();
        ILogger logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
        return new Replicator(codecs: codecs, logger: logger);
    }

    private static Replicator CreateReplicator(ReplicationCodecs codecs = null)
    {
        return CreateReplicator(out _, codecs);
    }

    private static byte[] Delta(Replicator replicator, ReplicationBaseline baseline)
    {
        Assert.True(replicator.TryWriteDelta(baseline, out byte[] data));
        Assert.NotNull(data);
        return data;
    }

    // ---------- test types ----------

    public enum Team
    {
        Red,
        Green,
        Blue
    }

    public class BasePlayer
    {
        [Replicated] private int _baseSecret;

        [Replicated] protected float BaseProtected { get; set; }

        public int BaseSecret
        {
            get => _baseSecret;
            set => _baseSecret = value;
        }

        public float BaseProtectedValue
        {
            get => BaseProtected;
            set => BaseProtected = value;
        }
    }

    public class Player : BasePlayer
    {
        [Replicated] public int Health;
        [Replicated] public string Name;
        [Replicated] public Vector2 Position;
        [Replicated] public Team Team;
        [Replicated] public int? Target;
        [Replicated] public bool Alive;
        [Replicated] private double _private;

        private int _counted;

        public int SetterCalls;

        [Replicated] public long Score { get; set; }

        [Replicated] public int PrivateSet { get; private set; }

        [field: Replicated] public int BackingField { get; set; }

        [Replicated]
        public int Counted
        {
            get => _counted;
            set
            {
                _counted = value;
                SetterCalls++;
            }
        }

        public double PrivateValue
        {
            get => _private;
            set => _private = value;
        }

        public void SetPrivateSet(int value)
        {
            PrivateSet = value;
        }

        public static Player CreateFilled()
        {
            var player = new Player
            {
                Health = 100,
                Name = "Hero",
                Position = new Vector2(1.5f, -2.25f),
                Team = Team.Blue,
                Target = 42,
                Alive = true,
                PrivateValue = 3.25,
                Score = 1234567890123L,
                BackingField = 17,
                Counted = 5,
                BaseSecret = -9,
                BaseProtectedValue = 0.5f
            };
            player.SetPrivateSet(77);
            return player;
        }

        public void AssertEqual(Player other)
        {
            Assert.Equal(Health, other.Health);
            Assert.Equal(Name, other.Name);
            Assert.Equal(Position, other.Position);
            Assert.Equal(Team, other.Team);
            Assert.Equal(Target, other.Target);
            Assert.Equal(Alive, other.Alive);
            Assert.Equal(PrivateValue, other.PrivateValue);
            Assert.Equal(Score, other.Score);
            Assert.Equal(PrivateSet, other.PrivateSet);
            Assert.Equal(BackingField, other.BackingField);
            Assert.Equal(Counted, other.Counted);
            Assert.Equal(BaseSecret, other.BaseSecret);
            Assert.Equal(BaseProtectedValue, other.BaseProtectedValue);
        }
    }

    public class Simple
    {
        [Replicated] public int X;
        [Replicated] public int Y;
    }

    // ---------- first delta, no change, partial delta ----------

    [Fact]
    public void FirstDelta_ContainsEverything()
    {
        Replicator replicator = CreateReplicator();
        Player server = Player.CreateFilled();
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        Assert.Same(server, baseline.Target);
        Assert.False(baseline.IsWritten);

        byte[] data = Delta(replicator, baseline);
        Assert.True(baseline.IsWritten);

        var client = new Player();
        replicator.Apply(client, data);
        server.AssertEqual(client);
    }

    [Fact]
    public void FirstDelta_WritesDefaultValuesToo()
    {
        Replicator replicator = CreateReplicator();
        ReplicationBaseline baseline = replicator.CreateBaseline(new Simple());
        byte[] data = Delta(replicator, baseline);

        var client = new Simple { X = 5, Y = 6 };
        replicator.Apply(client, data);
        Assert.Equal(0, client.X);
        Assert.Equal(0, client.Y);
    }

    [Fact]
    public void NoChange_ReturnsFalse_WriterUntouched()
    {
        Replicator replicator = CreateReplicator();
        Player server = Player.CreateFilled();
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        Delta(replicator, baseline);

        Assert.False(replicator.TryWriteDelta(baseline, out byte[] data));
        Assert.Null(data);

        var writer = new BitWriter();
        writer.WriteBits(0b101, 3);
        byte[] before = writer.ToArray();
        Assert.False(replicator.TryWriteDelta(baseline, writer));
        Assert.Equal(3, writer.BitPosition);
        Assert.Equal(before, writer.ToArray());
    }

    [Fact]
    public void OneChangedMember_SmallerPayload_OnlyThatMemberApplied()
    {
        Replicator replicator = CreateReplicator();
        Player server = Player.CreateFilled();
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        byte[] full = Delta(replicator, baseline);

        var client = new Player();
        replicator.Apply(client, full);

        // Сентинелы на клиенте: частичная дельта не должна их трогать
        client.Name = "sentinel";
        client.Position = new Vector2(-1, -1);
        client.Team = Team.Green;
        client.Target = null;
        client.Alive = false;
        client.PrivateValue = -1;
        client.Score = -1;
        client.SetPrivateSet(-1);
        client.BackingField = -1;
        client.Counted = -1;
        client.BaseSecret = 1;
        client.BaseProtectedValue = -1;
        client.SetterCalls = 0;

        server.Health = 50;
        byte[] partial = Delta(replicator, baseline);
        Assert.True(partial.Length < full.Length);

        replicator.Apply(client, partial);
        Assert.Equal(50, client.Health);
        Assert.Equal("sentinel", client.Name);
        Assert.Equal(new Vector2(-1, -1), client.Position);
        Assert.Equal(Team.Green, client.Team);
        Assert.Null(client.Target);
        Assert.False(client.Alive);
        Assert.Equal(-1, client.PrivateValue);
        Assert.Equal(-1, client.Score);
        Assert.Equal(-1, client.PrivateSet);
        Assert.Equal(-1, client.BackingField);
        Assert.Equal(-1, client.Counted);
        Assert.Equal(1, client.BaseSecret);
        Assert.Equal(-1, client.BaseProtectedValue);
        Assert.Equal(0, client.SetterCalls);
    }

    [Fact]
    public void EveryMemberKind_ChangesReplicate()
    {
        Replicator replicator = CreateReplicator();
        Player server = Player.CreateFilled();
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var client = new Player();
        replicator.Apply(client, Delta(replicator, baseline));

        server.Name = null;
        server.Position = new Vector2(10, 20);
        server.Team = Team.Red;
        server.Target = null;
        server.Alive = false;
        server.PrivateValue = double.NaN;
        server.Score = long.MinValue;
        server.SetPrivateSet(-5);
        server.BackingField = 99;
        server.Counted = 6;
        server.BaseSecret = 1000;
        server.BaseProtectedValue = -7.5f;

        replicator.Apply(client, Delta(replicator, baseline));
        server.AssertEqual(client);
    }

    [Fact]
    public void PropertySetter_IsInvokedOnClient()
    {
        Replicator replicator = CreateReplicator();
        Player server = Player.CreateFilled();
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var client = new Player();

        replicator.Apply(client, Delta(replicator, baseline));
        Assert.Equal(1, client.SetterCalls);
        Assert.Equal(5, client.Counted);

        server.Health = 1;
        replicator.Apply(client, Delta(replicator, baseline));
        Assert.Equal(1, client.SetterCalls);

        server.Counted = 8;
        replicator.Apply(client, Delta(replicator, baseline));
        Assert.Equal(2, client.SetterCalls);
        Assert.Equal(8, client.Counted);
    }

    [Fact]
    public void BatchingManyObjectsIntoOneWriter()
    {
        Replicator replicator = CreateReplicator();
        var a = new Simple { X = 1, Y = 2 };
        var b = new Simple { X = 3, Y = 4 };
        ReplicationBaseline baselineA = replicator.CreateBaseline(a);
        ReplicationBaseline baselineB = replicator.CreateBaseline(b);

        var writer = new BitWriter();
        Assert.True(replicator.TryWriteDelta(baselineA, writer));
        Assert.True(replicator.TryWriteDelta(baselineB, writer));

        var clientA = new Simple();
        var clientB = new Simple();
        var reader = new BitReader(writer.ToArray());
        replicator.Apply(clientA, ref reader);
        replicator.Apply(clientB, ref reader);
        Assert.True(reader.RemainingBits < 8);
        Assert.Equal((1, 2), (clientA.X, clientA.Y));
        Assert.Equal((3, 4), (clientB.X, clientB.Y));
    }

    public class MemberKindsBase
    {
        [Replicated] protected int ProtectedField;
        [Replicated] private int PrivateProperty { get; set; }

        public int ProtectedFieldValue
        {
            get => ProtectedField;
            set => ProtectedField = value;
        }

        public int PrivatePropertyValue
        {
            get => PrivateProperty;
            set => PrivateProperty = value;
        }
    }

    public class MemberKindsDerived : MemberKindsBase
    {
        [Replicated] private int _derivedPrivate;
        [Replicated] protected string DerivedProtectedField;

        public int DerivedPrivate
        {
            get => _derivedPrivate;
            set => _derivedPrivate = value;
        }

        public string DerivedProtected
        {
            get => DerivedProtectedField;
            set => DerivedProtectedField = value;
        }
    }

    [Fact]
    public void ProtectedFieldsAndPrivateProperties_Replicate()
    {
        Replicator replicator = CreateReplicator();
        var server = new MemberKindsDerived
        {
            ProtectedFieldValue = 1, PrivatePropertyValue = 2, DerivedPrivate = 3, DerivedProtected = "x"
        };
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var client = new MemberKindsDerived();
        replicator.Apply(client, Delta(replicator, baseline));
        Assert.Equal((1, 2, 3, "x"),
            (client.ProtectedFieldValue, client.PrivatePropertyValue, client.DerivedPrivate, client.DerivedProtected));

        server.PrivatePropertyValue = 20;
        client.ProtectedFieldValue = -1;
        replicator.Apply(client, Delta(replicator, baseline));
        Assert.Equal((-1, 20), (client.ProtectedFieldValue, client.PrivatePropertyValue));
    }

    public class WideObject
    {
        [Replicated] public int A00, A01, A02, A03, A04, A05, A06, A07, A08, A09, A10, A11, A12, A13, A14, A15, A16, A17,
            A18, A19, A20, A21, A22, A23, A24, A25, A26, A27, A28, A29, A30, A31, A32, A33, A34, A35, A36, A37, A38, A39,
            A40, A41, A42, A43, A44, A45, A46, A47, A48, A49, A50, A51, A52, A53, A54, A55, A56, A57, A58, A59, A60, A61,
            A62, A63, A64, A65, A66, A67, A68, A69;
    }

    [Fact]
    public void MemberMaskLongerThan64Bits_DeltaAndSnapshot()
    {
        Replicator replicator = CreateReplicator();
        var server = new WideObject { A00 = 1, A63 = 2, A64 = 3, A69 = 4 };
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var client = new WideObject();
        replicator.Apply(client, Delta(replicator, baseline));
        Assert.Equal((1, 2, 3, 4), (client.A00, client.A63, client.A64, client.A69));

        server.A01 = 12;
        server.A65 = 11;
        byte[] delta = Delta(replicator, baseline);
        Assert.Equal((70 + 64 + 7) / 8, delta.Length);
        client.A00 = -5;
        replicator.Apply(client, delta);
        Assert.Equal((-5, 12, 11), (client.A00, client.A01, client.A65));

        Assert.True(replicator.TryWriteSnapshot(baseline, out byte[] snapshot));
        var late = new WideObject();
        replicator.Apply(late, snapshot);
        Assert.Equal((1, 12, 2, 3, 11, 4), (late.A00, late.A01, late.A63, late.A64, late.A65, late.A69));
    }

    // ---------- overriding properties ----------

    public class VirtualBase
    {
        [Replicated] public virtual int Value { get; set; }
    }

    public class VirtualDerived : VirtualBase
    {
        public int OverrideCalls;
        private int _value;

        [Replicated]
        public override int Value
        {
            get => _value;
            set
            {
                _value = value;
                OverrideCalls++;
            }
        }
    }

    public class BadOverrideBase
    {
        public virtual int Value { get; set; }
    }

    public class BadOverrideDerived : BadOverrideBase
    {
        [Replicated] public override int Value { get; set; }
    }

    [Fact]
    public void OverridingProperty_BaseDeclarationWins()
    {
        Replicator replicator = CreateReplicator();
        var server = new VirtualDerived { Value = 12 };
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        byte[] data = Delta(replicator, baseline);

        var client = new VirtualDerived();
        replicator.Apply(client, data);
        Assert.Equal(12, client.Value);
        Assert.Equal(1, client.OverrideCalls);

        // Один член: 1 бит маски + 32 бита
        Assert.Equal(5, data.Length);
    }

    public abstract class AbstractBase
    {
        [Replicated] public abstract int Value { get; set; }
    }

    public class AbstractImpl : AbstractBase
    {
        public int SetterCalls;
        private int _value;

        public override int Value
        {
            get => _value;
            set
            {
                _value = value;
                SetterCalls++;
            }
        }
    }

    public class GenericBase<T>
    {
        [Replicated] public virtual T Value { get; set; }
    }

    public class GenericDerived : GenericBase<int>
    {
        [Replicated] public override int Value { get; set; }
    }

    [Fact]
    public void AbstractAndGenericBaseDeclarations_AreReplicatedThroughOverride()
    {
        Replicator replicator = CreateReplicator();
        var server = new AbstractImpl { Value = 7 };
        var client = new AbstractImpl();
        replicator.Apply(client, Delta(replicator, replicator.CreateBaseline(server)));
        Assert.Equal(7, client.Value);
        Assert.Equal(1, client.SetterCalls);

        var genericServer = new GenericDerived { Value = 9 };
        byte[] data = Delta(replicator, replicator.CreateBaseline(genericServer));
        Assert.Equal(5, data.Length);
        var genericClient = new GenericDerived();
        replicator.Apply(genericClient, data);
        Assert.Equal(9, genericClient.Value);
    }

    [Fact]
    public void OverridingProperty_WithoutReplicatedBase_Throws()
    {
        var e = Assert.Throws<ReplicationException>(() => CreateReplicator().CreateBaseline(new BadOverrideDerived()));
        Assert.Contains(typeof(BadOverrideDerived).FullName + ".Value", e.Message);
    }

    // ---------- model errors ----------

    public struct NoCodec
    {
        public int A;
    }

    public class Nested
    {
        [Replicated] public int A;
    }

    public class ReadonlyValue
    {
        [Replicated] public readonly int Value;
    }

    public class GetOnlyValue
    {
        [Replicated] public int Value => 1;
    }

    public class StaticField
    {
        [Replicated] public static int Value;
    }

    public class StaticProperty
    {
        [Replicated] public static int Value { get; set; }
    }

    public class ListMember
    {
        [Replicated] public List<int> Values = new();
    }

    public class ArrayMember
    {
        [Replicated] public int[] Values = [];
    }

    public class DictionaryMember
    {
        [Replicated] public Dictionary<int, int> Values = new();
    }

    public class HashSetMember
    {
        [Replicated] public HashSet<int> Values = new();
    }

    public class StructMember
    {
        [Replicated] public NoCodec Value;
    }

    public class QuantizedObject
    {
        [Replicated, Quantize(0.1)] public Nested Value;
    }

    public class ToleranceObject
    {
        [Replicated(Tolerance = 0.5)] public Nested Value;
    }

    public class DelegateMember
    {
        [Replicated] public Action Value;
    }

    public class SetOnlyProperty
    {
        [Replicated]
        public int Value
        {
            set { }
        }
    }

    public class IndexerMember
    {
        [Replicated]
        public int this[int index]
        {
            get => index;
            set { }
        }
    }

    public class QuantizedString
    {
        [Replicated, Quantize(0.1)] public string Value;
    }

    public class BadQuantize
    {
        [Replicated, Quantize(10, 0, 1)] public float Value;
    }

    public class BadTolerance
    {
        [Replicated(Tolerance = -1)] public float Value;
    }

    public class ValidAfterInvalid
    {
        [Replicated] public int A;
    }

    [Theory]
    [InlineData(typeof(ReadonlyValue), "Value", "writable")]
    [InlineData(typeof(GetOnlyValue), "Value", "writable")]
    [InlineData(typeof(StaticField), "Value", "static")]
    [InlineData(typeof(StaticProperty), "Value", "static")]
    [InlineData(typeof(ListMember), "Values", "ReplicatedList")]
    [InlineData(typeof(ArrayMember), "Values", "ReplicatedList")]
    [InlineData(typeof(DictionaryMember), "Values", "ReplicatedDictionary")]
    [InlineData(typeof(HashSetMember), "Values", "HashSet")]
    [InlineData(typeof(StructMember), "Value", "codec")]
    [InlineData(typeof(QuantizedObject), "Value", "object member")]
    [InlineData(typeof(ToleranceObject), "Value", "object member")]
    [InlineData(typeof(DelegateMember), "Value", "delegate")]
    [InlineData(typeof(SetOnlyProperty), "Value", "getter")]
    [InlineData(typeof(IndexerMember), "Item", "indexer")]
    [InlineData(typeof(QuantizedString), "Value", "Quantize")]
    [InlineData(typeof(BadQuantize), "Value", "")]
    [InlineData(typeof(BadTolerance), "Value", "Tolerance")]
    public void ModelErrors_ThrowWithMemberPath(Type type, string member, string hint)
    {
        Replicator replicator = CreateReplicator();
        object instance = Activator.CreateInstance(type);

        var e = Assert.Throws<ReplicationException>(() => replicator.CreateBaseline(instance));
        Assert.Contains(type.FullName + "." + member, e.Message);
        Assert.Contains(hint, e.Message, StringComparison.OrdinalIgnoreCase);

        // Ошибка не оставляет в кэше недостроенную модель
        Assert.Throws<ReplicationException>(() => replicator.CreateBaseline(instance));
        Assert.Throws<ReplicationException>(() => replicator.GetSchemaHash(type));
        Assert.Throws<ReplicationException>(() => replicator.Apply(instance, new byte[] { 0 }));

        // Другие типы продолжают работать
        replicator.CreateBaseline(new ValidAfterInvalid());
    }

    [Fact]
    public void ModelErrors_ValueTypeTarget_Throws()
    {
        Assert.Throws<ReplicationException>(() => CreateReplicator().CreateBaseline(new NoCodec()));
    }

    public class NoMembers
    {
        public int NotReplicated;
    }

    [Fact]
    public void TypeWithoutMembers_FirstDeltaIsEmptyBody()
    {
        Replicator replicator = CreateReplicator();
        ReplicationBaseline baseline = replicator.CreateBaseline(new NoMembers { NotReplicated = 3 });
        byte[] data = Delta(replicator, baseline);
        Assert.Empty(data);
        Assert.False(replicator.TryWriteDelta(baseline, out _));

        var client = new NoMembers();
        replicator.Apply(client, data);
        Assert.Equal(0, client.NotReplicated);
    }

    // ---------- quantize and tolerance ----------

    public class Mover
    {
        [Replicated, Quantize(0, 100, 0.1)] public float Quantized;
        [Replicated(Tolerance = 0.5)] public float Tolerant;
        [Replicated, Quantize(0.01)] public Vector2 Unbounded;
    }

    [Fact]
    public void QuantizeAndTolerance_EndToEnd()
    {
        Replicator replicator = CreateReplicator(out CollectingSink sink);
        var server = new Mover { Quantized = 50, Tolerant = 10, Unbounded = new Vector2(1, 2) };
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var client = new Mover();
        replicator.Apply(client, Delta(replicator, baseline));
        Assert.Equal(50, client.Quantized, 0.05);
        Assert.Equal(10, client.Tolerant);
        Assert.Equal(1, client.Unbounded.X, 0.005);
        Assert.Equal(2, client.Unbounded.Y, 0.005);

        // Изменения меньше кванта и меньше допуска не отправляются
        server.Quantized = 50.03f;
        server.Tolerant = 10.3f;
        server.Unbounded = new Vector2(1.001f, 2.001f);
        Assert.False(replicator.TryWriteDelta(baseline, out _));

        // Накопленный дрейф отправляется
        server.Tolerant = 10.6f;
        replicator.Apply(client, Delta(replicator, baseline));
        Assert.Equal(10.6f, client.Tolerant);
        Assert.Equal(50, client.Quantized, 0.05);

        // Только квантованный член: 3 бита маски + 10 бит (1000 шагов)
        server.Quantized = 75.4f;
        byte[] data = Delta(replicator, baseline);
        Assert.Equal(2, data.Length);
        replicator.Apply(client, data);
        Assert.Equal(75.4f, client.Quantized, 0.05);

        Assert.Equal(0, sink.Count);
    }

    [Fact]
    public void Quantize_OutOfRange_WarnsOncePerMember()
    {
        Replicator replicator = CreateReplicator(out CollectingSink sink);
        var server = new Mover { Quantized = 500 };
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var client = new Mover();
        replicator.Apply(client, Delta(replicator, baseline));
        Assert.Equal(100, client.Quantized, 0.05);

        for (int i = 0; i < 5; i++)
        {
            server.Quantized = i % 2 == 0 ? -50 - i : 200 + i;
            replicator.TryWriteDelta(baseline, out _);
            Assert.True(replicator.TryWriteSnapshot(baseline, out _));
        }

        Assert.Equal(1, sink.Count);
    }

    // ---------- snapshots ----------

    [Fact]
    public void Snapshot_BeforeFirstDelta_ReturnsFalse()
    {
        Replicator replicator = CreateReplicator();
        ReplicationBaseline baseline = replicator.CreateBaseline(new Simple { X = 1 });

        Assert.False(replicator.TryWriteSnapshot(baseline, out byte[] data));
        Assert.Null(data);

        var writer = new BitWriter();
        writer.WriteBits(1, 1);
        Assert.False(replicator.TryWriteSnapshot(baseline, writer));
        Assert.Equal(1, writer.BitPosition);
    }

    [Fact]
    public void Snapshot_MidFrame_ChangedThenReverted_ClientMatchesServer()
    {
        Replicator replicator = CreateReplicator();
        var server = new Simple { X = 5, Y = 1 };
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var client1 = new Simple();
        replicator.Apply(client1, Delta(replicator, baseline));

        // Середина кадра: значение изменилось, новый клиент получает снимок
        server.X = 7;
        Assert.True(replicator.TryWriteSnapshot(baseline, out byte[] snapshot));
        var client2 = new Simple { X = -100, Y = -100 };
        replicator.Apply(client2, snapshot);

        // До конца кадра значение вернулось: дельта пуста
        server.X = 5;
        Assert.False(replicator.TryWriteDelta(baseline, out _));

        Assert.Equal((server.X, server.Y), (client1.X, client1.Y));
        Assert.Equal((server.X, server.Y), (client2.X, client2.Y));
    }

    [Fact]
    public void Snapshot_MidFrame_ThenEndOfFrameDelta_ClientMatchesServer()
    {
        Replicator replicator = CreateReplicator();
        Player server = Player.CreateFilled();
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var client1 = new Player();
        replicator.Apply(client1, Delta(replicator, baseline));

        server.Health = 10;
        server.Name = "Changed";
        Assert.True(replicator.TryWriteSnapshot(baseline, out byte[] snapshot));
        var client2 = new Player();
        replicator.Apply(client2, snapshot);

        // Снимок строится из базовой копии, а не из живого объекта
        Assert.Equal(100, client2.Health);
        Assert.Equal("Hero", client2.Name);

        server.Position = new Vector2(3, 4);
        byte[] delta = Delta(replicator, baseline);
        replicator.Apply(client1, delta);
        replicator.Apply(client2, delta);
        server.AssertEqual(client1);
        server.AssertEqual(client2);
    }

    [Fact]
    public void Snapshot_DoesNotChangeBaseline()
    {
        Replicator replicator = CreateReplicator();
        var server = new Simple { X = 1, Y = 2 };
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        Delta(replicator, baseline);

        Assert.True(replicator.TryWriteSnapshot(baseline, out byte[] first));
        server.X = 3;
        Assert.True(replicator.TryWriteSnapshot(baseline, out byte[] second));
        Assert.Equal(first, second);

        byte[] delta = Delta(replicator, baseline);
        var client = new Simple();
        replicator.Apply(client, first);
        replicator.Apply(client, delta);
        Assert.Equal((3, 2), (client.X, client.Y));
    }

    // ---------- schema hash ----------

    public static class SchemaBase
    {
        public class Entity
        {
            [Replicated] public int A;
            [Replicated] public float B;
        }
    }

    public static class SchemaSame
    {
        public class Entity
        {
            [Replicated] public float B;
            [Replicated] public int A;
            public int NotReplicated;
        }
    }

    public static class SchemaRenamed
    {
        public class Entity
        {
            [Replicated] public int A;
            [Replicated] public float C;
        }
    }

    public static class SchemaExtraMember
    {
        public class Entity
        {
            [Replicated] public int A;
            [Replicated] public float B;
            [Replicated] public bool D;
        }
    }

    public static class SchemaTypeChanged
    {
        public class Entity
        {
            [Replicated] public long A;
            [Replicated] public float B;
        }
    }

    public static class SchemaQuantized
    {
        public class Entity
        {
            [Replicated] public int A;
            [Replicated, Quantize(0.01)] public float B;
        }
    }

    public static class SchemaQuantizedOther
    {
        public class Entity
        {
            [Replicated] public int A;
            [Replicated, Quantize(0.02)] public float B;
        }
    }

    public static class SchemaQuantizedBounded
    {
        public class Entity
        {
            [Replicated] public int A;
            [Replicated, Quantize(0, 1, 0.01)] public float B;
        }
    }

    public static class SchemaTolerance
    {
        public class Entity
        {
            [Replicated] public int A;
            [Replicated(Tolerance = 0.1)] public float B;
        }
    }

    public static class SchemaStringCodec
    {
        public class Entity
        {
            [Replicated] public string S;
        }
    }

    [Fact]
    public void SchemaHash_StableAcrossReplicators()
    {
        ulong first = CreateReplicator().GetSchemaHash(typeof(Player));
        ulong second = CreateReplicator().GetSchemaHash(typeof(Player));
        Assert.Equal(first, second);

        Replicator replicator = CreateReplicator();
        Assert.Equal(replicator.GetSchemaHash(typeof(Player)), replicator.GetSchemaHash(typeof(Player)));

        // Порядок объявления и нереплицируемые члены не влияют на хэш
        Assert.Equal(replicator.GetSchemaHash(typeof(SchemaBase.Entity)), replicator.GetSchemaHash(typeof(SchemaSame.Entity)));
    }

    [Theory]
    [InlineData(typeof(SchemaRenamed.Entity))]
    [InlineData(typeof(SchemaExtraMember.Entity))]
    [InlineData(typeof(SchemaTypeChanged.Entity))]
    [InlineData(typeof(SchemaQuantized.Entity))]
    [InlineData(typeof(SchemaQuantizedOther.Entity))]
    [InlineData(typeof(SchemaQuantizedBounded.Entity))]
    [InlineData(typeof(SchemaTolerance.Entity))]
    public void SchemaHash_ChangesWithSchema(Type changed)
    {
        Replicator replicator = CreateReplicator();
        Assert.NotEqual(replicator.GetSchemaHash(typeof(SchemaBase.Entity)), replicator.GetSchemaHash(changed));
    }

    [Fact]
    public void SchemaHash_QuantizationVariantsDiffer()
    {
        Replicator replicator = CreateReplicator();
        var hashes = new HashSet<ulong>
        {
            replicator.GetSchemaHash(typeof(SchemaQuantized.Entity)),
            replicator.GetSchemaHash(typeof(SchemaQuantizedOther.Entity)),
            replicator.GetSchemaHash(typeof(SchemaQuantizedBounded.Entity)),
            replicator.GetSchemaHash(typeof(SchemaTolerance.Entity))
        };
        Assert.Equal(4, hashes.Count);
    }

    [Fact]
    public void SchemaHash_IncludesCodecParameters()
    {
        var codecs = new ReplicationCodecs();
        codecs.Register(new StringCodec(16));
        ulong limited = CreateReplicator(codecs).GetSchemaHash(typeof(SchemaStringCodec.Entity));
        ulong standard = CreateReplicator().GetSchemaHash(typeof(SchemaStringCodec.Entity));
        Assert.NotEqual(standard, limited);
    }

    // ---------- errors while writing, recovery ----------

    public struct Custom
    {
        public int Value;
    }

    private sealed class ThrowingCodec : IReplicationCodec<Custom>
    {
        public bool Throw;

        public void Write(BitWriter writer, Custom value)
        {
            if (Throw)
            {
                throw new InvalidOperationException("boom");
            }

            writer.WriteVarInt(value.Value);
        }

        public Custom Read(ref BitReader reader)
        {
            return new Custom { Value = (int)reader.ReadVarInt() };
        }

        public bool IsChanged(Custom lastSent, Custom current)
        {
            return lastSent.Value != current.Value;
        }
    }

    public class WithCustom
    {
        [Replicated] public int A;
        [Replicated] public Custom C;
        [Replicated] public int Z;
    }

    [Fact]
    public void CodecException_IsWrappedWithPath_WriterRewound_NextDeltaIsFull()
    {
        var codec = new ThrowingCodec();
        var codecs = new ReplicationCodecs();
        codecs.Register<Custom>(codec);
        Replicator replicator = CreateReplicator(codecs);

        var server = new WithCustom { A = 1, C = new Custom { Value = 2 }, Z = 3 };
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var client = new WithCustom();
        replicator.Apply(client, Delta(replicator, baseline));

        server.A = 10;
        server.C = new Custom { Value = 20 };
        server.Z = 30;
        codec.Throw = true;

        var writer = new BitWriter();
        writer.WriteBits(0b11, 2);
        byte[] before = writer.ToArray();
        var e = Assert.Throws<ReplicationException>(() => replicator.TryWriteDelta(baseline, writer));
        Assert.Contains(typeof(WithCustom).FullName + ".C", e.Message);
        Assert.IsType<InvalidOperationException>(e.InnerException);
        Assert.Equal(2, writer.BitPosition);
        Assert.Equal(before, writer.ToArray());

        // Базовая копия сброшена: снимка нет, следующая дельта полная
        Assert.False(baseline.IsWritten);
        Assert.False(replicator.TryWriteSnapshot(baseline, out _));

        codec.Throw = false;
        byte[] recovered = Delta(replicator, baseline);
        byte[] expectedFull = Delta(replicator, replicator.CreateBaseline(server));
        Assert.Equal(expectedFull, recovered);

        var freshClient = new WithCustom { A = -1, C = new Custom { Value = -1 }, Z = -1 };
        replicator.Apply(freshClient, recovered);
        Assert.Equal((10, 20, 30), (freshClient.A, freshClient.C.Value, freshClient.Z));
    }

    public enum Small
    {
        A,
        B
    }

    public class WithEnum
    {
        [Replicated] public Small Value;
    }

    [Fact]
    public void ReplicationExceptionFromCodec_IsWrappedWithPath()
    {
        Replicator replicator = CreateReplicator();
        var server = new WithEnum { Value = (Small)7 };
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var e = Assert.Throws<ReplicationException>(() => replicator.TryWriteDelta(baseline, out _));
        Assert.Contains(typeof(WithEnum).FullName + ".Value", e.Message);

        server.Value = Small.B;
        var client = new WithEnum();
        replicator.Apply(client, Delta(replicator, baseline));
        Assert.Equal(Small.B, client.Value);
    }

    // ---------- malformed input ----------

    [Fact]
    public void TrailingData_IsRejected()
    {
        Replicator replicator = CreateReplicator();
        ReplicationBaseline baseline = replicator.CreateBaseline(new Simple { X = 1, Y = 2 });
        byte[] data = Delta(replicator, baseline);

        // Хвост меньше байта (дополнение последнего байта) допустим
        replicator.Apply(new Simple(), data);

        byte[] withTrailing = data.Append((byte)0).ToArray();
        Assert.Throws<ReplicationFormatException>(() => replicator.Apply(new Simple(), withTrailing));
    }

    [Fact]
    public void TruncatedData_ThrowsFormatException()
    {
        Replicator replicator = CreateReplicator();
        Player server = Player.CreateFilled();
        byte[] data = Delta(replicator, replicator.CreateBaseline(server));

        for (int length = 0; length < data.Length; length++)
        {
            byte[] truncated = data.AsSpan(0, length).ToArray();
            Assert.Throws<ReplicationFormatException>(() => replicator.Apply(new Player(), truncated));
        }
    }

    [Fact]
    public void FormatExceptionFromMember_ContainsPath()
    {
        Replicator replicator = CreateReplicator();

        // Маска: только X, затем 8 бит вместо 32
        var writer = new BitWriter();
        writer.WriteBits(0b01, 2);
        writer.WriteBits(0xFF, 6);
        var e = Assert.Throws<ReplicationFormatException>(() => replicator.Apply(new Simple(), writer.ToArray()));
        Assert.Contains(typeof(Simple).FullName + ".X", e.Message);
    }

    // ---------- argument validation ----------

    [Fact]
    public void Arguments_AreValidated()
    {
        Replicator replicator = CreateReplicator();
        ReplicationBaseline baseline = replicator.CreateBaseline(new Simple());

        Assert.Throws<ArgumentNullException>(() => replicator.CreateBaseline(null));
        Assert.Throws<ArgumentNullException>(() => replicator.TryWriteDelta(null, out _));
        Assert.Throws<ArgumentNullException>(() => replicator.TryWriteDelta(null, new BitWriter()));
        Assert.Throws<ArgumentNullException>(() => replicator.TryWriteDelta(baseline, (BitWriter)null));
        Assert.Throws<ArgumentNullException>(() => replicator.TryWriteSnapshot(null, out _));
        Assert.Throws<ArgumentNullException>(() => replicator.TryWriteSnapshot(baseline, (BitWriter)null));
        Assert.Throws<ArgumentNullException>(() => replicator.Apply(null, new byte[1]));
        Assert.Throws<ArgumentNullException>(() => replicator.GetSchemaHash(null));

        Replicator other = CreateReplicator();
        Assert.Throws<ReplicationException>(() => other.TryWriteDelta(baseline, out _));
        Assert.Throws<ReplicationException>(() => other.TryWriteDelta(baseline, new BitWriter()));
        Assert.Throws<ReplicationException>(() => other.TryWriteSnapshot(baseline, out _));

        Assert.Throws<ArgumentOutOfRangeException>(() => new Replicator(limits: new ReplicationLimits { MaxDepth = 0 },
            logger: new LoggerConfiguration().CreateLogger()));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Replicator(limits: new ReplicationLimits { MaxCollectionCount = 0 },
            logger: new LoggerConfiguration().CreateLogger()));
    }

    // ---------- allocations ----------

    [Fact]
    public void UnchangedDelta_DoesNotAllocate()
    {
        Replicator replicator = CreateReplicator();
        Player server = Player.CreateFilled();
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var writer = new BitWriter(1024);
        Assert.True(replicator.TryWriteDelta(baseline, writer));
        writer.Reset();
        Assert.False(replicator.TryWriteDelta(baseline, writer));

        AllocationAssert.DoesNotAllocate(() =>
        {
            for (int i = 0; i < 1000; i++)
            {
                replicator.TryWriteDelta(baseline, writer);
            }
        });
    }

    [Fact]
    public void ChangedDeltaAndApply_ValueMembers_DoNotAllocate()
    {
        Replicator replicator = CreateReplicator();
        var server = new Mover { Quantized = 1, Tolerant = 1, Unbounded = new Vector2(1, 1) };
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var writer = new BitWriter(1024);
        var client = new Mover();

        void Frame(int i)
        {
            writer.Reset();
            server.Quantized = i % 100;
            server.Tolerant = i;
            server.Unbounded = new Vector2(i, -i);
            replicator.TryWriteDelta(baseline, writer);
            var reader = new BitReader(writer.AsSpan());
            replicator.Apply(client, ref reader);
        }

        Frame(1);
        Frame(2);

        AllocationAssert.DoesNotAllocate(() =>
        {
            for (int i = 3; i < 1000; i++)
            {
                Frame(i);
            }
        });
        Assert.Equal(999, client.Tolerant);
    }
}
