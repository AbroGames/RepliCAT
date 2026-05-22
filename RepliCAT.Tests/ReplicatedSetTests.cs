using RepliCAT;
using RepliCAT.Bits;
using Serilog;

namespace RepliCAT.Tests;

public class ReplicatedSetTests
{
    // ---------- test infrastructure ----------

    private static Replicator CreateReplicator(ReplicationLimits limits = null)
    {
        ILogger logger = new LoggerConfiguration().CreateLogger();
        return new Replicator(limits: limits, logger: logger);
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

    public class IntSetHolder
    {
        [Replicated] public ReplicatedSet<int> Values = new();
    }

    public class StringSetHolder
    {
        [Replicated] public ReplicatedSet<string> Values = new();
    }

    public class NullableSetHolder
    {
        [Replicated] public ReplicatedSet<int?> Values = new();
    }

    public class ReplaceableHolder
    {
        private ReplicatedSet<int> _values = new();

        public int SetterCalls;

        [Replicated]
        public ReplicatedSet<int> Values
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
        [Replicated] public ReplicatedSet<int> Values { get; } = new();
    }

    public class ManualHolder
    {
        [Replicated(Manual = true)] public readonly ReplicatedSet<int> Values = new();
    }

    public enum Slot
    {
        Head,
        Chest,
        Legs
    }

    public class ItemKindsHolder
    {
        [Replicated] public ReplicatedSet<Slot> Slots = new();
        [Replicated] public ReplicatedSet<Godot.Vector2I> Cells = new();
        [Replicated] public ReplicatedSet<long> Ids = new();
        [Replicated] public ReplicatedSet<int?> Nullable = new();
    }

    public class Entity
    {
        [Replicated] public int Id;
        [Replicated] public ReplicatedSet<int> Tags = new();
    }

    public class Composite
    {
        [Replicated] public int Frame;
        [Replicated] public ReplicatedSet<int> Values { get; set; } = new();
        [Replicated] public ReplicatedSet<int> Other { get; set; } = new();
        [Replicated] public ReplicatedSet<string> Names { get; set; } = new();
        [Replicated] public ReplicatedDictionary<int, ReplicatedSet<int>> Groups { get; set; } = new();
        [Replicated] public ReplicatedList<ReplicatedSet<string>> Rows { get; set; } = new();
        [Replicated] public ReplicatedList<Entity> Entities { get; set; } = new();
    }

    public static class Invalid
    {
        public class Item
        {
            [Replicated] public int A;
        }

        public class ObjectItems
        {
            [Replicated] public ReplicatedSet<Item> Values = new();
        }

        public class ListItems
        {
            [Replicated] public ReplicatedSet<ReplicatedList<int>> Values = new();
        }

        public class QuantizedItems
        {
            [Replicated, Quantize(0.1)] public ReplicatedSet<float> Values = new();
        }

        public class TolerantItems
        {
            [Replicated(Tolerance = 1)] public ReplicatedSet<int> Values = new();
        }

        public class QuantizedListOfSets
        {
            [Replicated, Quantize(0.1)] public ReplicatedList<ReplicatedSet<float>> Values = new();
        }

        public class PlainHashSet
        {
            [Replicated] public HashSet<int> Values = new();
        }
    }

    // ---------- equality ----------

    private static void AssertSetEqual<T>(ReplicatedSet<T> expected, ReplicatedSet<T> actual)
    {
        if (expected == null)
        {
            Assert.Null(actual);
            return;
        }

        Assert.NotNull(actual);
        Assert.Equal(expected.Count, actual.Count);
        foreach (T item in expected)
        {
            Assert.True(actual.Contains(item), $"missing item {item}");
        }

        var model = new HashSet<T>(expected);
        int enumerated = 0;
        foreach (T item in actual)
        {
            Assert.True(model.Contains(item), $"extra item {item}");
            enumerated++;
        }

        Assert.Equal(expected.Count, enumerated);
    }

    private static void AssertEqual(Composite expected, Composite actual)
    {
        Assert.Equal(expected.Frame, actual.Frame);
        AssertSetEqual(expected.Values, actual.Values);
        AssertSetEqual(expected.Other, actual.Other);
        AssertSetEqual(expected.Names, actual.Names);

        Assert.Equal(expected.Groups.Count, actual.Groups.Count);
        foreach (KeyValuePair<int, ReplicatedSet<int>> pair in expected.Groups)
        {
            Assert.True(actual.Groups.TryGetValue(pair.Key, out ReplicatedSet<int> group));
            AssertSetEqual(pair.Value, group);
        }

        Assert.Equal(expected.Rows.Count, actual.Rows.Count);
        for (int i = 0; i < expected.Rows.Count; i++)
        {
            AssertSetEqual(expected.Rows[i], actual.Rows[i]);
        }

        Assert.Equal(expected.Entities.Count, actual.Entities.Count);
        for (int i = 0; i < expected.Entities.Count; i++)
        {
            if (expected.Entities[i] == null)
            {
                Assert.Null(actual.Entities[i]);
                continue;
            }

            Assert.Equal(expected.Entities[i].Id, actual.Entities[i].Id);
            AssertSetEqual(expected.Entities[i].Tags, actual.Entities[i].Tags);
        }
    }

    // ---------- wire decoding (IntSetHolder) ----------

    private sealed class DecodedSet
    {
        public bool Present;
        public bool Reset;
        public readonly List<int> Removals = new();
        public readonly List<int> Inserts = new();
    }

    /// <summary>
    /// Разбирает полезную нагрузку <see cref="IntSetHolder"/>: формат словаря без значений.
    /// </summary>
    private static DecodedSet DecodeIntSet(byte[] data)
    {
        var result = new DecodedSet();
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
                result.Removals.Add((int)(uint)reader.ReadBits(32));
            }
        }

