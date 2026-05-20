using RepliCAT;
using RepliCAT.Bits;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace RepliCAT.Tests;

public class NestedObjectTests
{
    // ---------- test infrastructure ----------

    private sealed class CollectingSink : ILogEventSink
    {
        public int Count;

        public void Emit(LogEvent logEvent)
        {
            Interlocked.Increment(ref Count);
        }
    }

    /// <summary>
    /// Поддельное отображение типов (реальное отображение в тестах не создается).
    /// </summary>
    private sealed class FakeTypeIds : ITypeIdMapping
    {
        private readonly Dictionary<Type, int> _ids = new();
        private readonly Dictionary<int, Type> _types = new();

        public FakeTypeIds Add(Type type, int id)
        {
            _ids.Add(type, id);
            _types.Add(id, type);
            return this;
        }

        public int GetId(Type type)
        {
            return _ids[type];
        }

        public Type GetType(int id)
        {
            return _types[id];
        }
    }

    private static Replicator CreateReplicator(ITypeIdMapping typeIds = null, ReplicationLimits limits = null)
    {
        ILogger logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(new CollectingSink()).CreateLogger();
        return new Replicator(typeIds, limits: limits, logger: logger);
    }

    private static FakeTypeIds CreateTypeIds()
    {
        return new FakeTypeIds()
            .Add(typeof(Stats), 1)
            .Add(typeof(BuffedStats), 2)
            .Add(typeof(Rifle), 3)
            .Add(typeof(Pistol), 4)
            .Add(typeof(Circle), 5)
            .Add(typeof(Square), 6)
            .Add(typeof(Unrelated), 7);
    }

    private static byte[] Delta(Replicator replicator, ReplicationBaseline baseline)
    {
        Assert.True(replicator.TryWriteDelta(baseline, out byte[] data));
        return data;
    }

