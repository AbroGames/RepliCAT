using RepliCAT;
using RepliCAT.Bits;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace RepliCAT.Tests;

public class ReplicatedListTests
{
    // ---------- test infrastructure ----------

    private sealed class CollectingSink : ILogEventSink
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Emit(LogEvent logEvent)
        {
            Interlocked.Increment(ref _count);
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

    private static Replicator CreateReplicator(out CollectingSink sink, ReplicationLimits limits = null)
    {
        sink = new CollectingSink();
        ILogger logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
        ITypeIdMapping typeIds = new FakeTypeIds()
            .Add(typeof(Item), 1)
            .Add(typeof(SpecialItem), 2);
        return new Replicator(typeIds, limits: limits, logger: logger);
    }

    private static Replicator CreateReplicator(ReplicationLimits limits = null)
    {
        return CreateReplicator(out _, limits);
    }

    private static byte[] Delta(Replicator replicator, ReplicationBaseline baseline)
    {
        Assert.True(replicator.TryWriteDelta(baseline, out byte[] data));
        Assert.NotNull(data);
        return data;
    }

    private static byte[] Snapshot(Replicator replicator, ReplicationBaseline baseline)
    {
        Assert.True(replicator.TryWriteSnapshot(baseline, out byte[] data));
        return data;
    }

    private static byte[] Craft(Action<BitWriter> write)
    {
        var writer = new BitWriter();
        write(writer);
        return writer.ToArray();
    }

    // ---------- test types ----------

    public class IntListHolder
    {
        [Replicated] public ReplicatedList<int> Values = new();
    }

    public class Item
    {
        [Replicated] public int A;
        [Replicated] public string Name;
    }

    public class SpecialItem : Item
    {
        [Replicated] public float Power;
    }

    public class ItemListHolder
    {
        [Replicated] public readonly ReplicatedList<Item> Items = new();
    }

    public class ReplaceableHolder
    {
        private ReplicatedList<int> _values = new();

        public int SetterCalls;

        [Replicated]
        public ReplicatedList<int> Values
        {
            get => _values;
            set
            {
                _values = value;
                SetterCalls++;
            }
        }
    }

    public class GetOnlyHolder
    {
        [Replicated] public ReplicatedList<int> Values { get; } = new();
    }

    public class GetOnlyNullHolder
    {
        [Replicated] public ReplicatedList<int> Values { get; }
    }

    public class NestedListHolder
    {
        [Replicated] public ReplicatedList<ReplicatedList<int>> Rows = new();
    }

    public class QuantizedHolder
    {
        [Replicated, Quantize(0, 10, 0.1)] public ReplicatedList<float> Values = new();
        [Replicated(Tolerance = 0.5)] public ReplicatedList<float> Tolerant = new();
    }

    public class QuantizedRowsHolder
    {
        [Replicated, Quantize(0, 10, 0.1)] public ReplicatedList<ReplicatedList<float>> Rows = new();
    }

    public class QuantizedObjectList
    {
        [Replicated, Quantize(1)] public ReplicatedList<Item> Items = new();
    }

    public class PlainListInList
    {
        [Replicated] public ReplicatedList<List<int>> Values = new();
    }

    public class TreeNode
    {
        [Replicated] public int Value;
        [Replicated] public ReplicatedList<TreeNode> Children = new();
    }

    public class Composite
    {
        [Replicated] public int Frame;
        [Replicated] public ReplicatedList<int> Values { get; set; } = new();
        [Replicated] public ReplicatedList<Item> Items { get; set; } = new();
        [Replicated] public ReplicatedList<ReplicatedList<int>> Rows { get; set; } = new();
        [Replicated] public ReplicatedList<string> Names = new();
    }

    // ---------- equality ----------

    private static void AssertItemEqual(Item expected, Item actual)
    {
        if (expected == null)
        {
            Assert.Null(actual);
            return;
        }

        Assert.NotNull(actual);
        Assert.Equal(expected.GetType(), actual.GetType());
        Assert.Equal(expected.A, actual.A);
        Assert.Equal(expected.Name, actual.Name);
        if (expected is SpecialItem special)
        {
            Assert.Equal(special.Power, ((SpecialItem)actual).Power);
        }
    }

    private static void AssertListEqual<T>(ReplicatedList<T> expected, ReplicatedList<T> actual, Action<T, T> assertItem)
    {
        if (expected == null)
        {
            Assert.Null(actual);
            return;
        }

        Assert.NotNull(actual);
        Assert.Equal(expected.Count, actual.Count);
        int index = 0;
        foreach (T item in actual)
        {
            assertItem(expected[index], item);
            assertItem(expected[index], actual[index]);
            index++;
        }

        Assert.Equal(expected.Count, index);
    }

    private static void AssertValuesEqual<T>(ReplicatedList<T> expected, ReplicatedList<T> actual)
    {
        AssertListEqual(expected, actual, static (e, a) => Assert.Equal(e, a));
    }

    private static void AssertEqual(Composite expected, Composite actual)
    {
        Assert.Equal(expected.Frame, actual.Frame);
        AssertValuesEqual(expected.Values, actual.Values);
        AssertListEqual(expected.Items, actual.Items, AssertItemEqual);
        AssertListEqual(expected.Rows, actual.Rows, AssertValuesEqual);
        AssertValuesEqual(expected.Names, actual.Names);
    }

    // ---------- wire decoding (IntListHolder) ----------

    private sealed class DecodedList
    {
        public bool Present;
        public bool Reset;
        public readonly List<int> Removals = new();
        public readonly List<(int Slot, int Value)> Upserts = new();
        public List<int> Order;
    }

    /// <summary>
    /// Разбирает полезную нагрузку <see cref="IntListHolder"/> по формату узла списка.
    /// </summary>
    private static DecodedList DecodeIntList(byte[] data)
    {
        var result = new DecodedList();
        var reader = new BitReader(data);
        Assert.Equal(1UL, reader.ReadBits(1)); // маска корня
        result.Present = reader.ReadBool();
        if (!result.Present)
        {
            return result;
        }

        result.Reset = reader.ReadBool();
        if (!result.Reset)
        {
            while (reader.ReadBool())
            {
                result.Removals.Add((int)reader.ReadVarUInt());
            }
        }

        while (reader.ReadBool())
        {
            int slot = (int)reader.ReadVarUInt();
            int value = (int)(uint)reader.ReadBits(32);
            result.Upserts.Add((slot, value));
        }

        if (reader.ReadBool())
        {
            result.Order = new List<int>();
            int count = (int)reader.ReadVarUInt();
            for (int i = 0; i < count; i++)
            {
                result.Order.Add((int)reader.ReadVarUInt());
            }
        }

        Assert.True(reader.RemainingBits < 8);
        return result;
    }

    // ---------- List<T> parity ----------

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Parity_BehavesLikeList(int seed)
    {
        var random = new Random(seed);
        var list = new ReplicatedList<int>();
        var model = new List<int>();

        for (int step = 0; step < 3000; step++)
        {
            int value = random.Next(20);
            switch (random.Next(14))
            {
                case 0:
                case 1:
                    list.Add(value);
                    model.Add(value);
                    break;
                case 2:
                {
                    int index = random.Next(model.Count + 1);
                    list.Insert(index, value);
                    model.Insert(index, value);
                    break;
                }
                case 3:
                case 4:
                    if (model.Count > 0)
                    {
                        int index = random.Next(model.Count);
                        list.RemoveAt(index);
                        model.RemoveAt(index);
                    }

                    break;
                case 5:
                    Assert.Equal(model.Remove(value), list.Remove(value));
                    break;
                case 6:
                    if (model.Count > 0)
                    {
                        int index = random.Next(model.Count);
                        list[index] = value;
                        model[index] = value;
                    }

                    break;
                case 7:
                    if (random.Next(15) == 0)
                    {
                        list.Clear();
                        model.Clear();
                    }

                    break;
                case 8:
                    list.Sort();
                    model.Sort();
                    break;
                case 9:
                    list.Sort(static (a, b) => b.CompareTo(a));
                    model.Sort(static (a, b) => b.CompareTo(a));
                    break;
                case 10:
                {
                    int[] range = [value, value + 1, value + 2];
                    list.AddRange(range);
                    model.AddRange(range);
                    break;
                }
                case 11:
                    Assert.Equal(model.IndexOf(value), list.IndexOf(value));
                    Assert.Equal(model.Contains(value), list.Contains(value));
                    break;
                case 12:
                {
                    var expected = new int[model.Count + 2];
                    var actual = new int[model.Count + 2];
                    model.CopyTo(expected, 1);
                    list.CopyTo(actual, 1);
                    Assert.Equal(expected, actual);
                    break;
                }
                case 13:
                    list.Sort(Comparer<int>.Create(static (a, b) => (a % 5).CompareTo(b % 5) * 100 + a.CompareTo(b)));
                    model.Sort(Comparer<int>.Create(static (a, b) => (a % 5).CompareTo(b % 5) * 100 + a.CompareTo(b)));
                    break;
            }

            Assert.Equal(model.Count, list.Count);
            Assert.Equal(model, list);
            for (int i = 0; i < model.Count; i++)
            {
                Assert.Equal(model[i], list[i]);
            }
        }
    }

    [Fact]
    public void ListApi_EdgeCases()
    {
        var list = new ReplicatedList<int>([1, 2, 3]);
        Assert.Equal([1, 2, 3], list);

        Assert.Throws<ArgumentOutOfRangeException>(() => list[3]);
        Assert.Throws<ArgumentOutOfRangeException>(() => list[-1] = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => list.Insert(4, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => list.RemoveAt(3));
        Assert.Throws<ArgumentException>(() => list.CopyTo(new int[3], 1));
        Assert.Throws<ArgumentNullException>(() => list.AddRange(null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReplicatedList<int>(-1));

        list.AddRange(list);
        Assert.Equal([1, 2, 3, 1, 2, 3], list);

        list.Insert(6, 7);
        Assert.Equal(7, list[6]);
        Assert.False(((ICollection<int>)list).IsReadOnly);

        var boxed = new List<int>();
        foreach (int item in (IEnumerable<int>)list)
        {
            boxed.Add(item);
        }

        Assert.Equal([1, 2, 3, 1, 2, 3, 7], boxed);
    }

    [Fact]
    public void Enumerator_ThrowsWhenModified()
    {
        var list = new ReplicatedList<int> { 1, 2, 3 };

        Assert.Throws<InvalidOperationException>(() =>
        {
            foreach (int item in list)
            {
                list.Add(item);
            }
        });

        Assert.Throws<InvalidOperationException>(() =>
        {
            foreach (int _ in list)
            {
                list[0] = 5;
            }
        });

        Assert.Throws<InvalidOperationException>(() =>
        {
            foreach (int _ in list)
            {
                list.Sort();
            }
        });
    }

    // ---------- value items ----------

    [Fact]
    public void ValueItems_AllOperationsReplicate()
    {
        Replicator replicator = CreateReplicator();
        var server = new IntListHolder();
        server.Values.AddRange([10, 20, 30]);
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var client = new IntListHolder();
        ReplicatedList<int> clientList = client.Values;

        void Frame(Action<ReplicatedList<int>> mutate)
        {
            mutate(server.Values);
            replicator.Apply(client, Delta(replicator, baseline));
            Assert.Same(clientList, client.Values);
            AssertValuesEqual(server.Values, client.Values);
        }

        Frame(_ => { });
        Frame(l => l.Add(40));
        Frame(l => l.RemoveAt(1));
        Frame(l => l[0] = 11);
        Frame(l => l.Insert(1, 15));
        Frame(l => l.Insert(0, 5));
        Frame(l => l.Sort(static (a, b) => b.CompareTo(a)));
        Frame(l => l.Remove(15));
        Frame(l =>
        {
            l.RemoveAt(0);
            l.Add(100);
            l.Insert(1, 50);
            l[2] = 7;
        });
        Frame(l => l.Clear());
        Frame(l => l.AddRange([1, 2, 3, 4]));
        Frame(l =>
        {
            l.Clear();
            l.Add(9);
        });
    }

    [Fact]
    public void NoChanges_ReturnsFalse()
    {
        Replicator replicator = CreateReplicator();
        var server = new IntListHolder();
        server.Values.AddRange([1, 2, 3]);
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        Delta(replicator, baseline);

        var writer = new BitWriter();
        Assert.False(replicator.TryWriteDelta(baseline, writer));
        Assert.Equal(0, writer.BitPosition);

        // Изменение, которое ничего не меняет по существу (то же значение), тоже не дает данных.
        server.Values[1] = 2;
        Assert.False(replicator.TryWriteDelta(baseline, writer));

        // Добавить и тут же удалить.
        server.Values.Add(4);
        server.Values.RemoveAt(3);
        Assert.False(replicator.TryWriteDelta(baseline, writer));
        Assert.Equal(0, writer.BitPosition);

        // Объектные элементы без изменений.
        var itemServer = new ItemListHolder();
        itemServer.Items.Add(new Item { A = 1 });
        ReplicationBaseline itemBaseline = replicator.CreateBaseline(itemServer);
        Delta(replicator, itemBaseline);
        Assert.False(replicator.TryWriteDelta(itemBaseline, writer));
        Assert.Equal(0, writer.BitPosition);
    }

    [Fact]
    public void Wire_OnlyInsertAndSortSendOrder()
    {
        Replicator replicator = CreateReplicator();
        var server = new IntListHolder();
        server.Values.AddRange([10, 20, 30]);
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var client = new IntListHolder();

        DecodedList Frame(Action<ReplicatedList<int>> mutate)
        {
            mutate(server.Values);
            byte[] data = Delta(replicator, baseline);
            replicator.Apply(client, data);
            AssertValuesEqual(server.Values, client.Values);
            return DecodeIntList(data);
        }

        DecodedList first = Frame(_ => { });
        Assert.True(first.Reset);
        Assert.Equal([(0, 10), (1, 20), (2, 30)], first.Upserts);
        Assert.Null(first.Order);

        DecodedList add = Frame(l => l.Add(40));
        Assert.False(add.Reset);
        Assert.Empty(add.Removals);
        Assert.Equal([(3, 40)], add.Upserts);
        Assert.Null(add.Order);

        DecodedList removeAt = Frame(l => l.RemoveAt(1));
        Assert.Equal([1], removeAt.Removals);
        Assert.Empty(removeAt.Upserts);
        Assert.Null(removeAt.Order);

        DecodedList set = Frame(l => l[0] = 11);
        Assert.Empty(set.Removals);
        Assert.Equal([(0, 11)], set.Upserts);
        Assert.Null(set.Order);

        // Удаление и добавление за один кадр: освобожденный слот переиспользуется,
        // это удаление + новый элемент, порядок не нужен.
        DecodedList removeAdd = Frame(l =>
        {
            l.RemoveAt(0);
            l.Add(50);
        });
        Assert.Equal([0], removeAdd.Removals);
        Assert.Equal([(0, 50)], removeAdd.Upserts);
        Assert.Null(removeAdd.Order);

        DecodedList insert = Frame(l => l.Insert(0, 5));
        Assert.Single(insert.Upserts);
        Assert.NotNull(insert.Order);
        Assert.Equal(server.Values.Count, insert.Order.Count);

        DecodedList sort = Frame(l => l.Sort(static (a, b) => b.CompareTo(a)));
        Assert.Empty(sort.Removals);
        Assert.Empty(sort.Upserts);
        Assert.NotNull(sort.Order);

        DecodedList insertAtEnd = Frame(l => l.Insert(l.Count, 60));
        Assert.Null(insertAtEnd.Order);
    }

    [Fact]
    public void SlotReuse_KeepsSlotIdsSmall()
    {
        Replicator replicator = CreateReplicator();
        var random = new Random(7);
        var server = new IntListHolder();
        for (int i = 0; i < 10; i++)
        {
            server.Values.Add(i);
        }

        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var client = new IntListHolder();
        replicator.Apply(client, Delta(replicator, baseline));

        for (int frame = 0; frame < 1000; frame++)
        {
            int removals = random.Next(1, 4);
            for (int i = 0; i < removals; i++)
            {
                server.Values.RemoveAt(random.Next(server.Values.Count));
            }

            for (int i = 0; i < removals; i++)
            {
                server.Values.Add(frame * 10 + i);
            }

            byte[] data = Delta(replicator, baseline);
            replicator.Apply(client, data);
            AssertValuesEqual(server.Values, client.Values);

            DecodedList decoded = DecodeIntList(data);
            Assert.All(decoded.Removals, static slot => Assert.InRange(slot, 0, 9));
            Assert.All(decoded.Upserts, static upsert => Assert.InRange(upsert.Slot, 0, 9));
            Assert.Null(decoded.Order);
        }
    }

    [Fact]
    public void GetOnlyListProperty_ReusesInstance()
    {
        Replicator replicator = CreateReplicator();
        var server = new GetOnlyHolder();
        server.Values.AddRange([1, 2]);
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var client = new GetOnlyHolder();
        ReplicatedList<int> clientList = client.Values;

        replicator.Apply(client, Delta(replicator, baseline));
        Assert.Same(clientList, client.Values);
        AssertValuesEqual(server.Values, client.Values);

        // Клиенту без экземпляра нужен новый список, а сеттера нет.
        var nullClient = new GetOnlyNullHolder();
        var ex = Assert.Throws<ReplicationException>(() => replicator.Apply(nullClient, Snapshot(replicator, baseline)));
        Assert.IsNotType<ReplicationFormatException>(ex);
        Assert.Contains(nameof(GetOnlyNullHolder.Values), ex.Message);
    }

    [Fact]
    public void ReplacingListInstance_ResetsAndClientKeepsInstance()
    {
        Replicator replicator = CreateReplicator();
        var server = new ReplaceableHolder();
        server.Values.AddRange([1, 2, 3]);
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var client = new ReplaceableHolder();
        ReplicatedList<int> clientList = client.Values;
        client.SetterCalls = 0;

        replicator.Apply(client, Delta(replicator, baseline));

        server.Values = new ReplicatedList<int> { 7, 8 };
        byte[] data = Delta(replicator, baseline);
        Assert.True(DecodeIntList(data).Reset);
        replicator.Apply(client, data);

        Assert.Same(clientList, client.Values);
        Assert.Equal(0, client.SetterCalls);
        AssertValuesEqual(server.Values, client.Values);

        // Дальнейшие изменения нового экземпляра — обычные дельты.
        server.Values.Add(9);
        data = Delta(replicator, baseline);
        Assert.False(DecodeIntList(data).Reset);
        replicator.Apply(client, data);
        AssertValuesEqual(server.Values, client.Values);
    }

    [Fact]
    public void NullList_TransitionsBothWays()
    {
        Replicator replicator = CreateReplicator();
        var server = new ReplaceableHolder();
        server.Values.Add(1);
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var client = new ReplaceableHolder();
        replicator.Apply(client, Delta(replicator, baseline));

        server.Values = null;
        replicator.Apply(client, Delta(replicator, baseline));
        Assert.Null(client.Values);
        Assert.False(replicator.TryWriteDelta(baseline, out _));

        var late = new ReplaceableHolder();
        replicator.Apply(late, Snapshot(replicator, baseline));
        Assert.Null(late.Values);

        server.Values = new ReplicatedList<int> { 4, 5 };
        replicator.Apply(client, Delta(replicator, baseline));
        replicator.Apply(late, Snapshot(replicator, baseline));
        AssertValuesEqual(server.Values, client.Values);
        AssertValuesEqual(server.Values, late.Values);
    }

    [Fact]
    public void StringItems_IncludingNull()
    {
        Replicator replicator = CreateReplicator();
        var server = new Composite();
        server.Names.AddRange(["a", null, "", "b"]);
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var client = new Composite();
        replicator.Apply(client, Delta(replicator, baseline));
        AssertValuesEqual(server.Names, client.Names);

        server.Names[0] = null;
        server.Names[1] = "x";
        replicator.Apply(client, Delta(replicator, baseline));
        AssertValuesEqual(server.Names, client.Names);
    }

    // ---------- object items ----------

    [Fact]
    public void ObjectItems_ElementDeltasApplyIntoSameInstances()
    {
        Replicator replicator = CreateReplicator();
        var server = new ItemListHolder();
        server.Items.Add(new Item { A = 1, Name = "one" });
        server.Items.Add(new SpecialItem { A = 2, Name = "two", Power = 2.5f });
        server.Items.Add(null);
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var client = new ItemListHolder();

        byte[] full = Delta(replicator, baseline);
        replicator.Apply(client, full);
        AssertListEqual(server.Items, client.Items, AssertItemEqual);
        Item clientFirst = client.Items[0];
        Item clientSecond = client.Items[1];
        Assert.IsType<SpecialItem>(clientSecond);

        // Изменение члена элемента — дельта элемента в тот же экземпляр.
        server.Items[1].A = 20;
        byte[] elementDelta = Delta(replicator, baseline);
        Assert.True(elementDelta.Length < full.Length);
        replicator.Apply(client, elementDelta);
        AssertListEqual(server.Items, client.Items, AssertItemEqual);
        Assert.Same(clientFirst, client.Items[0]);
        Assert.Same(clientSecond, client.Items[1]);

        // Новый элемент передается целиком.
        server.Items.Add(new Item { A = 4, Name = "four" });
        replicator.Apply(client, Delta(replicator, baseline));
        AssertListEqual(server.Items, client.Items, AssertItemEqual);
        Assert.Same(clientFirst, client.Items[0]);
        Assert.Same(clientSecond, client.Items[1]);

        // Удаленный элемент удаляется.
        server.Items.RemoveAt(0);
        replicator.Apply(client, Delta(replicator, baseline));
        AssertListEqual(server.Items, client.Items, AssertItemEqual);
        Assert.Same(clientSecond, client.Items[0]);

        // null ↔ объект внутри списка.
        server.Items[1] = new Item { A = 5 };
        server.Items[0] = null;
        replicator.Apply(client, Delta(replicator, baseline));
        AssertListEqual(server.Items, client.Items, AssertItemEqual);

        // Замена элемента новым экземпляром того же типа: клиент переиспользует свой экземпляр.
        Item clientThird = client.Items[2];
        server.Items[2] = new Item { A = 6, Name = "six" };
        replicator.Apply(client, Delta(replicator, baseline));
        AssertListEqual(server.Items, client.Items, AssertItemEqual);
        Assert.Same(clientThird, client.Items[2]);

        // Сортировка — только порядок, экземпляры сохраняются.
        server.Items.Add(new Item { A = -1 });
        replicator.Apply(client, Delta(replicator, baseline));
        Item clientLast = client.Items[3];
        server.Items.Sort(static (a, b) => (a?.A ?? int.MinValue).CompareTo(b?.A ?? int.MinValue));
        replicator.Apply(client, Delta(replicator, baseline));
        AssertListEqual(server.Items, client.Items, AssertItemEqual);
        Assert.Same(clientLast, client.Items[1]);
        Assert.Same(clientThird, client.Items[3]);
    }

    [Fact]
    public void ListOfLists_Replicates()
    {
        Replicator replicator = CreateReplicator();
        var server = new NestedListHolder();
        server.Rows.Add(new ReplicatedList<int> { 1, 2 });
        server.Rows.Add(new ReplicatedList<int>());
        server.Rows.Add(null);
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var client = new NestedListHolder();

        void Frame(Action mutate)
        {
            mutate();
            replicator.Apply(client, Delta(replicator, baseline));
            AssertListEqual(server.Rows, client.Rows, AssertValuesEqual);
        }

        Frame(() => { });
        ReplicatedList<int> clientRow = client.Rows[0];

        Frame(() => server.Rows[0].Add(3));
        Assert.Same(clientRow, client.Rows[0]);

        Frame(() => server.Rows[1].AddRange([7, 8, 9]));
        Frame(() => server.Rows[2] = new ReplicatedList<int> { 5 });
        Frame(() => server.Rows[0] = new ReplicatedList<int> { 42 });
        Assert.Same(clientRow, client.Rows[0]);
        Frame(() => server.Rows.Insert(0, new ReplicatedList<int> { 0 }));
        Frame(() =>
        {
            server.Rows.RemoveAt(2);
            server.Rows[0].Sort(static (a, b) => b.CompareTo(a));
        });

        Assert.False(replicator.TryWriteDelta(baseline, out _));
    }

    [Fact]
    public void RecursiveTypeWithListOfChildren()
    {
        Replicator replicator = CreateReplicator();
        var server = new TreeNode { Value = 1 };
        server.Children.Add(new TreeNode { Value = 2 });
        server.Children[0].Children.Add(new TreeNode { Value = 3 });
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var client = new TreeNode();
        replicator.Apply(client, Delta(replicator, baseline));

        server.Children[0].Children[0].Value = 30;
        server.Children.Add(new TreeNode { Value = 4 });
        replicator.Apply(client, Delta(replicator, baseline));

        Assert.Equal(1, client.Value);
        Assert.Equal(2, client.Children.Count);
        Assert.Equal(30, client.Children[0].Children[0].Value);
        Assert.Equal(4, client.Children[1].Value);
    }

    [Fact]
    public void Depth_CollectionsCountAsLevels()
    {
        var server = new ItemListHolder();
        server.Items.Add(new Item());

        // Список (1 уровень) + элемент-объект (2 уровень).
        Replicator ok = CreateReplicator(new ReplicationLimits { MaxDepth = 2 });
        var client = new ItemListHolder();
        ok.Apply(client, Delta(ok, ok.CreateBaseline(server)));
        Assert.Single(client.Items);

        Replicator tooShallow = CreateReplicator(new ReplicationLimits { MaxDepth = 1 });
        Assert.Throws<ReplicationException>(() => tooShallow.TryWriteDelta(tooShallow.CreateBaseline(server), out _));
    }

    // ---------- quantization and tolerance ----------

    [Fact]
    public void QuantizeAndTolerance_ApplyToValueItems()
    {
        Replicator replicator = CreateReplicator(out CollectingSink sink);
        var server = new QuantizedHolder();
        server.Values.AddRange([1.23f, 4.56f]);
        server.Tolerant.AddRange([1f, 2f]);
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var client = new QuantizedHolder();
        replicator.Apply(client, Delta(replicator, baseline));

        Assert.Equal(1.2f, client.Values[0], 0.051f);
        Assert.Equal(4.6f, client.Values[1], 0.051f);
        Assert.Equal([1f, 2f], client.Tolerant);

        // Изменение меньше допуска не отправляется, даже при структурном изменении списка.
        server.Tolerant[0] = 1.3f;
        server.Tolerant.Add(3f);
        replicator.Apply(client, Delta(replicator, baseline));
        Assert.Equal([1f, 2f, 3f], client.Tolerant);

        server.Tolerant[0] = 1.6f;
        replicator.Apply(client, Delta(replicator, baseline));
        Assert.Equal(1.6f, client.Tolerant[0]);

        // Выход за диапазон у многих элементов — одно предупреждение на член.
        Assert.Equal(0, sink.Count);
        for (int i = 0; i < 10; i++)
        {
            server.Values.Add(100 + i);
        }

        replicator.Apply(client, Delta(replicator, baseline));
        var late = new QuantizedHolder();
        replicator.Apply(late, Snapshot(replicator, baseline));
        Assert.Equal(1, sink.Count);
        Assert.Equal(10f, client.Values[5]);
        Assert.Equal(10f, late.Values[11]);
    }

    [Fact]
    public void Quantize_PropagatesThroughNestedLists()
    {
        Replicator replicator = CreateReplicator();
        var server = new QuantizedRowsHolder();
        server.Rows.Add(new ReplicatedList<float> { 3.33f });
        var writer = new BitWriter();
        Assert.True(replicator.TryWriteDelta(replicator.CreateBaseline(server), writer));

        // маска (1) + внешний список: present, reset (2), [1][slot 0] (9), внутренний список: present, reset (2),
        // [1][slot 0] (9), 7 бит квантованного значения (100 шагов), конец вставок и hasOrder (2); затем то же (2).
        Assert.Equal(34, writer.BitPosition);
        var client = new QuantizedRowsHolder();
        replicator.Apply(client, writer.AsSpan());
        Assert.Equal(3.3f, client.Rows[0][0], 0.051f);
        Assert.NotEqual(
            replicator.GetSchemaHash(typeof(QuantizedRowsHolder)),
            replicator.GetSchemaHash(typeof(NestedListHolder)));
    }

    // ---------- model errors and schema ----------

    [Fact]
    public void ModelErrors_ThrowWithMemberPath()
    {
        Replicator replicator = CreateReplicator();

        var quantizedObjects = Assert.Throws<ReplicationException>(() => replicator.CreateBaseline(new QuantizedObjectList()));
        Assert.Contains($"{typeof(QuantizedObjectList).FullName}.{nameof(QuantizedObjectList.Items)}", quantizedObjects.Message);

        var plainInList = Assert.Throws<ReplicationException>(() => replicator.CreateBaseline(new PlainListInList()));
        Assert.Contains(nameof(PlainListInList.Values), plainInList.Message);
        Assert.Contains("ReplicatedList", plainInList.Message);

        Assert.Throws<ReplicationException>(() => replicator.CreateBaseline(new ReplicatedList<int>()));
    }

    [Fact]
    public void SchemaHash_DescribesItems()
    {
        Replicator first = CreateReplicator();
        Replicator second = CreateReplicator();
        Assert.Equal(first.GetSchemaHash(typeof(Composite)), second.GetSchemaHash(typeof(Composite)));
        Assert.NotEqual(first.GetSchemaHash(typeof(IntListHolder)), first.GetSchemaHash(typeof(SchemaVariants.FloatList)));
        Assert.NotEqual(
            first.GetSchemaHash(typeof(SchemaVariants.FloatList)),
            first.GetSchemaHash(typeof(SchemaVariants.QuantizedFloatList)));
        Assert.NotEqual(first.GetSchemaHash(typeof(IntListHolder)), first.GetSchemaHash(typeof(SchemaVariants.IntValue)));
    }

    public static class SchemaVariants
    {
        public class FloatList
        {
            [Replicated] public ReplicatedList<float> Values = new();
        }

        public class QuantizedFloatList
        {
            [Replicated, Quantize(0.1)] public ReplicatedList<float> Values = new();
        }

        public class IntValue
        {
            [Replicated] public int Values;
        }
    }

    // ---------- snapshots ----------

    [Fact]
    public void MidFrameSnapshotPlusDelta_MatchesServer()
    {
        Replicator replicator = CreateReplicator();
        var server = new Composite();
        server.Values.AddRange([1, 2, 3]);
        server.Items.Add(new Item { A = 1 });
        server.Items.Add(new SpecialItem { A = 2, Power = 1 });
        server.Rows.Add(new ReplicatedList<int> { 5 });
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var client = new Composite();
        replicator.Apply(client, Delta(replicator, baseline));

        // Кадр: часть изменений до снимка, часть после; одно значение меняется и возвращается обратно.
        server.Values[0] = 100;
        server.Items[0].A = 50;
        server.Values.Add(4);
        byte[] snapshot = Snapshot(replicator, baseline);
        server.Values[0] = 1;
        server.Items[0].A = 1;
        server.Items.RemoveAt(1);
        server.Rows[0].Add(6);
        server.Values.Insert(0, 0);
        byte[] delta = Delta(replicator, baseline);

        var late = new Composite();
        late.Values.AddRange([9, 9, 9, 9, 9]); // мусор, который сброс должен убрать
        late.Items.Add(new Item { A = 77 });
        replicator.Apply(late, snapshot);
        replicator.Apply(late, delta);
        replicator.Apply(client, delta);

        AssertEqual(server, client);
        AssertEqual(server, late);
    }

    // ---------- randomized ----------

    private static Item RandomItem(Random random)
    {
        return random.Next(6) switch
        {
            0 => null,
            1 => new SpecialItem { A = random.Next(100), Name = "s" + random.Next(5), Power = random.Next(10) / 2f },
            _ => new Item { A = random.Next(100), Name = random.Next(4) == 0 ? null : "n" + random.Next(5) }
        };
    }

    private static void MutateValues(ReplicatedList<int> list, Random random)
    {
        int count = list.Count;
        switch (random.Next(10))
        {
            case 0:
            case 1:
                if (count < 40)
                {
                    list.Add(random.Next(1000));
                }

                break;
            case 2:
                if (count < 40)
                {
                    list.Insert(random.Next(count + 1), random.Next(1000));
                }

                break;
            case 3:
            case 4:
                if (count > 0)
                {
                    list.RemoveAt(random.Next(count));
                }

                break;
            case 5:
            case 6:
                if (count > 0)
                {
                    list[random.Next(count)] = random.Next(1000);
                }

                break;
            case 7:
                list.Sort();
                break;
            case 8:
                if (random.Next(10) == 0)
                {
                    list.Clear();
                }

                break;
            case 9:
                if (count > 1)
                {
                    // Перестановка двух элементов через удаление и вставку.
                    int from = random.Next(count);
                    int value = list[from];
                    list.RemoveAt(from);
                    list.Insert(random.Next(count), value);
                }

                break;
        }
    }

    private static void MutateItems(ReplicatedList<Item> list, Random random)
    {
        int count = list.Count;
        switch (random.Next(10))
        {
            case 0:
                if (count < 30)
                {
                    list.Add(RandomItem(random));
                }

                break;
            case 1:
                if (count < 30)
                {
                    list.Insert(random.Next(count + 1), RandomItem(random));
                }

                break;
            case 2:
                if (count > 0)
                {
                    list.RemoveAt(random.Next(count));
                }

                break;
            case 3:
                if (count > 0)
                {
                    list[random.Next(count)] = RandomItem(random);
                }

                break;
            case 4:
            case 5:
            case 6:
                if (count > 0 && list[random.Next(count)] is { } item)
                {
                    item.A = random.Next(100);
                    if (random.Next(3) == 0)
                    {
                        item.Name = random.Next(3) == 0 ? null : "m" + random.Next(5);
                    }

                    if (item is SpecialItem special && random.Next(2) == 0)
                    {
                        special.Power = random.Next(10);
                    }
                }

                break;
            case 7:
                list.Sort(static (a, b) => (a?.A ?? -1).CompareTo(b?.A ?? -1));
                break;
            case 8:
                if (random.Next(8) == 0)
                {
                    list.Clear();
                }

                break;
            case 9:
                if (count > 1)
                {
                    // Один и тот же экземпляр в двух позициях списка.
                    list[random.Next(count)] = list[random.Next(count)];
                }

                break;
        }
    }

    private static void Mutate(Composite server, Random random)
    {
        switch (random.Next(12))
        {
            case 0:
            case 1:
            case 2:
                if (server.Values != null)
                {
                    MutateValues(server.Values, random);
                }

                break;
            case 3:
            case 4:
            case 5:
            case 6:
                MutateItems(server.Items, random);
                break;
            case 7:
            case 8:
            {
                ReplicatedList<ReplicatedList<int>> rows = server.Rows;
                int count = rows.Count;
                int action = random.Next(6);
                if (action == 0 && count < 8)
                {
                    rows.Add(random.Next(5) == 0 ? null : new ReplicatedList<int>());
                }
                else if (action == 1 && count > 0)
                {
                    rows.RemoveAt(random.Next(count));
                }
                else if (action == 2 && count > 0)
                {
                    rows[random.Next(count)] = new ReplicatedList<int> { random.Next(10) };
                }
                else if (count > 0 && rows[random.Next(count)] is { } row)
                {
                    MutateValues(row, random);
                }

                break;
            }
            case 9:
                if (server.Names.Count < 10 && random.Next(2) == 0)
                {
                    server.Names.Add(random.Next(4) == 0 ? null : "name" + random.Next(10));
                }
                else if (server.Names.Count > 0)
                {
                    server.Names.RemoveAt(random.Next(server.Names.Count));
                }

                break;
            case 10:
                // Редкая замена экземпляров списков и null.
                switch (random.Next(12))
                {
                    case 0:
                        server.Values = server.Values == null ? new ReplicatedList<int> { 1, 2 } : null;
                        break;
                    case 1:
                        server.Values = new ReplicatedList<int>(server.Values ?? new ReplicatedList<int>());
                        break;
                    case 2:
                    {
                        var copy = new ReplicatedList<Item>(server.Items);
                        if (copy.Count > 0)
                        {
                            copy.RemoveAt(0);
                        }

                        server.Items = copy;
                        break;
                    }
                }

                break;
            case 11:
                server.Frame++;
                break;
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Randomized_ClientsMatchServerEveryFrame(int seed)
    {
        var random = new Random(seed);
        Replicator replicator = CreateReplicator();
        var server = new Composite();
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var client = new Composite();
        ReplicatedList<Item> clientItems = client.Items;
        Composite late = null;
        int lateClients = 0;

        for (int frame = 0; frame < 1000; frame++)
        {
            int operations = random.Next(0, 8);

            // Момент снимка для нового клиента: до любой из операций, перед дельтой или после нее.
            int snapshotAt = random.Next(10) == 0 ? random.Next(operations + 2) : -1;
            byte[] snapshot = null;
            bool snapshotBeforeDelta = false;

            for (int op = 0; op < operations; op++)
            {
                if (op == snapshotAt && replicator.TryWriteSnapshot(baseline, out snapshot))
                {
                    snapshotBeforeDelta = true;
                }

                Mutate(server, random);
            }

            if (snapshotAt == operations && replicator.TryWriteSnapshot(baseline, out snapshot))
            {
                snapshotBeforeDelta = true;
            }

            bool hasDelta = replicator.TryWriteDelta(baseline, out byte[] delta);

            if (snapshotAt == operations + 1)
            {
                Assert.True(replicator.TryWriteSnapshot(baseline, out snapshot));
            }

            if (hasDelta)
            {
                replicator.Apply(client, delta);
            }

            if (late != null && hasDelta)
            {
                replicator.Apply(late, delta);
            }

            if (snapshot != null)
            {
                late = new Composite();
                if (random.Next(2) == 0)
                {
                    late.Values.AddRange([-1, -2, -3]);
                    late.Items.Add(new Item { A = -1 });
                    late.Rows.Add(new ReplicatedList<int> { -1 });
                }

                replicator.Apply(late, snapshot);
                if (snapshotBeforeDelta && hasDelta)
                {
                    replicator.Apply(late, delta);
                }

                lateClients++;
            }

            AssertEqual(server, client);
            Assert.Same(clientItems, client.Items);
            if (late != null)
            {
                AssertEqual(server, late);
            }
        }

        Assert.True(lateClients > 20);
    }

    // ---------- malformed data ----------

    /// <summary>
    /// Клиент со списком из слотов 0, 1, 2 (значения 10, 20, 30).
    /// </summary>
    private static IntListHolder CreateClientWithThreeItems(Replicator replicator)
    {
        var server = new IntListHolder();
        server.Values.AddRange([10, 20, 30]);
        var client = new IntListHolder();
        replicator.Apply(client, Delta(replicator, replicator.CreateBaseline(server)));
        return client;
    }

    private static byte[] OrderDelta(params ulong[] slots)
    {
        return OrderDeltaWithCount((ulong)slots.Length, slots);
    }

    private static byte[] OrderDeltaWithCount(ulong count, params ulong[] slots)
    {
        return Craft(w =>
        {
            w.WriteBool(true); // маска
            w.WriteBool(true); // present
            w.WriteBool(false); // reset
            w.WriteBool(false); // конец удалений
            w.WriteBool(false); // конец вставок
            w.WriteBool(true); // hasOrder
            w.WriteVarUInt(count);
            foreach (ulong slot in slots)
            {
                w.WriteVarUInt(slot);
            }
        });
    }

    [Fact]
    public void Malformed_ValidOrderIsAccepted()
    {
        Replicator replicator = CreateReplicator();
        IntListHolder client = CreateClientWithThreeItems(replicator);
        replicator.Apply(client, OrderDelta(2, 0, 1));
        Assert.Equal([30, 10, 20], client.Values);
    }

    [Theory]
    [InlineData(new ulong[] { 0, 0, 1 })] // повтор
    [InlineData(new ulong[] { 0, 1, 5 })] // неизвестный слот
    [InlineData(new ulong[] { 0, 1 })] // не хватает слотов
    [InlineData(new ulong[] { 0, 1, 2, 3 })] // лишний слот
    [InlineData(new ulong[] { 0, 1, 70000 })] // слот выше лимита
    public void Malformed_BadOrderPermutationThrows(ulong[] slots)
    {
        Replicator replicator = CreateReplicator();
        IntListHolder client = CreateClientWithThreeItems(replicator);
        Assert.Throws<ReplicationFormatException>(() => replicator.Apply(client, OrderDelta(slots)));

        // Список остается согласованным.
        Assert.Equal([10, 20, 30], client.Values);
    }

    [Fact]
    public void Malformed_OrderCountAboveLimitThrows()
    {
        Replicator replicator = CreateReplicator(new ReplicationLimits { MaxCollectionCount = 8 });
        IntListHolder client = CreateClientWithThreeItems(replicator);
        Assert.Throws<ReplicationFormatException>(() => replicator.Apply(client, OrderDeltaWithCount(1000000)));
        Assert.Throws<ReplicationFormatException>(() => replicator.Apply(client, OrderDeltaWithCount(ulong.MaxValue)));
    }

    [Fact]
    public void Malformed_DuplicateSlotInResetThrows()
    {
        Replicator replicator = CreateReplicator();
        IntListHolder client = CreateClientWithThreeItems(replicator);
        byte[] data = Craft(w =>
        {
            w.WriteBool(true); // маска
            w.WriteBool(true); // present
            w.WriteBool(true); // reset
            w.WriteBool(true);
            w.WriteVarUInt(4);
            w.WriteBits(1, 32);
            w.WriteBool(true);
            w.WriteVarUInt(4);
            w.WriteBits(2, 32);
            w.WriteBool(false);
            w.WriteBool(false); // hasOrder
        });

        Assert.Throws<ReplicationFormatException>(() => replicator.Apply(client, data));

        // Прежний слот тоже нельзя записать дважды.
        byte[] existing = Craft(w =>
        {
            w.WriteBool(true);
            w.WriteBool(true);
            w.WriteBool(true);
            w.WriteBool(true);
            w.WriteVarUInt(1);
            w.WriteBits(1, 32);
            w.WriteBool(true);
            w.WriteVarUInt(1);
            w.WriteBits(2, 32);
            w.WriteBool(false);
            w.WriteBool(false);
        });

        IntListHolder second = CreateClientWithThreeItems(replicator);
        Assert.Throws<ReplicationFormatException>(() => replicator.Apply(second, existing));
    }

    [Fact]
    public void Malformed_ValidResetIsAccepted()
    {
        Replicator replicator = CreateReplicator();
        IntListHolder client = CreateClientWithThreeItems(replicator);
        byte[] data = Craft(w =>
        {
            w.WriteBool(true);
            w.WriteBool(true);
            w.WriteBool(true);
            w.WriteBool(true);
            w.WriteVarUInt(2);
            w.WriteBits(7, 32);
            w.WriteBool(true);
            w.WriteVarUInt(9);
            w.WriteBits(8, 32);
            w.WriteBool(false);
            w.WriteBool(false);
        });

        replicator.Apply(client, data);
        Assert.Equal([7, 8], client.Values);
    }

    [Theory]
    [InlineData(false, 65536UL)]
    [InlineData(false, ulong.MaxValue)]
    [InlineData(true, 65536UL)]
    public void Malformed_SlotIdAboveLimitThrows(bool removal, ulong slot)
    {
        Replicator replicator = CreateReplicator();
        byte[] data = Craft(w =>
        {
            w.WriteBool(true);
            w.WriteBool(true);
            w.WriteBool(false);
            if (removal)
            {
                w.WriteBool(true);
                w.WriteVarUInt(slot);
            }

            w.WriteBool(false);
            if (!removal)
            {
                w.WriteBool(true);
                w.WriteVarUInt(slot);
                w.WriteBits(1, 32);
            }

            w.WriteBool(false);
            w.WriteBool(false);
        });

        Assert.Throws<ReplicationFormatException>(() => replicator.Apply(new IntListHolder(), data));
    }

    [Fact]
    public void Malformed_SlotIdAboveCustomLimitThrows()
    {
        Replicator replicator = CreateReplicator(new ReplicationLimits { MaxCollectionCount = 4 });
        byte[] ok = Craft(w =>
        {
            w.WriteBool(true);
            w.WriteBool(true);
            w.WriteBool(true);
            w.WriteBool(true);
            w.WriteVarUInt(3);
            w.WriteBits(1, 32);
            w.WriteBool(false);
            w.WriteBool(false);
        });
        var client = new IntListHolder();
        replicator.Apply(client, ok);
        Assert.Equal([1], client.Values);

        byte[] bad = Craft(w =>
        {
            w.WriteBool(true);
            w.WriteBool(true);
            w.WriteBool(true);
            w.WriteBool(true);
            w.WriteVarUInt(4);
            w.WriteBits(1, 32);
            w.WriteBool(false);
            w.WriteBool(false);
        });
        Assert.Throws<ReplicationFormatException>(() => replicator.Apply(client, bad));
        Assert.Empty(client.Values);
    }

    [Fact]
    public void Malformed_RemovalOfMissingSlotIsNoOp()
    {
        Replicator replicator = CreateReplicator();
        IntListHolder client = CreateClientWithThreeItems(replicator);
        byte[] data = Craft(w =>
        {
            w.WriteBool(true);
            w.WriteBool(true);
            w.WriteBool(false);
            w.WriteBool(true);
            w.WriteVarUInt(7); // отсутствует
            w.WriteBool(true);
            w.WriteVarUInt(1);
            w.WriteBool(false);
            w.WriteBool(false);
            w.WriteBool(false);
        });

        replicator.Apply(client, data);
        Assert.Equal([10, 30], client.Values);
    }

    [Fact]
    public void Malformed_TruncatedDataThrows()
    {
        Replicator replicator = CreateReplicator();
        var server = new Composite();
        server.Values.AddRange([1, 2, 3]);
        server.Items.Add(new Item { A = 1, Name = "abc" });
        server.Rows.Add(new ReplicatedList<int> { 1 });
        byte[] data = Delta(replicator, replicator.CreateBaseline(server));

        for (int length = 0; length < data.Length; length++)
        {
            var client = new Composite();
            byte[] truncated = data.AsSpan(0, length).ToArray();
            Assert.Throws<ReplicationFormatException>(() => replicator.Apply(client, truncated));

            // Список после ошибки остается согласованным: перечисление и индексатор совпадают.
            int index = 0;
            foreach (int value in client.Values)
            {
                Assert.Equal(value, client.Values[index++]);
            }

            Assert.Equal(client.Values.Count, index);
        }
    }

    [Fact]
    public void Write_CollectionAboveLimitThrows()
    {
        Replicator replicator = CreateReplicator(new ReplicationLimits { MaxCollectionCount = 3 });
        var server = new IntListHolder();
        server.Values.AddRange([1, 2, 3]);
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        Delta(replicator, baseline);

        server.Values.Add(4);
        var ex = Assert.Throws<ReplicationException>(() => replicator.TryWriteDelta(baseline, out _));
        Assert.Contains(nameof(IntListHolder.Values), ex.Message);
    }

    // ---------- allocations ----------

    [Fact]
    public void UnchangedListDelta_DoesNotAllocate()
    {
        Replicator replicator = CreateReplicator();
        var server = new Composite();
        for (int i = 0; i < 100; i++)
        {
            server.Values.Add(i);
            server.Items.Add(new Item { A = i });
        }

        server.Rows.Add(new ReplicatedList<int> { 1, 2 });
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var writer = new BitWriter(1 << 16);
        Assert.True(replicator.TryWriteDelta(baseline, writer));
        writer.Reset();
        Assert.False(replicator.TryWriteDelta(baseline, writer));

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            replicator.TryWriteDelta(baseline, writer);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void ChangedListDeltaAndApply_DoNotAllocate()
    {
        Replicator replicator = CreateReplicator();
        var server = new Composite();
        for (int i = 0; i < 20; i++)
        {
            server.Values.Add(i);
            server.Items.Add(new Item { A = i });
        }

        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var writer = new BitWriter(1 << 16);
        var client = new Composite();

        void Frame(int i)
        {
            writer.Reset();
            server.Values[i % 20] = i + 1000;
            server.Items[i % 20].A = i + 1000;
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
        AssertEqual(server, client);
    }
}