        while (reader.ReadBool())
        {
            result.Inserts.Add((int)(uint)reader.ReadBits(32));
        }

        Assert.True(reader.RemainingBits < 8);
        return result;
    }

    // ---------- HashSet<T> parity ----------

    private static int[] RandomItems(Random random)
    {
        var items = new int[random.Next(8)];
        for (int i = 0; i < items.Length; i++)
        {
            items[i] = random.Next(30);
        }

        return items;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Parity_BehavesLikeHashSet(int seed)
    {
        var random = new Random(seed);
        var set = new ReplicatedSet<int>();
        var model = new HashSet<int>();

        for (int step = 0; step < 3000; step++)
        {
            int item = random.Next(30);
            int[] items = RandomItems(random);

            // Коллекции разных видов: массив с повторами, HashSet и ReplicatedSet.
            IEnumerable<int> other = random.Next(3) switch
            {
                0 => items,
                1 => new HashSet<int>(items),
                _ => new ReplicatedSet<int>(items)
            };

            switch (random.Next(14))
            {
                case 0:
                case 1:
                case 2:
                    Assert.Equal(model.Add(item), set.Add(item));
                    break;
                case 3:
                case 4:
                    Assert.Equal(model.Remove(item), set.Remove(item));
                    break;
                case 5:
                    if (random.Next(10) == 0)
                    {
                        set.Clear();
                        model.Clear();
                    }

                    break;
                case 6:
                {
                    int divisor = random.Next(2, 6);
                    Assert.Equal(model.RemoveWhere(x => x % divisor == 0), set.RemoveWhere(x => x % divisor == 0));
                    break;
                }
                case 7:
                    set.UnionWith(other);
                    model.UnionWith(items);
                    break;
                case 8:
                    if (random.Next(4) == 0)
                    {
                        // Ленивая коллекция поверх самого множества.
                        set.IntersectWith(items.Concat(set.Where(x => x < 10)));
                        model.IntersectWith(items.Concat(model.Where(x => x < 10)).ToArray());
                    }
                    else
                    {
                        set.IntersectWith(other);
                        model.IntersectWith(items);
                    }

                    break;
                case 9:
                    set.ExceptWith(other);
                    model.ExceptWith(items);
                    break;
                case 10:
                    set.SymmetricExceptWith(other);
                    model.SymmetricExceptWith(items);
                    break;
                case 11:
                {
                    // Проверки против коллекций, построенных из самого множества.
                    int[] subset = model.Where(_ => random.Next(2) == 0).ToArray();
                    int[] superset = model.Concat(items).ToArray();
                    foreach (int[] candidate in new[] { items, subset, superset, model.ToArray() })
                    {
                        AssertQueriesMatch(model, set, candidate);
                    }

                    break;
                }
                case 12:
                    AssertQueriesMatch(model, set, other);
                    break;
                case 13:
                {
                    Assert.Equal(model.Contains(item), set.Contains(item));
                    var expected = new int[model.Count + 1];
                    var actual = new int[model.Count + 1];
                    model.CopyTo(expected, 1);
                    set.CopyTo(actual, 1);
                    Assert.Equal(expected.Skip(1).Order(), actual.Skip(1).Order());
                    break;
                }
            }

            Assert.Equal(model.Count, set.Count);
            Assert.Equal(model.Order(), set.Order());
        }
    }

    private static void AssertQueriesMatch(HashSet<int> model, ReplicatedSet<int> set, IEnumerable<int> other)
    {
        int[] items = other.ToArray();
        Assert.Equal(model.IsSubsetOf(items), set.IsSubsetOf(other));
        Assert.Equal(model.IsProperSubsetOf(items), set.IsProperSubsetOf(other));
        Assert.Equal(model.IsSupersetOf(items), set.IsSupersetOf(other));
        Assert.Equal(model.IsProperSupersetOf(items), set.IsProperSupersetOf(other));
        Assert.Equal(model.Overlaps(items), set.Overlaps(other));
        Assert.Equal(model.SetEquals(items), set.SetEquals(other));
    }

    [Fact]
    public void SetOperationsWithItself()
    {
        var set = new ReplicatedSet<int> { 1, 2, 3 };
        Assert.True(set.IsSubsetOf(set));
        Assert.False(set.IsProperSubsetOf(set));
        Assert.True(set.IsSupersetOf(set));
        Assert.False(set.IsProperSupersetOf(set));
        Assert.True(set.Overlaps(set));
        Assert.True(set.SetEquals(set));

        set.UnionWith(set);
        set.IntersectWith(set);
        Assert.Equal([1, 2, 3], set.Order());

        set.SymmetricExceptWith(set);
        Assert.Empty(set);

        set.UnionWith([4, 5]);
        set.ExceptWith(set);
        Assert.Empty(set);

        // Изменение множества во время перечисления его самого через LINQ.
        set.UnionWith([1, 2, 3, 4]);
        set.ExceptWith(set.Where(x => x % 2 == 0));
        Assert.Equal([1, 3], set.Order());
        set.IntersectWith(set.Where(x => x > 1));
        Assert.Equal([3], set.Order());
    }

    [Fact]
    public void SetApi_EdgeCases()
    {
        var set = new ReplicatedSet<string>(["a", "b", "a"]);
        Assert.Equal(2, set.Count);
        Assert.False(((ICollection<string>)set).IsReadOnly);

        Assert.Throws<ArgumentNullException>(() => set.Add(null));
        Assert.Throws<ArgumentNullException>(() => set.Remove(null));
        Assert.Throws<ArgumentNullException>(() => set.Contains(null));
        Assert.Throws<ArgumentNullException>(() => new ReplicatedSet<string>(["a", null]));
        Assert.Throws<ArgumentNullException>(() => new ReplicatedSet<int>((IEnumerable<int>)null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReplicatedSet<int>(-1));
        Assert.Throws<ArgumentNullException>(() => set.RemoveWhere(null));
        Assert.Throws<ArgumentNullException>(() => set.UnionWith(null));
        Assert.Throws<ArgumentNullException>(() => set.IsSubsetOf(null));
        Assert.Throws<ArgumentNullException>(() => set.CopyTo(null, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => set.CopyTo(new string[2], -1));
        Assert.Throws<ArgumentException>(() => set.CopyTo(new string[2], 1));

        // null в other: добавлять его нельзя, а в проверках его просто нет в множестве.
        Assert.Throws<ArgumentNullException>(() => set.UnionWith(["c", null]));
        Assert.Throws<ArgumentNullException>(() => new ReplicatedSet<string>(["x"]).SymmetricExceptWith(["y", null]));
        set.Remove("c");
        set.ExceptWith(["a", null]);
        Assert.Equal(["b"], set.Order());
        set.Add("a");
        set.IntersectWith(["a", "b", null]);
        Assert.Equal(["a", "b"], set.Order());
        Assert.True(set.IsSubsetOf(["a", "b", null]));
        Assert.True(set.IsProperSubsetOf(["a", "b", null]));
        Assert.False(set.IsSupersetOf(["a", null]));
        Assert.False(set.IsProperSupersetOf([null]));
        Assert.False(set.Overlaps([null]));
        Assert.True(set.Overlaps([null, "b"]));
        Assert.False(set.SetEquals(["a", null]));

        // Nullable<T>: null не может быть элементом.
        var nullable = new ReplicatedSet<int?> { 1 };
        Assert.Throws<ArgumentNullException>(() => nullable.Add(null));
        Assert.False(nullable.IsSupersetOf([1, null]));

        // Интерфейсы.
        ISet<string> asSet = set;
        Assert.False(asSet.Add("a"));
        ((ICollection<string>)set).Add("c");
        IReadOnlySet<string> readOnly = set;
        Assert.True(readOnly.Contains("c"));
        Assert.Equal(["a", "b", "c"], ((IEnumerable<string>)set).Order());

        // Reset перечислителя (без using: переменная using у структуры доступна только для чтения).
        ReplicatedSet<string>.Enumerator enumerator = set.GetEnumerator();
        var first = new List<string>();
        while (enumerator.MoveNext())
        {
            first.Add(enumerator.Current);
        }

        enumerator.Reset();
        var second = new List<string>();
        while (enumerator.MoveNext())
        {
            second.Add(enumerator.Current);
        }

        Assert.Equal(first, second);
        Assert.Equal(3, second.Count);
        enumerator.Dispose();
    }

    [Fact]
    public void Enumerator_ThrowsWhenModified()
    {
        var set = new ReplicatedSet<int> { 1, 2 };

        Assert.Throws<InvalidOperationException>(() =>
        {
            foreach (int item in set)
            {
                set.Add(item + 10);
            }
        });
    }

    // ---------- replication ----------

    [Fact]
    public void AllOperationsReplicate()
    {
        Replicator replicator = CreateReplicator();
        var server = new IntSetHolder();
        server.Values.Add(1);
        server.Values.Add(2);
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var client = new IntSetHolder();
        ReplicatedSet<int> clientSet = client.Values;

        void Frame(Action<ReplicatedSet<int>> mutate)
        {
            mutate(server.Values);
            replicator.Apply(client, Delta(replicator, baseline));
            Assert.Same(clientSet, client.Values);
            AssertSetEqual(server.Values, client.Values);
        }

        Frame(_ => { });
        Frame(s => s.Add(3));
        Frame(s => s.Remove(2));
        Frame(s =>
        {
            s.Remove(1);
            s.Add(4);
            s.Add(5);
        });
        Frame(s => s.UnionWith([6, 7, 8]));
        Frame(s => s.IntersectWith([3, 4, 6, 7, 100]));
        Frame(s => s.SymmetricExceptWith([3, 9]));
        Frame(s => s.RemoveWhere(x => x > 6));
        Frame(s => s.Clear());
        Frame(s => s.UnionWith([10, 11]));
        Frame(s =>
        {
            s.Clear();
            s.Add(12);
        });
    }

    [Fact]
    public void Wire_OnlyChangedItemsAreSent()
    {
        Replicator replicator = CreateReplicator();
        var server = new IntSetHolder();
        for (int i = 0; i < 10; i++)
        {
            server.Values.Add(i);
        }

        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        DecodedSet first = DecodeIntSet(Delta(replicator, baseline));
        Assert.True(first.Reset);
        Assert.Equal(10, first.Inserts.Count);

        server.Values.Add(100);
        DecodedSet add = DecodeIntSet(Delta(replicator, baseline));
        Assert.False(add.Reset);
        Assert.Empty(add.Removals);
        Assert.Equal([100], add.Inserts);

        server.Values.Remove(4);
        server.Values.Add(-1);
        DecodedSet removeAdd = DecodeIntSet(Delta(replicator, baseline));
        Assert.False(removeAdd.Reset);
        Assert.Equal([4], removeAdd.Removals);
        Assert.Equal([-1], removeAdd.Inserts);

        // Удалить и вернуть тот же элемент — изменений нет.
        server.Values.Remove(5);
        server.Values.Add(5);
        Assert.False(replicator.TryWriteDelta(baseline, out _));

        // Добавить и тут же удалить — тоже.
        var writer = new BitWriter();
        server.Values.Add(200);
        server.Values.Remove(200);
        Assert.False(replicator.TryWriteDelta(baseline, writer));
        Assert.Equal(0, writer.BitPosition);
    }

    [Fact]
    public void ItemKinds_Replicate()
    {
        Replicator replicator = CreateReplicator();
        var server = new ItemKindsHolder();
        server.Slots.Add(Slot.Head);
        server.Slots.Add(Slot.Legs);
        server.Cells.Add(new Godot.Vector2I(-3, 7));
        server.Ids.Add(long.MinValue);
        server.Ids.Add(long.MaxValue);
        server.Nullable.Add(0);
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var client = new ItemKindsHolder();
        replicator.Apply(client, Delta(replicator, baseline));

        AssertSetEqual(server.Slots, client.Slots);
        AssertSetEqual(server.Cells, client.Cells);
        AssertSetEqual(server.Ids, client.Ids);
        AssertSetEqual(server.Nullable, client.Nullable);

        server.Slots.Remove(Slot.Head);
        server.Slots.Add(Slot.Chest);
        server.Cells.Add(new Godot.Vector2I(1, 1));
        server.Nullable.Add(-5);
        replicator.Apply(client, Delta(replicator, baseline));
        AssertSetEqual(server.Slots, client.Slots);
        AssertSetEqual(server.Cells, client.Cells);
        AssertSetEqual(server.Nullable, client.Nullable);
    }

    [Fact]
    public void StringItems_Replicate()
    {
        Replicator replicator = CreateReplicator();
        var server = new StringSetHolder();
        server.Values.UnionWith(["a", "", "длинный элемент"]);
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var client = new StringSetHolder();
        replicator.Apply(client, Delta(replicator, baseline));
        AssertSetEqual(server.Values, client.Values);

        server.Values.Remove("");
        server.Values.Add("b");
        replicator.Apply(client, Delta(replicator, baseline));
        AssertSetEqual(server.Values, client.Values);
    }

    // ---------- instances ----------

    [Fact]
    public void GetOnlySetProperty_ReusesInstance()
    {
        Replicator replicator = CreateReplicator();
        var server = new GetOnlyHolder();
        server.Values.Add(2);
        var client = new GetOnlyHolder();
        ReplicatedSet<int> clientSet = client.Values;
        replicator.Apply(client, Delta(replicator, replicator.CreateBaseline(server)));
        Assert.Same(clientSet, client.Values);
        AssertSetEqual(server.Values, client.Values);
    }

    [Fact]
    public void ReplacingSetInstance_ResetsAndClientKeepsInstance()
    {
        Replicator replicator = CreateReplicator();
        var server = new ReplaceableHolder();
        server.Values.UnionWith([1, 2]);
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var client = new ReplaceableHolder();
        ReplicatedSet<int> clientSet = client.Values;
        replicator.Apply(client, Delta(replicator, baseline));
        Assert.Equal(0, client.SetterCalls);

        server.Values = new ReplicatedSet<int> { 2, 3 };
        byte[] data = Delta(replicator, baseline);
        replicator.Apply(client, data);
        Assert.Same(clientSet, client.Values);
        Assert.Equal(0, client.SetterCalls);
        AssertSetEqual(server.Values, client.Values);

        var decoded = new BitReader(data);
        decoded.ReadBits(1);
        Assert.True(decoded.ReadBool()); // present
        Assert.True(decoded.ReadBool()); // reset

        // Та же ссылка после сброса — обычная дельта.
        server.Values.Add(4);
        replicator.Apply(client, Delta(replicator, baseline));
        AssertSetEqual(server.Values, client.Values);
    }

    [Fact]
    public void NullSet_TransitionsBothWays()
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
        Assert.Equal(1, client.SetterCalls);
        Assert.False(replicator.TryWriteDelta(baseline, out _));

        var late = new ReplaceableHolder();
        replicator.Apply(late, Snapshot(replicator, baseline));
        Assert.Null(late.Values);

        // Клиент без экземпляра создает новое множество.
        server.Values = new ReplicatedSet<int> { 4, 5 };
        replicator.Apply(client, Delta(replicator, baseline));
        replicator.Apply(late, Snapshot(replicator, baseline));
        Assert.Equal(2, client.SetterCalls);
        AssertSetEqual(server.Values, client.Values);
        AssertSetEqual(server.Values, late.Values);

        // Новое множество клиента работает как обычное.
        server.Values.Remove(4);
        replicator.Apply(client, Delta(replicator, baseline));
        AssertSetEqual(server.Values, client.Values);
    }

    [Fact]
    public void ManualSet_SentOnlyWhenMarked()
    {
        Replicator replicator = CreateReplicator();
        var server = new ManualHolder();
        server.Values.Add(1);
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var client = new ManualHolder();
        replicator.Apply(client, Delta(replicator, baseline));
        AssertSetEqual(server.Values, client.Values);

        server.Values.Add(2);
        Assert.False(replicator.TryWriteDelta(baseline, out _));

        ManualReplication.MarkDirty(server, nameof(ManualHolder.Values));
        replicator.Apply(client, Delta(replicator, baseline));
        AssertSetEqual(server.Values, client.Values);
        Assert.False(replicator.TryWriteDelta(baseline, out _));
    }

    // ---------- nesting ----------

    [Fact]
    public void NestedSets_Replicate()
    {
        Replicator replicator = CreateReplicator();
        var server = new Composite();
        server.Groups[1] = new ReplicatedSet<int> { 1, 2 };
        server.Groups[2] = null;
        server.Rows.Add(new ReplicatedSet<string> { "a" });
        server.Rows.Add(null);
        server.Entities.Add(new Entity { Id = 1, Tags = { 10, 11 } });
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var client = new Composite();

        void Frame(Action mutate)
        {
            mutate();
            replicator.Apply(client, Delta(replicator, baseline));
            AssertEqual(server, client);
        }

        Frame(() => { });
        ReplicatedSet<int> clientGroup = client.Groups[1];
        ReplicatedSet<string> clientRow = client.Rows[0];
        ReplicatedSet<int> clientTags = client.Entities[0].Tags;

        Frame(() => server.Groups[1].Add(3));
        Assert.Same(clientGroup, client.Groups[1]);

        Frame(() => server.Rows[0].Remove("a"));
        Assert.Same(clientRow, client.Rows[0]);

        Frame(() => server.Entities[0].Tags.SymmetricExceptWith([11, 12]));
        Assert.Same(clientTags, client.Entities[0].Tags);

        Frame(() =>
        {
            server.Groups[2] = new ReplicatedSet<int> { 7 };
            server.Groups[1] = new ReplicatedSet<int> { 42 };
            server.Rows[1] = new ReplicatedSet<string> { "x", "y" };
            server.Entities.Add(new Entity { Id = 2 });
        });
        Assert.Same(clientGroup, client.Groups[1]);

        Frame(() =>
        {
            server.Groups.Remove(1);
            server.Rows.Insert(0, new ReplicatedSet<string>());
            server.Entities[1].Tags.Add(5);
        });

        Assert.False(replicator.TryWriteDelta(baseline, out _));
    }

    [Fact]
    public void Depth_SetsCountAsLevels()
    {
        var server = new IntSetHolder();
        server.Values.Add(1);

        Replicator ok = CreateReplicator(new ReplicationLimits { MaxDepth = 1 });
        var client = new IntSetHolder();
        ok.Apply(client, Delta(ok, ok.CreateBaseline(server)));
        Assert.Single(client.Values);

        // Словарь множеств — два уровня.
        var groups = new Composite();
        groups.Groups[1] = new ReplicatedSet<int> { 1 };
        Assert.Throws<ReplicationException>(() => ok.TryWriteDelta(ok.CreateBaseline(groups), out _));

        Replicator deeper = CreateReplicator(new ReplicationLimits { MaxDepth = 2 });
        byte[] data = Delta(deeper, deeper.CreateBaseline(groups));
        Assert.Throws<ReplicationFormatException>(() => ok.Apply(new Composite(), data));
    }

    // ---------- model errors and schema ----------

    [Fact]
    public void ModelErrors_ThrowWithMemberPath()
    {
        Replicator replicator = CreateReplicator();

        var objectItems = Assert.Throws<ReplicationException>(() => replicator.CreateBaseline(new Invalid.ObjectItems()));
        Assert.Contains($"{typeof(Invalid.ObjectItems).FullName}.{nameof(Invalid.ObjectItems.Values)}", objectItems.Message);
        Assert.Contains("set item", objectItems.Message);

        var listItems = Assert.Throws<ReplicationException>(() => replicator.CreateBaseline(new Invalid.ListItems()));
        Assert.Contains(nameof(Invalid.ListItems.Values), listItems.Message);

        var quantized = Assert.Throws<ReplicationException>(() => replicator.CreateBaseline(new Invalid.QuantizedItems()));
        Assert.Contains(nameof(Invalid.QuantizedItems.Values), quantized.Message);

        var tolerant = Assert.Throws<ReplicationException>(() => replicator.CreateBaseline(new Invalid.TolerantItems()));
        Assert.Contains(nameof(Invalid.TolerantItems.Values), tolerant.Message);

        Assert.Throws<ReplicationException>(() => replicator.CreateBaseline(new Invalid.QuantizedListOfSets()));

        var hashSet = Assert.Throws<ReplicationException>(() => replicator.CreateBaseline(new Invalid.PlainHashSet()));
        Assert.Contains("ReplicatedSet<T>", hashSet.Message);

        Assert.Throws<ReplicationException>(() => replicator.CreateBaseline(new ReplicatedSet<int>()));
    }

    [Fact]
    public void SchemaHash_DescribesItems()
    {
        Replicator first = CreateReplicator();
        Replicator second = CreateReplicator();
        Assert.Equal(first.GetSchemaHash(typeof(Composite)), second.GetSchemaHash(typeof(Composite)));

        ulong intSet = first.GetSchemaHash(typeof(IntSetHolder));
        Assert.NotEqual(intSet, first.GetSchemaHash(typeof(SchemaVariants.LongSet)));
        Assert.NotEqual(intSet, first.GetSchemaHash(typeof(SchemaVariants.IntList)));
        Assert.NotEqual(intSet, first.GetSchemaHash(typeof(SchemaVariants.IntDictionary)));
    }

    public static class SchemaVariants
    {
        public class LongSet
        {
            [Replicated] public ReplicatedSet<long> Values = new();
        }

        public class IntList
        {
            [Replicated] public ReplicatedList<int> Values = new();
        }

        public class IntDictionary
        {
            [Replicated] public ReplicatedDictionary<int, int> Values = new();
        }
    }

    // ---------- snapshots ----------

    [Fact]
    public void MidFrameSnapshotPlusDelta_MatchesServer()
    {
        Replicator replicator = CreateReplicator();
        var server = new Composite();
        server.Values.UnionWith([1, 2]);
        server.Names.Add("a");
        server.Groups[1] = new ReplicatedSet<int> { 5 };
        server.Rows.Add(new ReplicatedSet<string> { "x" });
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var client = new Composite();
        replicator.Apply(client, Delta(replicator, baseline));

        // Кадр: часть изменений до снимка, часть после; элемент удаляется и возвращается обратно.
        server.Values.Remove(1);
        server.Values.Add(3);
        server.Rows[0].Remove("x");
        byte[] snapshot = Snapshot(replicator, baseline);
        server.Values.Add(1);
        server.Names.Remove("a");
        server.Groups[1].Add(6);
        server.Rows[0].Add("x");
        server.Rows[0].Add("y");
        byte[] delta = Delta(replicator, baseline);

        var late = new Composite();
        late.Values.UnionWith([9, 1]); // мусор, который сброс должен убрать
        late.Names.Add("z");
        replicator.Apply(late, snapshot);
        replicator.Apply(late, delta);
        replicator.Apply(client, delta);

        AssertEqual(server, client);
        AssertEqual(server, late);
    }

    // ---------- randomized ----------

    private static void MutateInts(ReplicatedSet<int> set, Random random)
    {
        int item = random.Next(40);
        switch (random.Next(9))
        {
            case 0:
            case 1:
            case 2:
                set.Add(item);
                break;
            case 3:
            case 4:
                set.Remove(item);
                break;
            case 5:
                if (random.Next(10) == 0)
                {
                    set.Clear();
                }

                break;
            case 6:
                // Удалить и вернуть.
                if (set.Remove(item))
                {
                    set.Add(item);
                }

                break;
            case 7:
            {
                int[] items = RandomItems(random);
                switch (random.Next(4))
                {
                    case 0:
                        set.UnionWith(items);
                        break;
                    case 1:
                        set.ExceptWith(items);
                        break;
                    case 2:
                        set.SymmetricExceptWith(items);
                        break;
                    case 3:
                        if (random.Next(4) == 0)
                        {
                            set.IntersectWith(items.Concat(set.Take(10)));
                        }

                        break;
                }

                break;
            }
            case 8:
            {
                int divisor = random.Next(3, 10);
                set.RemoveWhere(x => x % divisor == 0);
                break;
            }
        }
    }

    private static void Mutate(Composite server, Random random)
    {
        switch (random.Next(14))
        {
            case 0:
            case 1:
            case 2:
                if (server.Values != null)
                {
                    MutateInts(server.Values, random);
                }

                break;
            case 3:
                if (server.Other != null)
                {
                    MutateInts(server.Other, random);
                }

                break;
            case 4:
            case 5:
            {
                string name = "n" + random.Next(20);
                if (random.Next(3) == 0)
                {
                    server.Names.Remove(name);
                }
                else
                {
                    server.Names.Add(name);
                }

                break;
            }
            case 6:
            case 7:
            {
                // Словарь множеств.
                int key = random.Next(6);
                int action = random.Next(6);
                if (action == 0)
                {
                    server.Groups[key] = random.Next(5) == 0 ? null : new ReplicatedSet<int>();
                }
                else if (action == 1)
                {
                    server.Groups.Remove(key);
                }
                else if (server.Groups.TryGetValue(key, out ReplicatedSet<int> group) && group != null)
                {
                    MutateInts(group, random);
                }

                break;
            }
            case 8:
            case 9:
            {
                // Список множеств.
                ReplicatedList<ReplicatedSet<string>> rows = server.Rows;
                int count = rows.Count;
                int action = random.Next(6);
                if (action == 0 && count < 6)
                {
                    rows.Insert(random.Next(count + 1), random.Next(5) == 0 ? null : new ReplicatedSet<string>());
                }
                else if (action == 1 && count > 0)
                {
                    rows.RemoveAt(random.Next(count));
                }
                else if (count > 0 && rows[random.Next(count)] is { } row)
                {
                    string item = "r" + random.Next(8);
                    if (!row.Remove(item))
                    {
                        row.Add(item);
                    }
                }

                break;
            }
            case 10:
            case 11:
            {
                // Объекты с множеством внутри.
                ReplicatedList<Entity> entities = server.Entities;
                int count = entities.Count;
                int action = random.Next(8);
                if (action == 0 && count < 6)
                {
                    entities.Add(random.Next(5) == 0 ? null : new Entity { Id = random.Next(100) });
                }
                else if (action == 1 && count > 0)
                {
                    entities.RemoveAt(random.Next(count));
                }
                else if (count > 0 && entities[random.Next(count)] is { } entity)
                {
                    if (random.Next(4) == 0)
                    {
                        entity.Id = random.Next(100);
                    }
                    else
                    {
                        MutateInts(entity.Tags, random);
                    }
                }

                break;
            }
            case 12:
                // Редкая замена экземпляров, null, обмен и совместное использование множеств.
                switch (random.Next(10))
                {
                    case 0:
                        server.Values = server.Values == null ? new ReplicatedSet<int> { 1 } : null;
                        break;
                    case 1:
                        server.Values = new ReplicatedSet<int>(server.Values ?? new ReplicatedSet<int>());
                        break;
                    case 2:
                        (server.Values, server.Other) = (server.Other, server.Values);
                        break;
                    case 3:
                        server.Other = server.Values;
                        break;
                    case 4:
                        server.Other = new ReplicatedSet<int>();
                        break;
                    case 5:
                    {
                        var copy = new ReplicatedSet<string>(server.Names);
                        copy.Remove("n" + random.Next(20));
                        server.Names = copy;
                        break;
                    }
                }

                break;
            case 13:
                server.Frame++;
                break;
        }
    }

    private static Composite CreateLateClient(Random random)
    {
        var late = new Composite();
        if (random.Next(2) == 0)
        {
            // Мусор, который снимок должен убрать или перезаписать.
            late.Values.UnionWith([1, 1000]);
            late.Other = null;
            late.Names.Add("junk");
            late.Groups[2] = new ReplicatedSet<int> { -1 };
            late.Rows.Add(new ReplicatedSet<string> { "junk" });
            late.Entities.Add(new Entity { Id = -1, Tags = { -1 } });
        }

        return late;
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
        ReplicatedSet<string> clientNames = client.Names;
        var lateClients = new List<Composite>();
        int lateCount = 0;

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
                foreach (Composite late in lateClients)
                {
                    replicator.Apply(late, delta);
                }
            }

            if (snapshot != null)
            {
                Composite late = CreateLateClient(random);
                replicator.Apply(late, snapshot);
                if (snapshotBeforeDelta && hasDelta)
                {
                    replicator.Apply(late, delta);
                }

                lateClients.Add(late);
                if (lateClients.Count > 4)
                {
                    lateClients.RemoveAt(0);
                }

                lateCount++;
            }

            AssertEqual(server, client);
            Assert.Same(clientNames, client.Names);
            foreach (Composite late in lateClients)
            {
                AssertEqual(server, late);
            }

            // Повторная дельта без изменений пуста.
            Assert.False(replicator.TryWriteDelta(baseline, out _));
        }

        Assert.True(lateCount > 20);
    }

    // ---------- malformed data ----------

    /// <summary>
    /// Клиент с множеством { "a", "b", "c" }.
    /// </summary>
    private static StringSetHolder CreateClientWithThreeItems(Replicator replicator)
    {
        var server = new StringSetHolder();
        server.Values.UnionWith(["a", "b", "c"]);
        var client = new StringSetHolder();
        replicator.Apply(client, Delta(replicator, replicator.CreateBaseline(server)));
        return client;
    }

    private static void WriteItem(BitWriter writer, string item)
    {
        writer.WriteBool(true);
        RepliCAT.Codecs.StringCodec.Default.Write(writer, item);
    }

    [Fact]
    public void Malformed_NullItemThrows()
    {
        Replicator replicator = CreateReplicator();

        byte[] insert = Craft(w =>
        {
            w.WriteBool(true); // маска
            w.WriteBool(true); // present
            w.WriteBool(false); // reset
            w.WriteBool(false); // конец удалений
            WriteItem(w, null);
            w.WriteBool(false);
        });
        Assert.Throws<ReplicationFormatException>(() => replicator.Apply(CreateClientWithThreeItems(replicator), insert));

        byte[] removal = Craft(w =>
        {
            w.WriteBool(true);
            w.WriteBool(true);
            w.WriteBool(false);
            WriteItem(w, null);
            w.WriteBool(false);
            w.WriteBool(false);
        });
        Assert.Throws<ReplicationFormatException>(() => replicator.Apply(CreateClientWithThreeItems(replicator), removal));

        byte[] reset = Craft(w =>
        {
            w.WriteBool(true);
            w.WriteBool(true);
            w.WriteBool(true);
            WriteItem(w, null);
            w.WriteBool(false);
        });
        var ex = Assert.Throws<ReplicationFormatException>(
            () => replicator.Apply(CreateClientWithThreeItems(replicator), reset));
        Assert.Contains("null set key", ex.Message);

        // Nullable<int> без значения — тоже ошибка формата.
        byte[] nullable = Craft(w =>
        {
            w.WriteBool(true);
            w.WriteBool(true);
            w.WriteBool(true);
            w.WriteBool(true);
            w.WriteBool(false); // hasValue = 0
            w.WriteBool(false);
        });
        Assert.Throws<ReplicationFormatException>(() => replicator.Apply(new NullableSetHolder(), nullable));
    }

    [Fact]
    public void Malformed_DuplicateItemInResetThrows()
    {
        Replicator replicator = CreateReplicator();

        byte[] newItem = Craft(w =>
        {
            w.WriteBool(true);
            w.WriteBool(true);
            w.WriteBool(true);
            WriteItem(w, "x");
            WriteItem(w, "x");
            w.WriteBool(false);
        });
        Assert.Throws<ReplicationFormatException>(() => replicator.Apply(CreateClientWithThreeItems(replicator), newItem));

        byte[] existingItem = Craft(w =>
        {
            w.WriteBool(true);
            w.WriteBool(true);
            w.WriteBool(true);
            WriteItem(w, "b");
            WriteItem(w, "b");
            w.WriteBool(false);
        });
        StringSetHolder client = CreateClientWithThreeItems(replicator);
        Assert.Throws<ReplicationFormatException>(() => replicator.Apply(client, existingItem));

        // После ошибки множество согласовано: перечисление совпадает с поиском.
        foreach (string item in client.Values)
        {
            Assert.True(client.Values.Contains(item), $"lookup misses {item}");
        }
    }

    [Fact]
    public void Malformed_ValidResetIsAccepted()
    {
        Replicator replicator = CreateReplicator();
        StringSetHolder client = CreateClientWithThreeItems(replicator);
        byte[] data = Craft(w =>
        {
            w.WriteBool(true);
            w.WriteBool(true);
            w.WriteBool(true);
            WriteItem(w, "b");
            WriteItem(w, "z");
            w.WriteBool(false);
        });

        replicator.Apply(client, data);
        Assert.Equal(["b", "z"], client.Values.Order());
    }

    [Fact]
    public void Malformed_CountAboveLimitThrows()
    {
        Replicator replicator = CreateReplicator(new ReplicationLimits { MaxCollectionCount = 3 });

        byte[] tooManyInReset = Craft(w =>
        {
            w.WriteBool(true);
            w.WriteBool(true);
            w.WriteBool(true);
            WriteItem(w, "x");
            WriteItem(w, "y");
            WriteItem(w, "z");
            WriteItem(w, "w");
            w.WriteBool(false);
        });
        Assert.Throws<ReplicationFormatException>(
            () => replicator.Apply(CreateClientWithThreeItems(replicator), tooManyInReset));

        // Без сброса: новый элемент сверх лимита.
        StringSetHolder client = CreateClientWithThreeItems(replicator);
        byte[] tooMany = Craft(w =>
        {
            w.WriteBool(true);
            w.WriteBool(true);
            w.WriteBool(false);
            w.WriteBool(false);
            WriteItem(w, "a"); // существующий элемент допустим
            WriteItem(w, "d");
            w.WriteBool(false);
        });
        Assert.Throws<ReplicationFormatException>(() => replicator.Apply(client, tooMany));
        Assert.Equal(3, client.Values.Count);
    }

    [Fact]
    public void Malformed_TruncatedDataThrows()
    {
        Replicator replicator = CreateReplicator();
        var server = new Composite();
        server.Values.Add(2);
        server.Names.Add("abc");
        server.Groups[3] = new ReplicatedSet<int> { 1 };
        server.Rows.Add(new ReplicatedSet<string> { "q" });
        server.Entities.Add(new Entity { Id = 1, Tags = { 7 } });
        byte[] data = Delta(replicator, replicator.CreateBaseline(server));

        for (int length = 0; length < data.Length; length++)
        {
            var client = new Composite();
            byte[] truncated = data.AsSpan(0, length).ToArray();
            Assert.Throws<ReplicationFormatException>(() => replicator.Apply(client, truncated));
        }
    }

    [Fact]
    public void Write_CollectionAboveLimitThrows()
    {
        Replicator replicator = CreateReplicator(new ReplicationLimits { MaxCollectionCount = 3 });
        var server = new IntSetHolder();
        server.Values.UnionWith([1, 2, 3]);
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        Delta(replicator, baseline);

        server.Values.Add(4);
        var ex = Assert.Throws<ReplicationException>(() => replicator.TryWriteDelta(baseline, out _));
        Assert.Contains(nameof(IntSetHolder.Values), ex.Message);

        // После ошибки базовая копия сброшена, и следующая дельта снова полная.
        server.Values.Remove(4);
        DecodedSet recovered = DecodeIntSet(Delta(replicator, baseline));
        Assert.True(recovered.Reset);
    }

    // ---------- allocations ----------

    [Fact]
    public void UnchangedSetDelta_DoesNotAllocate()
    {
        Replicator replicator = CreateReplicator();
        var server = new Composite();
        for (int i = 0; i < 100; i++)
        {
            server.Values.Add(i);
            server.Names.Add("n" + i);
        }

        server.Groups[1] = new ReplicatedSet<int> { 1, 2 };
        server.Rows.Add(new ReplicatedSet<string> { "a" });
        server.Entities.Add(new Entity { Tags = { 1 } });
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var writer = new BitWriter(1 << 16);
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
    public void ChangedSetApply_DoesNotAllocate()
    {
        // Запись нового элемента создает его тень у отправителя, поэтому без аллокаций проверяются
        // применение дельт и запись структурного изменения без итоговой разницы.
        Replicator replicator = CreateReplicator();
        var server = new IntSetHolder();
        for (int i = 0; i < 20; i++)
        {
            server.Values.Add(i);
        }

        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var client = new IntSetHolder();
        replicator.Apply(client, Delta(replicator, baseline));

        // Пара дельт удаляет и возвращает половину элементов, поэтому применение пары не меняет клиента.
        server.Values.RemoveWhere(x => x % 2 == 0);
        byte[] remove = Delta(replicator, baseline);
        server.Values.UnionWith(Enumerable.Range(0, 10).Select(x => x * 2));
        byte[] add = Delta(replicator, baseline);

        void ApplyPair()
        {
            var removeReader = new BitReader(remove);
            replicator.Apply(client, ref removeReader);
            var addReader = new BitReader(add);
            replicator.Apply(client, ref addReader);
        }

        ApplyPair();
        AllocationAssert.DoesNotAllocate(() =>
        {
            for (int i = 0; i < 100; i++)
            {
                ApplyPair();
            }
        });
        AssertSetEqual(server.Values, client.Values);

        var writer = new BitWriter(1 << 16);
        AllocationAssert.DoesNotAllocate(() =>
        {
            for (int i = 0; i < 1000; i++)
            {
                server.Values.Remove(i % 20);
                server.Values.Add(i % 20);
                replicator.TryWriteDelta(baseline, writer);
            }
        });
        Assert.Equal(0, writer.BitPosition);
    }
}