    private static byte[] Snapshot(Replicator replicator, ReplicationBaseline baseline)
    {
        Assert.True(replicator.TryWriteSnapshot(baseline, out byte[] data));
        return data;
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        for (int index = text.IndexOf(value, StringComparison.Ordinal);
             index >= 0;
             index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    // ---------- test types ----------

    public class Stats
    {
        [Replicated] public int Hp;
        [Replicated] public float Speed;
    }

    public class BuffedStats : Stats
    {
        [Replicated] public int Bonus;
    }

    public class Unrelated
    {
        [Replicated] public int Value;
    }

    public class Unit
    {
        private Stats _stats;

        public int StatsSetterCalls;

        [Replicated] public int Id;

        [Replicated]
        public Stats Stats
        {
            get => _stats;
            set
            {
                _stats = value;
                StatsSetterCalls++;
            }
        }
    }

    private static void AssertEqual(Unit expected, Unit actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        if (expected.Stats == null)
        {
            Assert.Null(actual.Stats);
            return;
        }

        Assert.NotNull(actual.Stats);
        Assert.Equal(expected.Stats.GetType(), actual.Stats.GetType());
        Assert.Equal(expected.Stats.Hp, actual.Stats.Hp);
        Assert.Equal(expected.Stats.Speed, actual.Stats.Speed);
        if (expected.Stats is BuffedStats buffed)
        {
            Assert.Equal(buffed.Bonus, ((BuffedStats)actual.Stats).Bonus);
        }
    }

    // ---------- nested changes and instance reuse ----------

    [Fact]
    public void NestedChange_IsAppliedIntoSameClientInstance()
    {
        Replicator replicator = CreateReplicator();
        var server = new Unit { Id = 1, Stats = new Stats { Hp = 100, Speed = 2.5f } };
        ReplicationBaseline baseline = replicator.CreateBaseline(server);

        var client = new Unit();
        replicator.Apply(client, Delta(replicator, baseline));
        AssertEqual(server, client);
        Stats clientStats = client.Stats;
        Assert.NotNull(clientStats);
        Assert.NotSame(server.Stats, clientStats);
        Assert.Equal(1, client.StatsSetterCalls);

        Assert.False(replicator.TryWriteDelta(baseline, out _));

        server.Stats.Hp = 50;
        byte[] delta = Delta(replicator, baseline);
        replicator.Apply(client, delta);
        Assert.Same(clientStats, client.Stats);
        Assert.Equal(50, client.Stats.Hp);
        Assert.Equal(1, client.StatsSetterCalls);

        // Изменение одного вложенного члена: маска корня (2) + present + isDeclared + маска Stats (2) + int
        Assert.Equal(5, delta.Length);
        Assert.False(replicator.TryWriteDelta(baseline, out _));
    }

    [Fact]
    public void ReplacementWithSameType_ClientReusesInstance_ReceivesAllMembers()
    {
        Replicator replicator = CreateReplicator();
        var server = new Unit { Id = 1, Stats = new Stats { Hp = 100, Speed = 2.5f } };
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var client = new Unit();
        replicator.Apply(client, Delta(replicator, baseline));
        Stats clientStats = client.Stats;

        // Сентинелы: новая ссылка на сервере должна прислать все члены
        clientStats.Hp = -1;
        clientStats.Speed = -1;

        server.Stats = new Stats { Hp = 100, Speed = 2.5f };
        replicator.Apply(client, Delta(replicator, baseline));
        Assert.Same(clientStats, client.Stats);
        Assert.Equal(1, client.StatsSetterCalls);
        AssertEqual(server, client);

        // Новая ссылка стала базовой: следующий кадр без изменений пуст
        Assert.False(replicator.TryWriteDelta(baseline, out _));
        server.Stats.Speed = 9;
        replicator.Apply(client, Delta(replicator, baseline));
        Assert.Same(clientStats, client.Stats);
        Assert.Equal(9, client.Stats.Speed);
    }

    [Fact]
    public void ReplacementWithSubtype_ClientCreatesSubtypeThroughTypeIdMapping()
    {
        Replicator replicator = CreateReplicator(CreateTypeIds());
        var server = new Unit { Id = 1, Stats = new Stats { Hp = 10 } };
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var client = new Unit();
        replicator.Apply(client, Delta(replicator, baseline));
        Assert.IsType<Stats>(client.Stats);

        server.Stats = new BuffedStats { Hp = 20, Speed = 1, Bonus = 5 };
        replicator.Apply(client, Delta(replicator, baseline));
        var clientBuffed = Assert.IsType<BuffedStats>(client.Stats);
        Assert.Equal(2, client.StatsSetterCalls);
        AssertEqual(server, client);

        // Дальнейшие изменения подтипа попадают в тот же экземпляр
        ((BuffedStats)server.Stats).Bonus = 6;
        replicator.Apply(client, Delta(replicator, baseline));
        Assert.Same(clientBuffed, client.Stats);
        Assert.Equal(6, clientBuffed.Bonus);

        // Обратно к объявленному типу: клиент создает экземпляр Stats
        server.Stats = new Stats { Hp = 30 };
        replicator.Apply(client, Delta(replicator, baseline));
        Assert.IsType<Stats>(client.Stats);
        Assert.Equal(3, client.StatsSetterCalls);
        AssertEqual(server, client);
    }

    [Fact]
    public void Subtype_WithoutMapping_ThrowsReplicationException()
    {
        Replicator replicator = CreateReplicator();
        var server = new Unit { Stats = new BuffedStats() };
        ReplicationBaseline baseline = replicator.CreateBaseline(server);

        var e = Assert.Throws<ReplicationException>(() => replicator.TryWriteDelta(baseline, out _));
        Assert.IsNotType<ReplicationFormatException>(e);
        Assert.Contains(typeof(Unit).FullName + ".Stats", e.Message);
        Assert.Contains("ITypeIdMapping", e.Message);
        Assert.False(baseline.IsWritten);

        // Восстановление: после исправления следующая дельта полная
        server.Stats = new Stats { Hp = 3 };
        var client = new Unit();
        replicator.Apply(client, Delta(replicator, baseline));
        AssertEqual(server, client);
    }

    [Fact]
    public void Subtype_NotRegisteredInMapping_ThrowsReplicationException()
    {
        Replicator replicator = CreateReplicator(new FakeTypeIds().Add(typeof(Stats), 1));
        ReplicationBaseline baseline = replicator.CreateBaseline(new Unit { Stats = new BuffedStats() });

        var e = Assert.Throws<ReplicationException>(() => replicator.TryWriteDelta(baseline, out _));
        Assert.IsNotType<ReplicationFormatException>(e);
        Assert.Contains(typeof(Unit).FullName + ".Stats", e.Message);
        Assert.Contains(typeof(BuffedStats).FullName, e.Message);
    }

    [Fact]
    public void NullTransitions_InBothDirections()
    {
        Replicator replicator = CreateReplicator();
        var server = new Unit { Id = 1 };
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var client = new Unit { Stats = new Stats { Hp = 99 } };

        // Первая дельта пишет null явно
        replicator.Apply(client, Delta(replicator, baseline));
        Assert.Null(client.Stats);
        Assert.False(replicator.TryWriteDelta(baseline, out _));

        // null → объект
        server.Stats = new Stats { Hp = 5, Speed = 1 };
        replicator.Apply(client, Delta(replicator, baseline));
        AssertEqual(server, client);
        Stats clientStats = client.Stats;

        // объект → null
        server.Stats = null;
        replicator.Apply(client, Delta(replicator, baseline));
        Assert.Null(client.Stats);
        Assert.False(replicator.TryWriteDelta(baseline, out _));

        // null → объект снова: клиент создает новый экземпляр
        server.Stats = new Stats { Hp = 6 };
        replicator.Apply(client, Delta(replicator, baseline));
        AssertEqual(server, client);
        Assert.NotSame(clientStats, client.Stats);
    }

    // ---------- setter rules ----------

    public class GetOnlyHolder
    {
        private Stats _stats;

        public GetOnlyHolder(bool withInstance)
        {
            _stats = withInstance ? new Stats() : null;
            Readonly = new Stats();
        }

        private GetOnlyHolder() : this(true)
        {
        }

        [Replicated] public Stats Stats => _stats;

        [Replicated] public readonly Stats Readonly;

        public void SetStats(Stats stats)
        {
            _stats = stats;
        }
    }

    [Fact]
    public void GetOnlyNestedMember_WorksWhileInstanceIsReused()
    {
        Replicator replicator = CreateReplicator();
        var server = new GetOnlyHolder(true);
        server.Stats.Hp = 7;
        server.Readonly.Speed = 3;
        ReplicationBaseline baseline = replicator.CreateBaseline(server);

        var client = new GetOnlyHolder(true);
        Stats clientStats = client.Stats;
        Stats clientReadonly = client.Readonly;
        replicator.Apply(client, Delta(replicator, baseline));
        Assert.Same(clientStats, client.Stats);
        Assert.Same(clientReadonly, client.Readonly);
        Assert.Equal(7, client.Stats.Hp);
        Assert.Equal(3, client.Readonly.Speed);

        // Замена того же типа на сервере: клиент переиспользует экземпляр, сеттер не нужен
        server.SetStats(new Stats { Hp = 8 });
        replicator.Apply(client, Delta(replicator, baseline));
        Assert.Same(clientStats, client.Stats);
        Assert.Equal(8, client.Stats.Hp);
    }

    [Fact]
    public void GetOnlyNestedMember_Throws_WhenClientMustCreateInstance()
    {
        Replicator replicator = CreateReplicator();
        var server = new GetOnlyHolder(true);
        byte[] data = Delta(replicator, replicator.CreateBaseline(server));

        var client = new GetOnlyHolder(false);
        string path = typeof(GetOnlyHolder).FullName + ".Stats";
        var e = Assert.Throws<ReplicationException>(() => replicator.Apply(client, data));
        Assert.Contains(path, e.Message);
        Assert.Contains("setter", e.Message);

        // Путь в сообщении не дублируется
        Assert.Equal(1, CountOccurrences(e.Message, path));
    }

    [Fact]
    public void GetOnlyNestedMember_Throws_WhenServerSendsNull()
    {
        Replicator replicator = CreateReplicator();
        var server = new GetOnlyHolder(false);
        byte[] data = Delta(replicator, replicator.CreateBaseline(server));

        var e = Assert.Throws<ReplicationException>(() => replicator.Apply(new GetOnlyHolder(true), data));
        Assert.Contains(typeof(GetOnlyHolder).FullName + ".Stats", e.Message);

        // Клиенту, у которого и так null, присваивать нечего
        var client = new GetOnlyHolder(false);
        replicator.Apply(client, data);
        Assert.Null(client.Stats);
    }

    public class NoDefaultConstructor
    {
        public NoDefaultConstructor(int value)
        {
            Value = value;
        }

        [Replicated] public int Value;
    }

    public class PrivateConstructor
    {
        private PrivateConstructor()
        {
        }

        [Replicated] public int Value;

        public static PrivateConstructor Create(int value)
        {
            return new PrivateConstructor { Value = value };
        }
    }

    public class ConstructorHolder
    {
        [Replicated] public NoDefaultConstructor NoDefault;
        [Replicated] public PrivateConstructor Private;
    }

    [Fact]
    public void InstanceCreation_UsesNonPublicConstructor_AndRequiresParameterlessOne()
    {
        Replicator replicator = CreateReplicator();
        var server = new ConstructorHolder { Private = PrivateConstructor.Create(4) };
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var client = new ConstructorHolder();
        replicator.Apply(client, Delta(replicator, baseline));
        Assert.Equal(4, client.Private.Value);

        // Экземпляр переиспользуется, конструктор не нужен
        server.NoDefault = new NoDefaultConstructor(5);
        client.NoDefault = new NoDefaultConstructor(0);
        replicator.Apply(client, Delta(replicator, baseline));
        Assert.Equal(5, client.NoDefault.Value);

        // Клиенту нужно создать экземпляр без конструктора по умолчанию
        server.NoDefault = new NoDefaultConstructor(6);
        byte[] data = Delta(replicator, baseline);
        var e = Assert.Throws<ReplicationException>(() => replicator.Apply(new ConstructorHolder(), data));
        Assert.IsNotType<ReplicationFormatException>(e);
        Assert.Contains(typeof(ConstructorHolder).FullName + ".NoDefault", e.Message);
        Assert.Contains("parameterless", e.Message);
    }

    // ---------- polymorphism: abstract and interface declared types ----------

    public abstract class Weapon
    {
        [Replicated] public int Ammo;
    }

    public class Rifle : Weapon
    {
        [Replicated] public float Zoom;
    }

    public class Pistol : Weapon
    {
        [Replicated] public bool Silenced;
    }

    public class Armory
    {
        [Replicated] public Weapon Weapon;
    }

    public interface IShape
    {
    }

    public class Circle : IShape
    {
        [Replicated] public float Radius;
    }

    public class Square : IShape
    {
        [Replicated] public float Side;
    }

    public class Drawing
    {
        [Replicated] public IShape Shape;
    }

    [Fact]
    public void AbstractDeclaredType_ReplicatesSubtypes()
    {
        Replicator replicator = CreateReplicator(CreateTypeIds());
        var server = new Armory { Weapon = new Rifle { Ammo = 30, Zoom = 4 } };
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var client = new Armory();
        replicator.Apply(client, Delta(replicator, baseline));
        var rifle = Assert.IsType<Rifle>(client.Weapon);
        Assert.Equal((30, 4f), (rifle.Ammo, rifle.Zoom));

        server.Weapon.Ammo = 29;
        replicator.Apply(client, Delta(replicator, baseline));
        Assert.Same(rifle, client.Weapon);
        Assert.Equal(29, rifle.Ammo);

        server.Weapon = new Pistol { Ammo = 12, Silenced = true };
        replicator.Apply(client, Delta(replicator, baseline));
        var pistol = Assert.IsType<Pistol>(client.Weapon);
        Assert.Equal((12, true), (pistol.Ammo, pistol.Silenced));
    }

    [Fact]
    public void InterfaceDeclaredType_ReplicatesImplementations()
    {
        Replicator replicator = CreateReplicator(CreateTypeIds());
        var server = new Drawing { Shape = new Circle { Radius = 2 } };
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var client = new Drawing();
        replicator.Apply(client, Delta(replicator, baseline));
        var circle = Assert.IsType<Circle>(client.Shape);
        Assert.Equal(2, circle.Radius);

        ((Circle)server.Shape).Radius = 3;
        replicator.Apply(client, Delta(replicator, baseline));
        Assert.Same(circle, client.Shape);
        Assert.Equal(3, circle.Radius);

        server.Shape = new Square { Side = 5 };
        replicator.Apply(client, Delta(replicator, baseline));
        Assert.Equal(5, Assert.IsType<Square>(client.Shape).Side);

        server.Shape = null;
        replicator.Apply(client, Delta(replicator, baseline));
        Assert.Null(client.Shape);
    }

    public sealed class SealedStats
    {
        [Replicated] public int Hp;
    }

    public class SealedHolder
    {
        [Replicated] public SealedStats Stats = new();
    }

    public class UnsealedHolder
    {
        [Replicated] public Stats Stats = new();
    }

    [Fact]
    public void SealedDeclaredType_WritesNoTypeInfo()
    {
        Replicator replicator = CreateReplicator();

        var sealedWriter = new BitWriter();
        Assert.True(replicator.TryWriteDelta(replicator.CreateBaseline(new SealedHolder()), sealedWriter));
        // маска (1) + present (1) + маска SealedStats (1) + int (32)
        Assert.Equal(35, sealedWriter.BitPosition);

        var unsealedWriter = new BitWriter();
        Assert.True(replicator.TryWriteDelta(replicator.CreateBaseline(new UnsealedHolder()), unsealedWriter));
        // маска (1) + present (1) + isDeclaredType (1) + маска Stats (2) + int (32) + float (32)
        Assert.Equal(69, unsealedWriter.BitPosition);
    }

    // ---------- malformed data ----------

    private static byte[] Craft(Action<BitWriter> write)
    {
        var writer = new BitWriter();
        write(writer);
        return writer.ToArray();
    }

    private static byte[] ArmoryWithTypeId(ulong id)
    {
        return Craft(w =>
        {
            w.WriteBool(true); // маска Armory
            w.WriteBool(true); // present
            w.WriteBool(false); // isDeclaredType
            w.WriteVarUInt(id);
            w.WriteBits(0, 2); // пустая маска
        });
    }

    [Fact]
    public void UnknownTypeId_ThrowsFormatException()
    {
        Replicator replicator = CreateReplicator(CreateTypeIds());
        var e = Assert.Throws<ReplicationFormatException>(() => replicator.Apply(new Armory(), ArmoryWithTypeId(99)));
        Assert.Contains(typeof(Armory).FullName + ".Weapon", e.Message);

        Assert.Throws<ReplicationFormatException>(() => replicator.Apply(new Armory(), ArmoryWithTypeId(uint.MaxValue)));
        Assert.Throws<ReplicationFormatException>(() => replicator.Apply(new Armory(), ArmoryWithTypeId(ulong.MaxValue)));

        // Идентификатор типа без настроенного отображения
        Assert.Throws<ReplicationFormatException>(() => CreateReplicator().Apply(new Armory(), ArmoryWithTypeId(3)));

        // Корректный идентификатор работает
        var armory = new Armory();
        replicator.Apply(armory, ArmoryWithTypeId(3));
        Assert.IsType<Rifle>(armory.Weapon);
    }

    [Fact]
    public void NonAssignableTypeId_ThrowsFormatException()
    {
        Replicator replicator = CreateReplicator(CreateTypeIds());
        var e = Assert.Throws<ReplicationFormatException>(() => replicator.Apply(new Armory(), ArmoryWithTypeId(7)));
        Assert.Contains(typeof(Unrelated).FullName, e.Message);

        // Stats не наследует Weapon
        Assert.Throws<ReplicationFormatException>(() => replicator.Apply(new Armory(), ArmoryWithTypeId(1)));
    }

    [Fact]
    public void DeclaredFlag_ForAbstractDeclaredType_ThrowsFormatException()
    {
        Replicator replicator = CreateReplicator(CreateTypeIds());
        byte[] data = Craft(w =>
        {
            w.WriteBool(true); // маска Armory
            w.WriteBool(true); // present
            w.WriteBool(true); // isDeclaredType, но Weapon абстрактный
            w.WriteBits(0, 1);
        });

        Assert.Throws<ReplicationFormatException>(() => replicator.Apply(new Armory(), data));
    }

    [Fact]
    public void TruncatedNestedData_ThrowsFormatException()
    {
        Replicator replicator = CreateReplicator();
        byte[] data = Delta(replicator, replicator.CreateBaseline(new Unit { Stats = new Stats { Hp = 1 } }));
        Assert.Throws<ReplicationFormatException>(() => replicator.Apply(new Unit(), data.AsSpan(0, data.Length - 2)));
    }

    // ---------- depth, cycles, recursive types ----------

    public class Link
    {
        [Replicated] public int Value;
        [Replicated] public Link Next;
    }

    [Fact]
    public void Cycle_HitsDepthLimit_WithClearException()
    {
        Replicator replicator = CreateReplicator();
        var a = new Link { Value = 1 };
        var b = new Link { Value = 2, Next = a };
        a.Next = b;
        ReplicationBaseline baseline = replicator.CreateBaseline(a);

        var e = Assert.Throws<ReplicationException>(() => replicator.TryWriteDelta(baseline, out _));
        Assert.IsNotType<ReplicationFormatException>(e);
        Assert.Contains("depth", e.Message);
        string path = typeof(Link).FullName + ".Next";
        Assert.Contains(path, e.Message);
        Assert.Equal(1, CountOccurrences(e.Message, path));
        Assert.False(baseline.IsWritten);

        // Цикл разорван: глубина сброшена, следующая дельта полная
        b.Next = null;
        var client = new Link();
        replicator.Apply(client, Delta(replicator, baseline));
        Assert.Equal(1, client.Value);
        Assert.Equal(2, client.Next.Value);
        Assert.Null(client.Next.Next);
    }

    private static Link Chain(int nested)
    {
        var root = new Link { Value = 0 };
        Link current = root;
        for (int i = 1; i <= nested; i++)
        {
            current.Next = new Link { Value = i };
            current = current.Next;
        }

        return root;
    }

    [Fact]
    public void DepthLimit_OnWrite_AllowsExactlyMaxDepthNestedObjects()
    {
        Replicator replicator = CreateReplicator(limits: new ReplicationLimits { MaxDepth = 3 });
        var client = new Link();
        replicator.Apply(client, Delta(replicator, replicator.CreateBaseline(Chain(3))));
        Assert.Equal(3, client.Next.Next.Next.Value);

        ReplicationBaseline tooDeep = replicator.CreateBaseline(Chain(4));
        var e = Assert.Throws<ReplicationException>(() => replicator.TryWriteDelta(tooDeep, out _));
        Assert.Contains("depth", e.Message);
    }

    [Fact]
    public void Depth_DoesNotAccumulateAcrossFrames()
    {
        // При MaxDepth = 3 любой пропущенный Exit быстро превысил бы лимит на корректных данных.
        Replicator replicator = CreateReplicator(limits: new ReplicationLimits { MaxDepth = 3 });
        Link server = Chain(3);
        Link third = server.Next.Next.Next;
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var client = new Link();
        var lateClient = new Link();

        for (int frame = 0; frame < 200; frame++)
        {
            switch (frame % 5)
            {
                case 0:
                    third.Value = frame;
                    break;
                case 1:
                    // без изменений: ранний возврат на каждом уровне
                    break;
                case 2:
                    server.Next.Next.Next = null;
                    break;
                case 3:
                    server.Next.Next.Next = third;
                    break;
                case 4:
                    server.Next.Value = frame;
                    break;
            }

            if (replicator.TryWriteDelta(baseline, out byte[] data))
            {
                replicator.Apply(client, data);
            }

            replicator.Apply(lateClient, Snapshot(replicator, baseline));
            Assert.Equal(server.Next.Value, client.Next.Value);
            Assert.Equal(server.Next.Next.Next?.Value, client.Next.Next.Next?.Value);
            Assert.Equal(server.Next.Next.Next?.Value, lateClient.Next.Next.Next?.Value);
        }
    }

    [Fact]
    public void DepthLimit_OnRead_ThrowsFormatException()
    {
        Replicator server = CreateReplicator(limits: new ReplicationLimits { MaxDepth = 10 });
        byte[] data = Delta(server, server.CreateBaseline(Chain(3)));

        Replicator strict = CreateReplicator(limits: new ReplicationLimits { MaxDepth = 2 });
        var e = Assert.Throws<ReplicationFormatException>(() => strict.Apply(new Link(), data));
        Assert.Contains("depth", e.Message);

        // После ошибки глубина сброшена
        var client = new Link();
        strict.Apply(client, Delta(server, server.CreateBaseline(Chain(2))));
        Assert.Equal(2, client.Next.Next.Value);
    }

    public class TreeNode
    {
        [Replicated] public int Value;
        [Replicated] public TreeNode Left;
        [Replicated] public TreeNode Right;
    }

    private static void AssertTreeEqual(TreeNode expected, TreeNode actual)
    {
        if (expected == null)
        {
            Assert.Null(actual);
            return;
        }

        Assert.NotNull(actual);
        Assert.Equal(expected.Value, actual.Value);
        AssertTreeEqual(expected.Left, actual.Left);
        AssertTreeEqual(expected.Right, actual.Right);
    }

    [Fact]
    public void RecursiveType_Replicates()
    {
        Replicator replicator = CreateReplicator();
        var server = new TreeNode
        {
            Value = 1,
            Left = new TreeNode { Value = 2, Left = new TreeNode { Value = 4 } },
            Right = new TreeNode { Value = 3, Right = new TreeNode { Value = 5 } }
        };
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var client = new TreeNode();
        replicator.Apply(client, Delta(replicator, baseline));
        AssertTreeEqual(server, client);

        TreeNode clientDeepLeaf = client.Right.Right;
        server.Right.Right.Value = 50;
        byte[] delta = Delta(replicator, baseline);
        replicator.Apply(client, delta);
        Assert.Same(clientDeepLeaf, client.Right.Right);
        AssertTreeEqual(server, client);

        server.Left.Left = null;
        server.Right.Left = new TreeNode { Value = 6 };
        replicator.Apply(client, Delta(replicator, baseline));
        AssertTreeEqual(server, client);
        Assert.Same(clientDeepLeaf, client.Right.Right);

        Assert.False(replicator.TryWriteDelta(baseline, out _));
    }

    // ---------- snapshot from the baseline ----------

    [Fact]
    public void MidFrameSnapshot_PlusDelta_WithNestedObjects()
    {
        Replicator replicator = CreateReplicator(CreateTypeIds());
        var server = new Unit { Id = 1, Stats = new Stats { Hp = 10, Speed = 1 } };
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var client = new Unit();
        replicator.Apply(client, Delta(replicator, baseline));

        // Вложенное значение изменилось после последней дельты и вернулось до следующей
        server.Stats.Hp = 70;
        var late1 = new Unit();
        replicator.Apply(late1, Snapshot(replicator, baseline));
        server.Stats.Hp = 10;
        Assert.False(replicator.TryWriteDelta(baseline, out _));
        AssertEqual(server, late1);

        // Замена на подтип посреди кадра: снимок содержит базовую копию, дельта — замену
        server.Stats = new BuffedStats { Hp = 11, Bonus = 3 };
        var late2 = new Unit();
        replicator.Apply(late2, Snapshot(replicator, baseline));
        Assert.IsType<Stats>(late2.Stats);
        server.Stats.Speed = 4;
        byte[] delta = Delta(replicator, baseline);
        foreach (Unit c in new[] { client, late1, late2 })
        {
            replicator.Apply(c, delta);
            AssertEqual(server, c);
        }

        // Снимок после замены несет подтип
        var late3 = new Unit();
        replicator.Apply(late3, Snapshot(replicator, baseline));
        AssertEqual(server, late3);

        // null посреди кадра и снова объект
        server.Stats = null;
        var late4 = new Unit();
        replicator.Apply(late4, Snapshot(replicator, baseline));
        Assert.IsType<BuffedStats>(late4.Stats);
        delta = Delta(replicator, baseline);
        foreach (Unit c in new[] { client, late1, late2, late3, late4 })
        {
            replicator.Apply(c, delta);
            AssertEqual(server, c);
        }

        var late5 = new Unit { Stats = new Stats() };
        replicator.Apply(late5, Snapshot(replicator, baseline));
        AssertEqual(server, late5);

        server.Stats = new Stats { Hp = 1 };
        delta = Delta(replicator, baseline);
        foreach (Unit c in new[] { client, late1, late2, late3, late4, late5 })
        {
            replicator.Apply(c, delta);
            AssertEqual(server, c);
        }
    }

    [Fact]
    public void Snapshot_OfRecursiveTree_MatchesBaseline()
    {
        Replicator replicator = CreateReplicator();
        var server = new TreeNode { Value = 1, Left = new TreeNode { Value = 2 } };
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        Delta(replicator, baseline);

        server.Left.Value = 20;
        server.Right = new TreeNode { Value = 30 };
        var late = new TreeNode();
        replicator.Apply(late, Snapshot(replicator, baseline));
        Assert.Equal(2, late.Left.Value);
        Assert.Null(late.Right);

        replicator.Apply(late, Delta(replicator, baseline));
        AssertTreeEqual(server, late);
    }

    // ---------- model errors in nested types ----------

    public class BadNested
    {
        [Replicated] public List<int> Values = new();
    }

    public class HasBadNested
    {
        [Replicated] public int A;
        [Replicated] public BadNested Nested;
    }

    [Fact]
    public void NestedModelError_SurfacesAtRootModel_AndIsNotCached()
    {
        Replicator replicator = CreateReplicator();
        var e = Assert.Throws<ReplicationException>(() => replicator.CreateBaseline(new HasBadNested()));
        Assert.Contains(typeof(BadNested).FullName + ".Values", e.Message);

        Assert.Throws<ReplicationException>(() => replicator.CreateBaseline(new HasBadNested()));
        Assert.Throws<ReplicationException>(() => replicator.GetSchemaHash(typeof(BadNested)));
    }

    // ---------- schema hash ----------

    public static class SchemaA
    {
        public class Stats
        {
            [Replicated] public int A;
        }
    }

    public static class SchemaB
    {
        public class Stats
        {
            [Replicated] public float B;
        }
    }

    public static class RefFirst
    {
        public class Root
        {
            [Replicated] public SchemaA.Stats X;
            [Replicated] public SchemaB.Stats Y;
            [Replicated] public SchemaA.Stats Z;
        }
    }

    public static class RefSecond
    {
        public class Root
        {
            [Replicated] public SchemaA.Stats X;
            [Replicated] public SchemaB.Stats Y;
            [Replicated] public SchemaB.Stats Z;
        }
    }

    public static class NestedChanged
    {
        public class Stats
        {
            [Replicated] public int Hp;
            [Replicated] public double Speed;
        }

        public class Unit
        {
            [Replicated] public int Id;
            [Replicated] public Stats Stats;
        }
    }

    public static class NestedSame
    {
        public class Stats
        {
            [Replicated] public float Speed;
            [Replicated] public int Hp;
        }

        public class Unit
        {
            [Replicated] public Stats Stats;
            [Replicated] public int Id;
        }
    }

    public static class NestedSealed
    {
        public sealed class Stats
        {
            [Replicated] public int Hp;
            [Replicated] public float Speed;
        }

        public class Unit
        {
            [Replicated] public int Id;
            [Replicated] public Stats Stats;
        }
    }

    [Fact]
    public void SchemaHash_ReferencesAreUnambiguous_ForTypesWithSameShortName()
    {
        Replicator replicator = CreateReplicator();
        Assert.NotEqual(replicator.GetSchemaHash(typeof(RefFirst.Root)), replicator.GetSchemaHash(typeof(RefSecond.Root)));
    }

    [Fact]
    public void SchemaHash_CoversNestedTypes()
    {
        Replicator replicator = CreateReplicator();
        ulong unit = replicator.GetSchemaHash(typeof(Unit));

        // Та же раскладка в другом месте — тот же хэш
        Assert.Equal(unit, replicator.GetSchemaHash(typeof(NestedSame.Unit)));
        Assert.Equal(unit, CreateReplicator().GetSchemaHash(typeof(Unit)));

        // Изменение вложенного типа или его запечатанности меняет хэш
        Assert.NotEqual(unit, replicator.GetSchemaHash(typeof(NestedChanged.Unit)));
        Assert.NotEqual(unit, replicator.GetSchemaHash(typeof(NestedSealed.Unit)));
    }

    [Fact]
    public void SchemaHash_WorksForRecursiveAndPolymorphicTypes()
    {
        Replicator replicator = CreateReplicator();
        Assert.Equal(replicator.GetSchemaHash(typeof(TreeNode)), CreateReplicator().GetSchemaHash(typeof(TreeNode)));
        Assert.NotEqual(replicator.GetSchemaHash(typeof(TreeNode)), replicator.GetSchemaHash(typeof(Link)));

        // Абстрактный и интерфейсный объявленные типы описываются без создания экземпляров
        ulong armory = replicator.GetSchemaHash(typeof(Armory));
        ulong drawing = replicator.GetSchemaHash(typeof(Drawing));
        Assert.NotEqual(armory, drawing);
        replicator.GetSchemaHash(typeof(Weapon));
    }

    // ---------- allocations ----------

    [Fact]
    public void UnchangedNestedDelta_DoesNotAllocate()
    {
        Replicator replicator = CreateReplicator(CreateTypeIds());
        var server = new TreeNode
        {
            Value = 1,
            Left = new TreeNode { Value = 2 },
            Right = new TreeNode { Value = 3 }
        };
        var armoryServer = new Armory { Weapon = new Rifle() };
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        ReplicationBaseline armoryBaseline = replicator.CreateBaseline(armoryServer);
        var writer = new BitWriter(1024);
        Assert.True(replicator.TryWriteDelta(baseline, writer));
        Assert.True(replicator.TryWriteDelta(armoryBaseline, writer));
        writer.Reset();
        Assert.False(replicator.TryWriteDelta(baseline, writer));

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            replicator.TryWriteDelta(baseline, writer);
            replicator.TryWriteDelta(armoryBaseline, writer);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void ChangedNestedDeltaAndApply_DoNotAllocate()
    {
        Replicator replicator = CreateReplicator(CreateTypeIds());
        var server = new Armory { Weapon = new Rifle() };
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var writer = new BitWriter(1024);
        var client = new Armory();

        void Frame(int i)
        {
            writer.Reset();
            server.Weapon.Ammo = i;
            replicator.TryWriteDelta(baseline, writer);
            var reader = new BitReader(writer.AsSpan());
            replicator.Apply(client, ref reader);
        }

        Frame(1);
        Frame(2);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 3; i < 1000; i++)
        {
            Frame(i);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.Equal(999, client.Weapon.Ammo);
    }
}
