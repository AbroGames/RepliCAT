using RepliCAT;
using RepliCAT.Bits;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace RepliCAT.Tests;

public class ReplicatedDictionaryTests
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

    public class IntMapHolder
    {
        [Replicated] public ReplicatedDictionary<int, int> Values = new();
    }

    public class StringMapHolder
    {
        [Replicated] public ReplicatedDictionary<string, int> Values = new();
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

    public class ItemMapHolder
    {
        [Replicated] public readonly ReplicatedDictionary<int, Item> Items = new();
    }

    public class ReplaceableHolder
    {
        private ReplicatedDictionary<int, int> _values = new();

        public int SetterCalls;

        [Replicated]
        public ReplicatedDictionary<int, int> Values
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
        [Replicated] public ReplicatedDictionary<int, int> Values { get; } = new();
    }

    public enum Slot
    {
        Head,
        Chest,
        Legs
    }

    public class KeyKindsHolder
    {
        [Replicated] public ReplicatedDictionary<Slot, string> Equipment = new();
        [Replicated] public ReplicatedDictionary<Godot.Vector2I, int> Cells = new();
        [Replicated] public ReplicatedDictionary<long, bool> Flags = new();
        [Replicated] public ReplicatedDictionary<int?, int> NullableKeys = new();
    }

    public class DictOfListsHolder
    {
        [Replicated] public ReplicatedDictionary<string, ReplicatedList<int>> Lists = new();
    }

    public class ListOfDictsHolder
    {
        [Replicated] public ReplicatedList<ReplicatedDictionary<int, Item>> Maps = new();
    }

    public class QuantizedHolder
    {
        [Replicated, Quantize(0, 10, 0.1)] public ReplicatedDictionary<float, float> Values = new();
        [Replicated(Tolerance = 0.5)] public ReplicatedDictionary<int, float> Tolerant = new();
    }

    public class QuantizedNestedHolder
    {
        [Replicated, Quantize(0, 10, 0.1)] public ReplicatedDictionary<int, ReplicatedList<float>> Rows = new();
    }

    public class TreeNode
    {
        [Replicated] public int Value;
        [Replicated] public ReplicatedDictionary<int, TreeNode> Children = new();
    }

    public static class Invalid
    {
        public class ObjectKey
        {
            [Replicated] public ReplicatedDictionary<Item, int> Values = new();
        }

        public class ListKey
        {
            [Replicated] public ReplicatedDictionary<ReplicatedList<int>, int> Values = new();
        }

        public struct NoCodec
        {
            public int X;
        }

        public class StructKeyWithoutCodec
        {
            [Replicated] public ReplicatedDictionary<NoCodec, int> Values = new();
        }

        public class QuantizedObjectValues
        {
            [Replicated, Quantize(1)] public ReplicatedDictionary<int, Item> Values = new();
        }

        public class PlainDictionaryValues
        {
            [Replicated] public ReplicatedDictionary<int, Dictionary<int, int>> Values = new();
        }

        public class ReadonlyValueMember
        {
            [Replicated] public readonly int Value;
        }
    }

    public class Composite
    {
        [Replicated] public int Frame;
        [Replicated] public ReplicatedDictionary<int, int> Values { get; set; } = new();
        [Replicated] public ReplicatedDictionary<int, int> Other { get; set; } = new();
        [Replicated] public ReplicatedDictionary<string, Item> Items { get; set; } = new();
        [Replicated] public ReplicatedDictionary<int, ReplicatedList<int>> Lists { get; set; } = new();
        [Replicated] public ReplicatedList<ReplicatedDictionary<int, string>> Maps { get; set; } = new();
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

    private static void AssertDictionaryEqual<TKey, TValue>(ReplicatedDictionary<TKey, TValue> expected,
        ReplicatedDictionary<TKey, TValue> actual, Action<TValue, TValue> assertValue)
    {
        if (expected == null)
        {
            Assert.Null(actual);
            return;
        }

        Assert.NotNull(actual);
        Assert.Equal(expected.Count, actual.Count);
        foreach (KeyValuePair<TKey, TValue> pair in expected)
        {
            Assert.True(actual.TryGetValue(pair.Key, out TValue value), $"missing key {pair.Key}");
            assertValue(pair.Value, value);
        }

        int enumerated = 0;
        foreach (KeyValuePair<TKey, TValue> pair in actual)
        {
            Assert.True(expected.ContainsKey(pair.Key));
            enumerated++;
        }

        Assert.Equal(expected.Count, enumerated);
    }

    private static void AssertValuesEqual<TKey, TValue>(ReplicatedDictionary<TKey, TValue> expected,
        ReplicatedDictionary<TKey, TValue> actual)
    {
        AssertDictionaryEqual(expected, actual, static (e, a) => Assert.Equal(e, a));
    }

    private static void AssertListValuesEqual(ReplicatedList<int> expected, ReplicatedList<int> actual)
    {
        if (expected == null)
        {
            Assert.Null(actual);
            return;
        }

        Assert.NotNull(actual);
        Assert.Equal(expected, actual);
    }

    private static void AssertEqual(Composite expected, Composite actual)
    {
        Assert.Equal(expected.Frame, actual.Frame);
        AssertValuesEqual(expected.Values, actual.Values);
        AssertValuesEqual(expected.Other, actual.Other);
        AssertDictionaryEqual(expected.Items, actual.Items, AssertItemEqual);
        AssertDictionaryEqual(expected.Lists, actual.Lists, AssertListValuesEqual);
        Assert.Equal(expected.Maps.Count, actual.Maps.Count);
        for (int i = 0; i < expected.Maps.Count; i++)
        {
            AssertValuesEqual(expected.Maps[i], actual.Maps[i]);
        }
    }

    // ---------- wire decoding (IntMapHolder) ----------

    private sealed class DecodedMap
    {
        public bool Present;
        public bool Reset;
        public readonly List<int> Removals = new();
        public readonly List<(int Key, int Value)> Upserts = new();
    }

    /// <summary>
    /// Разбирает полезную нагрузку <see cref="IntMapHolder"/> по формату узла словаря.
    /// </summary>
    private static DecodedMap DecodeIntMap(byte[] data)
    {
        var result = new DecodedMap();
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
            int key = (int)(uint)reader.ReadBits(32);
            int value = (int)(uint)reader.ReadBits(32);
            result.Upserts.Add((key, value));
        }

        Assert.True(reader.RemainingBits < 8);
        return result;
    }

    // ---------- Dictionary<TKey, TValue> parity ----------

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Parity_BehavesLikeDictionary(int seed)
    {
        var random = new Random(seed);
        var dictionary = new ReplicatedDictionary<int, int>();
        var model = new Dictionary<int, int>();

        for (int step = 0; step < 3000; step++)
        {
            int key = random.Next(30);
            int value = random.Next(100);
            switch (random.Next(11))
            {
                case 0:
                case 1:
                    if (model.ContainsKey(key))
                    {
                        Assert.Throws<ArgumentException>(() => dictionary.Add(key, value));
                    }
                    else
                    {
                        dictionary.Add(key, value);
                        model.Add(key, value);
                    }

                    break;
                case 2:
                    Assert.Equal(model.TryAdd(key, value), dictionary.TryAdd(key, value));
                    break;
                case 3:
                case 4:
                    dictionary[key] = value;
                    model[key] = value;
                    break;
                case 5:
                    Assert.Equal(model.Remove(key), dictionary.Remove(key));
                    break;
                case 6:
                {
                    bool expected = model.Remove(key, out int expectedValue);
                    Assert.Equal(expected, dictionary.Remove(key, out int actualValue));
                    Assert.Equal(expectedValue, actualValue);
                    break;
                }
                case 7:
                    if (random.Next(20) == 0)
                    {
                        dictionary.Clear();
                        model.Clear();
                    }

                    break;
                case 8:
                {
                    Assert.Equal(model.ContainsKey(key), dictionary.ContainsKey(key));
                    Assert.Equal(model.ContainsValue(value), dictionary.ContainsValue(value));
                    bool found = model.TryGetValue(key, out int expectedValue);
                    Assert.Equal(found, dictionary.TryGetValue(key, out int actualValue));
                    Assert.Equal(expectedValue, actualValue);
                    if (found)
                    {
                        Assert.Equal(expectedValue, dictionary[key]);
                    }
                    else
                    {
                        Assert.Throws<KeyNotFoundException>(() => dictionary[key]);
                    }

                    break;
                }
                case 9:
                {
                    var collection = (ICollection<KeyValuePair<int, int>>)dictionary;
                    var modelCollection = (ICollection<KeyValuePair<int, int>>)model;
                    var pair = new KeyValuePair<int, int>(key, value);
                    Assert.Equal(modelCollection.Contains(pair), collection.Contains(pair));
                    Assert.Equal(modelCollection.Remove(pair), collection.Remove(pair));
                    break;
                }
                case 10:
                {
                    var expected = new KeyValuePair<int, int>[model.Count + 1];
                    var actual = new KeyValuePair<int, int>[model.Count + 1];
                    ((ICollection<KeyValuePair<int, int>>)model).CopyTo(expected, 1);
                    ((ICollection<KeyValuePair<int, int>>)dictionary).CopyTo(actual, 1);
                    Assert.Equal(expected.Skip(1).OrderBy(p => p.Key), actual.Skip(1).OrderBy(p => p.Key));
                    break;
                }
            }

            Assert.Equal(model.Count, dictionary.Count);
            Assert.Equal(model.OrderBy(p => p.Key), dictionary.OrderBy(p => p.Key));
            Assert.Equal(model.Keys.Order(), dictionary.Keys.Order());
            Assert.Equal(model.Values.Order(), dictionary.Values.Order());
        }
    }

    [Fact]
    public void DictionaryApi_EdgeCases()
    {
        var dictionary = new ReplicatedDictionary<string, int>([new("a", 1), new("b", 2)]);
        Assert.Equal(2, dictionary.Count);
        Assert.Equal(2, dictionary["b"]);

        Assert.Throws<ArgumentNullException>(() => dictionary.Add(null, 1));
        Assert.Throws<ArgumentNullException>(() => dictionary[null] = 1);
        Assert.Throws<ArgumentNullException>(() => dictionary.ContainsKey(null));
        Assert.Throws<ArgumentNullException>(() => new ReplicatedDictionary<int, int>((IEnumerable<KeyValuePair<int, int>>)null));
        Assert.Throws<ArgumentException>(() => new ReplicatedDictionary<int, int>([new(1, 1), new(1, 2)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReplicatedDictionary<int, int>(-1));
        Assert.False(((ICollection<KeyValuePair<string, int>>)dictionary).IsReadOnly);

        IDictionary<string, int> asDictionary = dictionary;
        asDictionary.Add(new KeyValuePair<string, int>("c", 3));
        Assert.Equal(["a", "b", "c"], asDictionary.Keys.Order());
        Assert.Equal([1, 2, 3], asDictionary.Values.Order());

        IReadOnlyDictionary<string, int> readOnly = dictionary;
        Assert.Equal(["a", "b", "c"], readOnly.Keys.Order());
        Assert.Equal([1, 2, 3], readOnly.Values.Order());

        var boxed = new List<KeyValuePair<string, int>>();
        foreach (KeyValuePair<string, int> pair in (IEnumerable<KeyValuePair<string, int>>)dictionary)
        {
            boxed.Add(pair);
        }

        Assert.Equal(3, boxed.Count);
    }

    [Fact]
    public void Enumerator_ThrowsWhenModified()
    {
        var dictionary = new ReplicatedDictionary<int, int> { [1] = 1, [2] = 2 };

        Assert.Throws<InvalidOperationException>(() =>
        {
            foreach (KeyValuePair<int, int> pair in dictionary)
            {
                dictionary.Add(pair.Key + 10, 0);
            }
        });
    }

    // ---------- value values ----------

    [Fact]
    public void ValueValues_AllOperationsReplicate()
    {
        Replicator replicator = CreateReplicator();
        var server = new IntMapHolder();
        server.Values.Add(1, 10);
        server.Values.Add(2, 20);
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var client = new IntMapHolder();
        ReplicatedDictionary<int, int> clientMap = client.Values;

        void Frame(Action<ReplicatedDictionary<int, int>> mutate)
        {
            mutate(server.Values);
            replicator.Apply(client, Delta(replicator, baseline));
            Assert.Same(clientMap, client.Values);
            AssertValuesEqual(server.Values, client.Values);
        }

        Frame(_ => { });
        Frame(d => d.Add(3, 30));
        Frame(d => d[1] = 11);
        Frame(d => d.Remove(2));
        Frame(d =>
        {
            d.Remove(1);
            d[1] = 12;
            d.TryAdd(4, 40);
            d[3] = 31;
        });
        Frame(d => d.Clear());
        Frame(d =>
        {
            d[5] = 50;
            d[6] = 60;
        });
        Frame(d =>
        {
            d.Clear();
            d[5] = 51;
        });
    }

    [Fact]
    public void Wire_OnlyChangedEntriesAreSent()
    {
        Replicator replicator = CreateReplicator();
        var server = new IntMapHolder();
        for (int i = 0; i < 10; i++)
        {
            server.Values.Add(i, i * 10);
        }

        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        DecodedMap first = DecodeIntMap(Delta(replicator, baseline));
        Assert.True(first.Reset);
        Assert.Equal(10, first.Upserts.Count);

        server.Values[3] = 33;
        DecodedMap set = DecodeIntMap(Delta(replicator, baseline));
        Assert.False(set.Reset);
        Assert.Empty(set.Removals);
        Assert.Equal([(3, 33)], set.Upserts);

        server.Values.Remove(4);
        server.Values.Add(100, 1);
        DecodedMap removeAdd = DecodeIntMap(Delta(replicator, baseline));
        Assert.False(removeAdd.Reset);
        Assert.Equal([4], removeAdd.Removals);
        Assert.Equal([(100, 1)], removeAdd.Upserts);

        // Удалить и вернуть то же значение — изменений нет.
        server.Values.Remove(5);
        server.Values.Add(5, 50);
        Assert.False(replicator.TryWriteDelta(baseline, out _));

        // Удалить и вернуть другое значение — только запись, без удаления.
        server.Values.Remove(6);
        server.Values.Add(6, 61);
        DecodedMap readd = DecodeIntMap(Delta(replicator, baseline));
        Assert.Empty(readd.Removals);
        Assert.Equal([(6, 61)], readd.Upserts);
    }

    [Fact]
    public void NoChanges_ReturnsFalse()
    {
        Replicator replicator = CreateReplicator();
        var server = new IntMapHolder();
        server.Values.Add(1, 1);
        server.Values.Add(2, 2);
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        Delta(replicator, baseline);

        var writer = new BitWriter();
        Assert.False(replicator.TryWriteDelta(baseline, writer));
        Assert.Equal(0, writer.BitPosition);

        // То же значение.
        server.Values[1] = 1;
        Assert.False(replicator.TryWriteDelta(baseline, writer));

        // Добавить и тут же удалить.
        server.Values.Add(3, 3);
        server.Values.Remove(3);
        Assert.False(replicator.TryWriteDelta(baseline, writer));
        Assert.Equal(0, writer.BitPosition);

        // Значения-объекты без изменений.
        var itemServer = new ItemMapHolder();
        itemServer.Items.Add(1, new Item { A = 1 });
        ReplicationBaseline itemBaseline = replicator.CreateBaseline(itemServer);
        Delta(replicator, itemBaseline);
        Assert.False(replicator.TryWriteDelta(itemBaseline, writer));
        Assert.Equal(0, writer.BitPosition);
    }

    [Fact]
    public void KeyKinds_Replicate()
    {
        Replicator replicator = CreateReplicator();
        var server = new KeyKindsHolder();
        server.Equipment[Slot.Head] = "helmet";
        server.Equipment[Slot.Legs] = null;
        server.Cells[new Godot.Vector2I(-3, 7)] = 5;
        server.Flags[long.MinValue] = true;
        server.NullableKeys[0] = 1;
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var client = new KeyKindsHolder();
        replicator.Apply(client, Delta(replicator, baseline));

        AssertValuesEqual(server.Equipment, client.Equipment);
        AssertValuesEqual(server.Cells, client.Cells);
        AssertValuesEqual(server.Flags, client.Flags);
        AssertValuesEqual(server.NullableKeys, client.NullableKeys);

        server.Equipment.Remove(Slot.Head);
        server.Equipment[Slot.Chest] = "armor";
        server.Cells[new Godot.Vector2I(-3, 7)] = 6;
        replicator.Apply(client, Delta(replicator, baseline));
        AssertValuesEqual(server.Equipment, client.Equipment);
        AssertValuesEqual(server.Cells, client.Cells);
    }

    [Fact]
    public void StringKeys_Replicate()
    {
        Replicator replicator = CreateReplicator();
        var server = new StringMapHolder();
        server.Values["a"] = 1;
        server.Values[""] = 2;
        server.Values["длинный ключ"] = 3;
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var client = new StringMapHolder();
        replicator.Apply(client, Delta(replicator, baseline));
        AssertValuesEqual(server.Values, client.Values);

        server.Values.Remove("");
        server.Values["b"] = 4;
        replicator.Apply(client, Delta(replicator, baseline));
        AssertValuesEqual(server.Values, client.Values);
    }

    // ---------- instances ----------

    [Fact]
    public void GetOnlyDictionaryProperty_ReusesInstance()
    {
        Replicator replicator = CreateReplicator();
        var server = new GetOnlyHolder();
        server.Values[1] = 2;
        var client = new GetOnlyHolder();
        ReplicatedDictionary<int, int> clientMap = client.Values;
        replicator.Apply(client, Delta(replicator, replicator.CreateBaseline(server)));
        Assert.Same(clientMap, client.Values);
        AssertValuesEqual(server.Values, client.Values);
    }

    [Fact]
    public void ReplacingDictionaryInstance_ResetsAndClientKeepsInstance()
    {
        Replicator replicator = CreateReplicator();
        var server = new ReplaceableHolder();
        server.Values[1] = 1;
        server.Values[2] = 2;
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var client = new ReplaceableHolder();
        ReplicatedDictionary<int, int> clientMap = client.Values;
        replicator.Apply(client, Delta(replicator, baseline));
        Assert.Equal(0, client.SetterCalls);

        server.Values = new ReplicatedDictionary<int, int> { [2] = 20, [3] = 3 };
        byte[] data = Delta(replicator, baseline);
        replicator.Apply(client, data);
        Assert.Same(clientMap, client.Values);
        Assert.Equal(0, client.SetterCalls);
        AssertValuesEqual(server.Values, client.Values);

        var decoded = new BitReader(data);
        decoded.ReadBits(1);
        Assert.True(decoded.ReadBool()); // present
        Assert.True(decoded.ReadBool()); // reset

        // Та же ссылка после сброса — обычная дельта.
        server.Values[3] = 30;
        replicator.Apply(client, Delta(replicator, baseline));
        AssertValuesEqual(server.Values, client.Values);
    }

    [Fact]
    public void NullDictionary_TransitionsBothWays()
    {
        Replicator replicator = CreateReplicator();
        var server = new ReplaceableHolder();
        server.Values[1] = 1;
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

        server.Values = new ReplicatedDictionary<int, int> { [4] = 5 };
        replicator.Apply(client, Delta(replicator, baseline));
        replicator.Apply(late, Snapshot(replicator, baseline));
        AssertValuesEqual(server.Values, client.Values);
        AssertValuesEqual(server.Values, late.Values);
    }

    // ---------- object values ----------

    [Fact]
    public void ObjectValues_ElementDeltasApplyIntoSameInstances()
    {
        Replicator replicator = CreateReplicator();
        var server = new ItemMapHolder();
        server.Items[1] = new Item { A = 1, Name = "one" };
        server.Items[2] = new SpecialItem { A = 2, Name = "two", Power = 2.5f };
        server.Items[3] = null;
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var client = new ItemMapHolder();

        byte[] full = Delta(replicator, baseline);
        replicator.Apply(client, full);
        AssertDictionaryEqual(server.Items, client.Items, AssertItemEqual);
        Item clientFirst = client.Items[1];
        Item clientSecond = client.Items[2];
        Assert.IsType<SpecialItem>(clientSecond);

        // Изменение члена значения — дельта значения в тот же экземпляр.
        server.Items[2].A = 20;
        byte[] elementDelta = Delta(replicator, baseline);
        Assert.True(elementDelta.Length < full.Length);
        replicator.Apply(client, elementDelta);
        AssertDictionaryEqual(server.Items, client.Items, AssertItemEqual);
        Assert.Same(clientFirst, client.Items[1]);
        Assert.Same(clientSecond, client.Items[2]);

        // Новое значение передается целиком.
        server.Items[4] = new Item { A = 4, Name = "four" };
        replicator.Apply(client, Delta(replicator, baseline));
        AssertDictionaryEqual(server.Items, client.Items, AssertItemEqual);
        Assert.Same(clientFirst, client.Items[1]);

        // Удаленное значение удаляется.
        server.Items.Remove(1);
        replicator.Apply(client, Delta(replicator, baseline));
        AssertDictionaryEqual(server.Items, client.Items, AssertItemEqual);
        Assert.False(client.Items.ContainsKey(1));
        Assert.Same(clientSecond, client.Items[2]);

        // null ↔ объект.
        server.Items[3] = new Item { A = 3 };
        server.Items[4] = null;
        replicator.Apply(client, Delta(replicator, baseline));
        AssertDictionaryEqual(server.Items, client.Items, AssertItemEqual);

        // Замена значения новым экземпляром того же типа: клиент переиспользует свой экземпляр.
        Item clientThird = client.Items[3];
        server.Items[3] = new Item { A = 33, Name = "x" };
        replicator.Apply(client, Delta(replicator, baseline));
        AssertDictionaryEqual(server.Items, client.Items, AssertItemEqual);
        Assert.Same(clientThird, client.Items[3]);

        // Замена подтипом: клиент создает экземпляр подтипа.
        server.Items[3] = new SpecialItem { A = 34, Power = 1 };
        replicator.Apply(client, Delta(replicator, baseline));
        AssertDictionaryEqual(server.Items, client.Items, AssertItemEqual);
        Assert.NotSame(clientThird, client.Items[3]);

        // Clear.
        server.Items.Clear();
        replicator.Apply(client, Delta(replicator, baseline));
        Assert.Empty(client.Items);
        Assert.False(replicator.TryWriteDelta(baseline, out _));
    }

    // ---------- nesting ----------

    [Fact]
    public void DictionaryOfLists_Replicates()
    {
        Replicator replicator = CreateReplicator();
        var server = new DictOfListsHolder();
        server.Lists["a"] = new ReplicatedList<int> { 1, 2 };
        server.Lists["b"] = new ReplicatedList<int>();
        server.Lists["c"] = null;
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var client = new DictOfListsHolder();

        void Frame(Action mutate)
        {
            mutate();
            replicator.Apply(client, Delta(replicator, baseline));
            AssertDictionaryEqual(server.Lists, client.Lists, AssertListValuesEqual);
        }

        Frame(() => { });
        ReplicatedList<int> clientA = client.Lists["a"];

        Frame(() => server.Lists["a"].Add(3));
        Assert.Same(clientA, client.Lists["a"]);

        Frame(() => server.Lists["b"].AddRange([7, 8, 9]));
        Frame(() => server.Lists["c"] = new ReplicatedList<int> { 5 });
        Frame(() => server.Lists["a"] = new ReplicatedList<int> { 42 });
        Assert.Same(clientA, client.Lists["a"]);
        Frame(() =>
        {
            server.Lists.Remove("b");
            server.Lists["a"].Insert(0, 0);
            server.Lists["d"] = new ReplicatedList<int> { 1 };
        });

        Assert.False(replicator.TryWriteDelta(baseline, out _));
    }

    [Fact]
    public void ListOfDictionaries_Replicates()
    {
        Replicator replicator = CreateReplicator();
        var server = new ListOfDictsHolder();
        server.Maps.Add(new ReplicatedDictionary<int, Item> { [1] = new Item { A = 1 } });
        server.Maps.Add(null);
        server.Maps.Add(new ReplicatedDictionary<int, Item>());
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var client = new ListOfDictsHolder();

        void Frame(Action mutate)
        {
            mutate();
            replicator.Apply(client, Delta(replicator, baseline));
            Assert.Equal(server.Maps.Count, client.Maps.Count);
            for (int i = 0; i < server.Maps.Count; i++)
            {
                AssertDictionaryEqual(server.Maps[i], client.Maps[i], AssertItemEqual);
            }
        }

        Frame(() => { });
        ReplicatedDictionary<int, Item> clientFirst = client.Maps[0];
        Item clientItem = clientFirst[1];

        Frame(() => server.Maps[0][1].A = 10);
        Assert.Same(clientFirst, client.Maps[0]);
        Assert.Same(clientItem, client.Maps[0][1]);

        Frame(() => server.Maps[2][5] = new SpecialItem { Power = 3 });
        Frame(() => server.Maps[1] = new ReplicatedDictionary<int, Item> { [7] = null });
        Frame(() => server.Maps.Insert(0, new ReplicatedDictionary<int, Item>()));
        Frame(() =>
        {
            server.Maps.RemoveAt(2);
            server.Maps[0][9] = new Item { Name = "nine" };
        });

        Assert.False(replicator.TryWriteDelta(baseline, out _));
    }

    [Fact]
    public void RecursiveTypeWithDictionaryOfChildren()
    {
        Replicator replicator = CreateReplicator();
        var server = new TreeNode { Value = 1 };
        server.Children[5] = new TreeNode { Value = 2 };
        server.Children[5].Children[6] = new TreeNode { Value = 3 };
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var client = new TreeNode();
        replicator.Apply(client, Delta(replicator, baseline));

        server.Children[5].Children[6].Value = 30;
        server.Children[7] = new TreeNode { Value = 4 };
        replicator.Apply(client, Delta(replicator, baseline));

        Assert.Equal(1, client.Value);
        Assert.Equal(2, client.Children.Count);
        Assert.Equal(30, client.Children[5].Children[6].Value);
        Assert.Equal(4, client.Children[7].Value);
    }

    [Fact]
    public void Depth_DictionariesCountAsLevels()
    {
        var server = new ItemMapHolder();
        server.Items[1] = new Item();

        // Словарь (1 уровень) + значение-объект (2 уровень).
        Replicator ok = CreateReplicator(new ReplicationLimits { MaxDepth = 2 });
        var client = new ItemMapHolder();
        ok.Apply(client, Delta(ok, ok.CreateBaseline(server)));
        Assert.Single(client.Items);

        Replicator tooShallow = CreateReplicator(new ReplicationLimits { MaxDepth = 1 });
        Assert.Throws<ReplicationException>(() => tooShallow.TryWriteDelta(tooShallow.CreateBaseline(server), out _));

        // Чтение тоже ограничено.
        byte[] data = Delta(ok, ok.CreateBaseline(server));
        Assert.Throws<ReplicationFormatException>(() => tooShallow.Apply(new ItemMapHolder(), data));

        // Словарь списков: словарь + список — два уровня.
        var lists = new DictOfListsHolder();
        lists.Lists["a"] = new ReplicatedList<int> { 1 };
        Assert.Throws<ReplicationException>(() => tooShallow.TryWriteDelta(tooShallow.CreateBaseline(lists), out _));
        Assert.True(ok.TryWriteDelta(ok.CreateBaseline(lists), out _));
    }

    // ---------- quantization and tolerance ----------

    [Fact]
    public void QuantizeAndTolerance_ApplyToValuesNotKeys()
    {
        Replicator replicator = CreateReplicator(out CollectingSink sink);
        var server = new QuantizedHolder();
        server.Values[123.456f] = 1.23f;
        server.Values[-7.5f] = 4.56f;
        server.Tolerant[1] = 1f;
        server.Tolerant[2] = 2f;
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var client = new QuantizedHolder();
        replicator.Apply(client, Delta(replicator, baseline));

        // Ключи не квантуются (и вне диапазона Quantize не дают предупреждений), значения — квантуются.
        Assert.Equal(1.2f, client.Values[123.456f], 0.051f);
        Assert.Equal(4.6f, client.Values[-7.5f], 0.051f);
        Assert.Equal(0, sink.Count);

        // Изменение меньше допуска не отправляется, даже при структурном изменении.
        server.Tolerant[1] = 1.3f;
        server.Tolerant[3] = 3f;
        replicator.Apply(client, Delta(replicator, baseline));
        Assert.Equal(1f, client.Tolerant[1]);
        Assert.Equal(3f, client.Tolerant[3]);

        server.Tolerant[1] = 1.6f;
        replicator.Apply(client, Delta(replicator, baseline));
        Assert.Equal(1.6f, client.Tolerant[1]);

        // Выход за диапазон у многих значений — одно предупреждение на член.
        for (int i = 0; i < 10; i++)
        {
            server.Values[i] = 100 + i;
        }

        replicator.Apply(client, Delta(replicator, baseline));
        var late = new QuantizedHolder();
        replicator.Apply(late, Snapshot(replicator, baseline));
        Assert.Equal(1, sink.Count);
        Assert.Equal(10f, client.Values[5f]);
        Assert.Equal(10f, late.Values[9f]);
        Assert.Equal(1.2f, late.Values[123.456f], 0.051f);
    }

    [Fact]
    public void Quantize_PropagatesThroughNestedCollections()
    {
        Replicator replicator = CreateReplicator();
        var server = new QuantizedNestedHolder();
        server.Rows[1] = new ReplicatedList<float> { 3.33f };
        var client = new QuantizedNestedHolder();
        replicator.Apply(client, Delta(replicator, replicator.CreateBaseline(server)));
        Assert.Equal(3.3f, client.Rows[1][0], 0.051f);
        Assert.NotEqual(3.33f, client.Rows[1][0]);
    }

    // ---------- model errors and schema ----------

    [Fact]
    public void ModelErrors_ThrowWithMemberPath()
    {
        Replicator replicator = CreateReplicator();

        var objectKey = Assert.Throws<ReplicationException>(() => replicator.CreateBaseline(new Invalid.ObjectKey()));
        Assert.Contains($"{typeof(Invalid.ObjectKey).FullName}.{nameof(Invalid.ObjectKey.Values)}", objectKey.Message);
        Assert.Contains("key", objectKey.Message);

        var listKey = Assert.Throws<ReplicationException>(() => replicator.CreateBaseline(new Invalid.ListKey()));
        Assert.Contains(nameof(Invalid.ListKey.Values), listKey.Message);

        var structKey = Assert.Throws<ReplicationException>(
            () => replicator.CreateBaseline(new Invalid.StructKeyWithoutCodec()));
        Assert.Contains(nameof(Invalid.StructKeyWithoutCodec.Values), structKey.Message);

        var quantizedObjects = Assert.Throws<ReplicationException>(
            () => replicator.CreateBaseline(new Invalid.QuantizedObjectValues()));
        Assert.Contains(nameof(Invalid.QuantizedObjectValues.Values), quantizedObjects.Message);

        var plainValues = Assert.Throws<ReplicationException>(
            () => replicator.CreateBaseline(new Invalid.PlainDictionaryValues()));
        Assert.Contains("ReplicatedDictionary", plainValues.Message);

        Assert.Throws<ReplicationException>(() => replicator.CreateBaseline(new ReplicatedDictionary<int, int>()));
    }

    [Fact]
    public void KeyCodecRegisteredLater_IsUsedByNewModels()
    {
        // Ключ-структура с зарегистрированным кодеком допустим.
        Replicator replicator = CreateReplicator();
        replicator.Codecs.Register(new NoCodecCodec());
        var server = new Invalid.StructKeyWithoutCodec();
        server.Values[new Invalid.NoCodec { X = 5 }] = 7;
        var client = new Invalid.StructKeyWithoutCodec();
        replicator.Apply(client, Delta(replicator, replicator.CreateBaseline(server)));
        Assert.Equal(7, client.Values[new Invalid.NoCodec { X = 5 }]);
    }

    private sealed class NoCodecCodec : RepliCAT.Codecs.IReplicationCodec<Invalid.NoCodec>
    {
        public void Write(BitWriter writer, Invalid.NoCodec value)
        {
            writer.WriteVarInt(value.X);
        }

        public Invalid.NoCodec Read(ref BitReader reader)
        {
            return new Invalid.NoCodec { X = (int)reader.ReadVarInt() };
        }

        public bool IsChanged(Invalid.NoCodec lastSent, Invalid.NoCodec current)
        {
            return lastSent.X != current.X;
        }
    }

    [Fact]
    public void SchemaHash_DescribesKeysAndValues()
    {
        Replicator first = CreateReplicator();
        Replicator second = CreateReplicator();
        Assert.Equal(first.GetSchemaHash(typeof(Composite)), second.GetSchemaHash(typeof(Composite)));

        ulong intInt = first.GetSchemaHash(typeof(IntMapHolder));
        Assert.NotEqual(intInt, first.GetSchemaHash(typeof(SchemaVariants.LongKey)));
        Assert.NotEqual(intInt, first.GetSchemaHash(typeof(SchemaVariants.FloatValue)));
        Assert.NotEqual(intInt, first.GetSchemaHash(typeof(SchemaVariants.ListOfInts)));
        Assert.NotEqual(
            first.GetSchemaHash(typeof(SchemaVariants.FloatValue)),
            first.GetSchemaHash(typeof(SchemaVariants.QuantizedFloatValue)));
    }

    public static class SchemaVariants
    {
        public class LongKey
        {
            [Replicated] public ReplicatedDictionary<long, int> Values = new();
        }

        public class FloatValue
        {
            [Replicated] public ReplicatedDictionary<int, float> Values = new();
        }

        public class QuantizedFloatValue
        {
            [Replicated, Quantize(0.1)] public ReplicatedDictionary<int, float> Values = new();
        }

        public class ListOfInts
        {
            [Replicated] public ReplicatedList<int> Values = new();
        }
    }

    // ---------- snapshots ----------

    [Fact]
    public void MidFrameSnapshotPlusDelta_MatchesServer()
    {
        Replicator replicator = CreateReplicator();
        var server = new Composite();
        server.Values[1] = 1;
        server.Values[2] = 2;
        server.Items["a"] = new Item { A = 1 };
        server.Items["b"] = new SpecialItem { A = 2, Power = 1 };
        server.Lists[1] = new ReplicatedList<int> { 5 };
        server.Maps.Add(new ReplicatedDictionary<int, string> { [1] = "x" });
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var client = new Composite();
        replicator.Apply(client, Delta(replicator, baseline));

        // Кадр: часть изменений до снимка, часть после; значение меняется и возвращается обратно.
        server.Values[1] = 100;
        server.Items["a"].A = 50;
        server.Values[3] = 3;
        server.Maps[0].Remove(1);
        byte[] snapshot = Snapshot(replicator, baseline);
        server.Values[1] = 1;
        server.Items["a"].A = 1;
        server.Items.Remove("b");
        server.Lists[1].Add(6);
        server.Maps[0][1] = "x";
        server.Maps[0][2] = "y";
        byte[] delta = Delta(replicator, baseline);

        var late = new Composite();
        late.Values[9] = 9; // мусор, который сброс должен убрать
        late.Values[1] = 77;
        late.Items["z"] = new Item { A = 77 };
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

    private static void MutateValues(ReplicatedDictionary<int, int> dictionary, Random random)
    {
        int key = random.Next(40);
        switch (random.Next(8))
        {
            case 0:
            case 1:
            case 2:
                dictionary[key] = random.Next(1000);
                break;
            case 3:
                dictionary.TryAdd(key, random.Next(1000));
                break;
            case 4:
            case 5:
                dictionary.Remove(key);
                break;
            case 6:
                if (random.Next(10) == 0)
                {
                    dictionary.Clear();
                }

                break;
            case 7:
                // Удалить и вернуть (то же или другое значение).
                if (dictionary.Remove(key, out int value))
                {
                    dictionary[key] = random.Next(2) == 0 ? value : value + 1;
                }

                break;
        }
    }

    private static void MutateItems(ReplicatedDictionary<string, Item> dictionary, Random random)
    {
        string key = "k" + random.Next(25);
        switch (random.Next(10))
        {
            case 0:
            case 1:
                dictionary[key] = RandomItem(random);
                break;
            case 2:
                dictionary.Remove(key);
                break;
            case 3:
            case 4:
            case 5:
            case 6:
                if (dictionary.TryGetValue(key, out Item item) && item != null)
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
                if (random.Next(8) == 0)
                {
                    dictionary.Clear();
                }

                break;
            case 8:
                // Один и тот же экземпляр под двумя ключами.
                if (dictionary.TryGetValue(key, out Item shared))
                {
                    dictionary["k" + random.Next(25)] = shared;
                }

                break;
            case 9:
                // Удалить и вернуть тот же экземпляр в том же кадре.
                if (dictionary.Remove(key, out Item removed))
                {
                    dictionary[key] = removed;
                }

                break;
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
                    MutateValues(server.Values, random);
                }

                break;
            case 3:
                if (server.Other != null)
                {
                    MutateValues(server.Other, random);
                }

                break;
            case 4:
            case 5:
            case 6:
            case 7:
                MutateItems(server.Items, random);
                break;
            case 8:
            case 9:
            {
                // Словарь списков.
                ReplicatedDictionary<int, ReplicatedList<int>> lists = server.Lists;
                int key = random.Next(6);
                int action = random.Next(6);
                if (action == 0)
                {
                    lists[key] = random.Next(5) == 0 ? null : new ReplicatedList<int>();
                }
                else if (action == 1)
                {
                    lists.Remove(key);
                }
                else if (lists.TryGetValue(key, out ReplicatedList<int> list) && list != null)
                {
                    if (list.Count > 0 && random.Next(3) == 0)
                    {
                        list.RemoveAt(random.Next(list.Count));
                    }
                    else if (random.Next(4) == 0)
                    {
                        list.Insert(random.Next(list.Count + 1), random.Next(100));
                    }
                    else
                    {
                        list.Add(random.Next(100));
                    }
                }

                break;
            }
            case 10:
            case 11:
            {
                // Список словарей.
                ReplicatedList<ReplicatedDictionary<int, string>> maps = server.Maps;
                int count = maps.Count;
                int action = random.Next(6);
                if (action == 0 && count < 6)
                {
                    maps.Insert(random.Next(count + 1), random.Next(5) == 0 ? null : new ReplicatedDictionary<int, string>());
                }
                else if (action == 1 && count > 0)
                {
                    maps.RemoveAt(random.Next(count));
                }
                else if (count > 0 && maps[random.Next(count)] is { } map)
                {
                    int key = random.Next(8);
                    if (random.Next(3) == 0)
                    {
                        map.Remove(key);
                    }
                    else
                    {
                        map[key] = random.Next(4) == 0 ? null : "v" + random.Next(10);
                    }
                }

                break;
            }
            case 12:
                // Редкая замена экземпляров, null, обмен и совместное использование словарей.
                switch (random.Next(10))
                {
                    case 0:
                        server.Values = server.Values == null ? new ReplicatedDictionary<int, int> { [1] = 2 } : null;
                        break;
                    case 1:
                        server.Values = new ReplicatedDictionary<int, int>(server.Values ?? new ReplicatedDictionary<int, int>());
                        break;
                    case 2:
                        (server.Values, server.Other) = (server.Other, server.Values);
                        break;
                    case 3:
                        server.Other = server.Values;
                        break;
                    case 4:
                        server.Other = new ReplicatedDictionary<int, int>();
                        break;
                    case 5:
                    {
                        var copy = new ReplicatedDictionary<string, Item>(server.Items);
                        copy.Remove("k" + random.Next(25));
                        server.Items = copy;
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
            late.Values[1] = -1;
            late.Values[1000] = -2;
            late.Other = null;
            late.Items["k1"] = new SpecialItem { A = -1 };
            late.Items["junk"] = new Item();
            late.Lists[2] = new ReplicatedList<int> { -1 };
            late.Maps.Add(new ReplicatedDictionary<int, string> { [1] = "junk" });
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
        ReplicatedDictionary<string, Item> clientItems = client.Items;
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
            Assert.Same(clientItems, client.Items);
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
    /// Клиент со словарем { "a": 1, "b": 2, "c": 3 }.
    /// </summary>
    private static StringMapHolder CreateClientWithThreeEntries(Replicator replicator)
    {
        var server = new StringMapHolder();
        server.Values["a"] = 1;
        server.Values["b"] = 2;
        server.Values["c"] = 3;
        var client = new StringMapHolder();
        replicator.Apply(client, Delta(replicator, replicator.CreateBaseline(server)));
        return client;
    }

    private static void WriteEntry(BitWriter writer, string key, int value)
    {
        writer.WriteBool(true);
        RepliCAT.Codecs.StringCodec.Default.Write(writer, key);
        writer.WriteBits((uint)value, 32);
    }

    [Fact]
    public void Malformed_NullKeyThrows()
    {
        Replicator replicator = CreateReplicator();

        byte[] upsert = Craft(w =>
        {
            w.WriteBool(true); // маска
            w.WriteBool(true); // present
            w.WriteBool(false); // reset
            w.WriteBool(false); // конец удалений
            WriteEntry(w, null, 1);
            w.WriteBool(false);
        });
        Assert.Throws<ReplicationFormatException>(() => replicator.Apply(CreateClientWithThreeEntries(replicator), upsert));

        byte[] removal = Craft(w =>
        {
            w.WriteBool(true);
            w.WriteBool(true);
            w.WriteBool(false);
            w.WriteBool(true);
            RepliCAT.Codecs.StringCodec.Default.Write(w, null);
            w.WriteBool(false);
            w.WriteBool(false);
        });
        Assert.Throws<ReplicationFormatException>(() => replicator.Apply(CreateClientWithThreeEntries(replicator), removal));

        byte[] reset = Craft(w =>
        {
            w.WriteBool(true);
            w.WriteBool(true);
            w.WriteBool(true);
            WriteEntry(w, null, 1);
            w.WriteBool(false);
        });
        Assert.Throws<ReplicationFormatException>(() => replicator.Apply(CreateClientWithThreeEntries(replicator), reset));

        // Ключ Nullable<int> без значения — тоже ошибка формата.
        byte[] nullableKey = Craft(w =>
        {
            w.WriteBits(0b1000, 4); // маска: только NullableKeys (члены по имени: Cells, Equipment, Flags, NullableKeys)
            w.WriteBool(true);
            w.WriteBool(true);
            w.WriteBool(true);
            w.WriteBool(false); // hasValue = 0
            w.WriteBits(1, 32);
            w.WriteBool(false);
        });
        Assert.Throws<ReplicationFormatException>(() => replicator.Apply(new KeyKindsHolder(), nullableKey));
    }

    [Fact]
    public void Malformed_DuplicateKeyInResetThrows()
    {
        Replicator replicator = CreateReplicator();

        // Новый ключ дважды.
        byte[] newKey = Craft(w =>
        {
            w.WriteBool(true);
            w.WriteBool(true);
            w.WriteBool(true);
            WriteEntry(w, "x", 1);
            WriteEntry(w, "x", 2);
            w.WriteBool(false);
        });
        StringMapHolder first = CreateClientWithThreeEntries(replicator);
        Assert.Throws<ReplicationFormatException>(() => replicator.Apply(first, newKey));

        // Прежний ключ дважды.
        byte[] existingKey = Craft(w =>
        {
            w.WriteBool(true);
            w.WriteBool(true);
            w.WriteBool(true);
            WriteEntry(w, "b", 1);
            WriteEntry(w, "b", 2);
            w.WriteBool(false);
        });
        StringMapHolder second = CreateClientWithThreeEntries(replicator);
        Assert.Throws<ReplicationFormatException>(() => replicator.Apply(second, existingKey));

        // После ошибки словарь согласован: перечисление совпадает с поиском.
        foreach (KeyValuePair<string, int> pair in second.Values)
        {
            Assert.Equal(pair.Value, second.Values[pair.Key]);
        }
    }

    [Fact]
    public void Malformed_ValidResetIsAccepted()
    {
        Replicator replicator = CreateReplicator();
        StringMapHolder client = CreateClientWithThreeEntries(replicator);
        byte[] data = Craft(w =>
        {
            w.WriteBool(true);
            w.WriteBool(true);
            w.WriteBool(true);
            WriteEntry(w, "b", 7);
            WriteEntry(w, "z", 8);
            w.WriteBool(false);
        });

        replicator.Apply(client, data);
        Assert.Equal(2, client.Values.Count);
        Assert.Equal(7, client.Values["b"]);
        Assert.Equal(8, client.Values["z"]);
    }

    [Fact]
    public void Malformed_DuplicateKeyOutsideResetIsAppliedTwice()
    {
        // Та же политика, что у списка: вне сброса повтор применяется как дельта к той же записи.
        Replicator replicator = CreateReplicator();
        StringMapHolder client = CreateClientWithThreeEntries(replicator);
        byte[] data = Craft(w =>
        {
            w.WriteBool(true);
            w.WriteBool(true);
            w.WriteBool(false);
            w.WriteBool(false);
            WriteEntry(w, "x", 1);
            WriteEntry(w, "x", 2);
            w.WriteBool(false);
        });

        replicator.Apply(client, data);
        Assert.Equal(4, client.Values.Count);
        Assert.Equal(2, client.Values["x"]);
    }

    [Fact]
    public void Malformed_RemovalOfMissingKeyIsNoOp()
    {
        Replicator replicator = CreateReplicator();
        StringMapHolder client = CreateClientWithThreeEntries(replicator);
        byte[] data = Craft(w =>
        {
            w.WriteBool(true);
            w.WriteBool(true);
            w.WriteBool(false);
            w.WriteBool(true);
            RepliCAT.Codecs.StringCodec.Default.Write(w, "missing");
            w.WriteBool(true);
            RepliCAT.Codecs.StringCodec.Default.Write(w, "b");
            w.WriteBool(false);
            w.WriteBool(false);
        });

        replicator.Apply(client, data);
        Assert.Equal(2, client.Values.Count);
        Assert.False(client.Values.ContainsKey("b"));
    }

    [Fact]
    public void Malformed_CountAboveLimitThrows()
    {
        Replicator replicator = CreateReplicator(new ReplicationLimits { MaxCollectionCount = 3 });

        // Сброс: не более трех записей, даже если все ключи новые.
        byte[] ok = Craft(w =>
        {
            w.WriteBool(true);
            w.WriteBool(true);
            w.WriteBool(true);
            WriteEntry(w, "x", 1);
            WriteEntry(w, "y", 2);
            WriteEntry(w, "z", 3);
            w.WriteBool(false);
        });
        StringMapHolder client = CreateClientWithThreeEntries(replicator);
        replicator.Apply(client, ok);
        Assert.Equal(["x", "y", "z"], client.Values.Keys.Order());

        byte[] tooManyInReset = Craft(w =>
        {
            w.WriteBool(true);
            w.WriteBool(true);
            w.WriteBool(true);
            WriteEntry(w, "x", 1);
            WriteEntry(w, "y", 2);
            WriteEntry(w, "z", 3);
            WriteEntry(w, "w", 4);
            w.WriteBool(false);
        });
        Assert.Throws<ReplicationFormatException>(() => replicator.Apply(client, tooManyInReset));

        // Без сброса: новый ключ сверх лимита.
        StringMapHolder second = CreateClientWithThreeEntries(replicator);
        byte[] tooMany = Craft(w =>
        {
            w.WriteBool(true);
            w.WriteBool(true);
            w.WriteBool(false);
            w.WriteBool(false);
            WriteEntry(w, "a", 10); // существующий ключ допустим
            WriteEntry(w, "d", 4);
            w.WriteBool(false);
        });
        Assert.Throws<ReplicationFormatException>(() => replicator.Apply(second, tooMany));
        Assert.Equal(3, second.Values.Count);
        Assert.Equal(10, second.Values["a"]);
    }

    [Fact]
    public void Malformed_TruncatedDataThrows()
    {
        Replicator replicator = CreateReplicator();
        var server = new Composite();
        server.Values[1] = 2;
        server.Items["abc"] = new Item { A = 1, Name = "abc" };
        server.Lists[3] = new ReplicatedList<int> { 1 };
        server.Maps.Add(new ReplicatedDictionary<int, string> { [1] = "q" });
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
        var server = new IntMapHolder();
        server.Values[1] = 1;
        server.Values[2] = 2;
        server.Values[3] = 3;
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        Delta(replicator, baseline);

        server.Values[4] = 4;
        var ex = Assert.Throws<ReplicationException>(() => replicator.TryWriteDelta(baseline, out _));
        Assert.Contains(nameof(IntMapHolder.Values), ex.Message);

        // После ошибки базовая копия сброшена, и следующая дельта снова полная.
        server.Values.Remove(4);
        DecodedMap recovered = DecodeIntMap(Delta(replicator, baseline));
        Assert.True(recovered.Reset);
    }

    // ---------- allocations ----------

    [Fact]
    public void UnchangedDictionaryDelta_DoesNotAllocate()
    {
        Replicator replicator = CreateReplicator();
        var server = new Composite();
        for (int i = 0; i < 100; i++)
        {
            server.Values[i] = i;
            server.Items["k" + i] = new Item { A = i };
        }

        server.Lists[1] = new ReplicatedList<int> { 1, 2 };
        server.Maps.Add(new ReplicatedDictionary<int, string> { [1] = "a" });
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
    public void ChangedDictionaryDeltaAndApply_DoNotAllocate()
    {
        Replicator replicator = CreateReplicator();
        var server = new Composite();
        var keys = new string[20];
        for (int i = 0; i < 20; i++)
        {
            server.Values[i] = i;
            keys[i] = "k" + i;
            server.Items[keys[i]] = new Item { A = i };
        }

        var itemMap = new ItemMapHolder();
        for (int i = 0; i < 20; i++)
        {
            itemMap.Items[i] = new Item { A = i };
        }

        ReplicationBaseline baseline = replicator.CreateBaseline(itemMap);
        ReplicationBaseline valueBaseline = replicator.CreateBaseline(server);
        var writer = new BitWriter(1 << 16);
        var client = new ItemMapHolder();
        var valueClient = new Composite();

        // Ключи-строки при чтении создают строки, поэтому применение проверяется на int-ключах,
        // а для строковых ключей — только запись.
        void Frame(int i)
        {
            writer.Reset();
            itemMap.Items[i % 20].A = i + 1000;
            replicator.TryWriteDelta(baseline, writer);
            var reader = new BitReader(writer.AsSpan());
            replicator.Apply(client, ref reader);

            writer.Reset();
            server.Values[i % 20] = i + 1000;
            server.Items[keys[i % 20]].A = i;
            replicator.TryWriteDelta(valueBaseline, writer);
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
        AssertDictionaryEqual(itemMap.Items, client.Items, AssertItemEqual);

        // Словарь значений с int-ключами: запись и применение без аллокаций.
        var valueServer = new IntMapHolder();
        for (int i = 0; i < 20; i++)
        {
            valueServer.Values[i] = i;
        }

        ReplicationBaseline intBaseline = replicator.CreateBaseline(valueServer);
        var intClient = new IntMapHolder();

        void ValueFrame(int i)
        {
            writer.Reset();
            valueServer.Values[i % 20] = i + 1000;
            replicator.TryWriteDelta(intBaseline, writer);
            var reader = new BitReader(writer.AsSpan());
            replicator.Apply(intClient, ref reader);
        }

        ValueFrame(1);
        ValueFrame(2);
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 3; i < 1000; i++)
        {
            ValueFrame(i);
        }

        allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        AssertValuesEqual(valueServer.Values, intClient.Values);
    }
}
