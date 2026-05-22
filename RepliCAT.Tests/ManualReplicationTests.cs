using RepliCAT;
using RepliCAT.Bits;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace RepliCAT.Tests;

public class ManualReplicationTests
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

    private static Replicator CreateReplicator()
    {
        var sink = new CollectingSink();
        ILogger logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
        return new Replicator(logger: logger);
    }

    private static byte[] Delta(Replicator replicator, ReplicationBaseline baseline)
    {
        Assert.True(replicator.TryWriteDelta(baseline, out byte[] data));
        Assert.NotNull(data);
        return data;
    }

    private static void NoDelta(Replicator replicator, ReplicationBaseline baseline)
    {
        Assert.False(replicator.TryWriteDelta(baseline, out byte[] data));
        Assert.Null(data);
    }

    private static byte[] Snapshot(Replicator replicator, ReplicationBaseline baseline)
    {
        Assert.True(replicator.TryWriteSnapshot(baseline, out byte[] data));
        return data;
    }

    // ---------- test types ----------

    public class Holder
    {
        [Replicated] public int Auto;
        [Replicated(Manual = true)] public int Manual;
        [Replicated(Manual = true)] public string Text;
    }

    public class OnlyManual
    {
        [Replicated(Manual = true)] public int Value;
    }

    public class Child
    {
        [Replicated] public int A;
        [Replicated(Manual = true)] public int M;
    }

    public class Parent
    {
        [Replicated] public int Score;
        [Replicated] public Child Child;
        [Replicated(Manual = true)] public Child ManualChild;
    }

    public class ListParent
    {
        [Replicated] public readonly ReplicatedList<Child> Items = new();
        [Replicated(Manual = true)] public readonly ReplicatedList<Child> ManualItems = new();
        [Replicated(Manual = true)] public readonly ReplicatedList<int> ManualNumbers = new();
    }

    public class MapParent
    {
        [Replicated] public readonly ReplicatedDictionary<int, Child> Map = new();
        [Replicated(Manual = true)] public readonly ReplicatedDictionary<int, Child> ManualMap = new();
        [Replicated(Manual = true)] public readonly ReplicatedDictionary<string, int> ManualCounters = new();
    }

    public class ManualProperty
    {
        public int SetterCalls;
        private int _value;

        [Replicated(Manual = true)]
        public int Value
        {
            get => _value;
            set
            {
                _value = value;
                SetterCalls++;
            }
        }
    }

    public class ManualBackingField
    {
        [field: Replicated(Manual = true)] public int Value { get; set; }
    }

    public static class SchemaAuto
    {
        public class Entity
        {
            [Replicated] public int A;
        }
    }

    public static class SchemaManual
    {
        public class Entity
        {
            [Replicated(Manual = true)] public int A;
        }
    }

    public static class SchemaNestedAuto
    {
        public class Entity
        {
            [Replicated] public Child C;
        }
    }

    public static class SchemaNestedManual
    {
        public class Entity
        {
            [Replicated(Manual = true)] public Child C;
        }
    }

    public class World
    {
        [Replicated] public int Tick;
        [Replicated(Manual = true)] public string Title;
        [Replicated] public Child Child;
        [Replicated(Manual = true)] public Child ManualChild;
        [Replicated] public readonly ReplicatedList<Child> Items = new();
        [Replicated(Manual = true)] public readonly ReplicatedList<Child> ManualItems = new();
        [Replicated] public readonly ReplicatedDictionary<int, Child> Map = new();
        [Replicated(Manual = true)] public readonly ReplicatedDictionary<int, Child> ManualMap = new();
    }

    // ---------- comparison helpers ----------

    private static void AssertChildEqual(Child expected, Child actual)
    {
        if (expected == null)
        {
            Assert.Null(actual);
            return;
        }

        Assert.NotNull(actual);
        Assert.Equal(expected.A, actual.A);
        Assert.Equal(expected.M, actual.M);
    }

    private static void AssertListEqual(ReplicatedList<Child> expected, ReplicatedList<Child> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            AssertChildEqual(expected[i], actual[i]);
        }
    }

    private static void AssertMapEqual(ReplicatedDictionary<int, Child> expected, ReplicatedDictionary<int, Child> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        foreach (KeyValuePair<int, Child> pair in expected)
        {
            Assert.True(actual.TryGetValue(pair.Key, out Child value));
            AssertChildEqual(pair.Value, value);
        }
    }

    private static void AssertWorldEqual(World expected, World actual)
    {
        Assert.Equal(expected.Tick, actual.Tick);
        Assert.Equal(expected.Title, actual.Title);
        AssertChildEqual(expected.Child, actual.Child);
        AssertChildEqual(expected.ManualChild, actual.ManualChild);
        AssertListEqual(expected.Items, actual.Items);
        AssertListEqual(expected.ManualItems, actual.ManualItems);
        AssertMapEqual(expected.Map, actual.Map);
        AssertMapEqual(expected.ManualMap, actual.ManualMap);
    }

    // ---------- value members ----------

    [Fact]
    public void FirstDelta_ContainsManualMembers()
    {
        Replicator replicator = CreateReplicator();
        var server = new Holder { Auto = 1, Manual = 2, Text = "hello" };
        ReplicationBaseline baseline = replicator.CreateBaseline(server);

        var client = new Holder();
        replicator.Apply(client, Delta(replicator, baseline));

        Assert.Equal(1, client.Auto);
        Assert.Equal(2, client.Manual);
        Assert.Equal("hello", client.Text);
    }

    [Fact]
    public void ChangeWithoutMarkDirty_IsNotSent()
    {
        Replicator replicator = CreateReplicator();
        var server = new Holder { Auto = 1, Manual = 2, Text = "a" };
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var client = new Holder();
        replicator.Apply(client, Delta(replicator, baseline));

        server.Manual = 3;
        server.Text = "b";
        NoDelta(replicator, baseline);

        // Изменение обычного члена отправляется, manual-члены — нет.
        server.Auto = 5;
        replicator.Apply(client, Delta(replicator, baseline));
        Assert.Equal(5, client.Auto);
        Assert.Equal(2, client.Manual);
        Assert.Equal("a", client.Text);
    }

    [Fact]
    public void MarkDirty_SendsOnceUntilNextMark()
    {
        Replicator replicator = CreateReplicator();
        var server = new Holder { Auto = 1, Manual = 2, Text = "a" };
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var client = new Holder();
        replicator.Apply(client, Delta(replicator, baseline));

        server.Manual = 3;
        server.Text = "b";
        ManualReplication.MarkDirty(server, nameof(Holder.Manual));

        client.Auto = -100;
        replicator.Apply(client, Delta(replicator, baseline));
        Assert.Equal(3, client.Manual);
        Assert.Equal("a", client.Text); // Text не помечен
        Assert.Equal(-100, client.Auto); // обычный член не менялся и не отправлялся

        NoDelta(replicator, baseline);

        server.Manual = 4;
        NoDelta(replicator, baseline);

        ManualReplication.MarkDirty(server, nameof(Holder.Manual));
        ManualReplication.MarkDirty(server, nameof(Holder.Text));
        replicator.Apply(client, Delta(replicator, baseline));
        Assert.Equal(4, client.Manual);
        Assert.Equal("b", client.Text);
        NoDelta(replicator, baseline);
    }

    [Fact]
    public void MarkDirty_WithoutChange_StillSendsValue()
    {
        Replicator replicator = CreateReplicator();
        var server = new OnlyManual { Value = 7 };
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var client = new OnlyManual();
        replicator.Apply(client, Delta(replicator, baseline));

        client.Value = 100;
        ManualReplication.MarkDirty(server, nameof(OnlyManual.Value));
        replicator.Apply(client, Delta(replicator, baseline));
        Assert.Equal(7, client.Value);
    }

    [Fact]
    public void MarkDirty_BeforeChangeInSameFrame_SendsCurrentValue()
    {
        Replicator replicator = CreateReplicator();
        var server = new OnlyManual { Value = 1 };
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var client = new OnlyManual();
        replicator.Apply(client, Delta(replicator, baseline));

        // Порядок вызовов не важен: отправляется значение на момент записи дельты.
        ManualReplication.MarkDirty(server, nameof(OnlyManual.Value));
        server.Value = 2;
        replicator.Apply(client, Delta(replicator, baseline));
        Assert.Equal(2, client.Value);
    }

    [Fact]
    public void MarkDirty_IsNotConsumedByOneBaseline()
    {
        Replicator first = CreateReplicator();
        Replicator second = CreateReplicator();
        var server = new OnlyManual { Value = 1 };
        ReplicationBaseline baselineA = first.CreateBaseline(server);
        ReplicationBaseline baselineB = second.CreateBaseline(server);
        Delta(first, baselineA);
        Delta(second, baselineB);

        server.Value = 2;
        ManualReplication.MarkDirty(server, nameof(OnlyManual.Value));

        var clientA = new OnlyManual();
        var clientB = new OnlyManual();
        first.Apply(clientA, Delta(first, baselineA));
        second.Apply(clientB, Delta(second, baselineB));
        Assert.Equal(2, clientA.Value);
        Assert.Equal(2, clientB.Value);
        NoDelta(first, baselineA);
        NoDelta(second, baselineB);
    }

    [Fact]
    public void MarkDirty_UnknownOrNonManualMember_IsHarmless()
    {
        Replicator replicator = CreateReplicator();
        var server = new Holder { Auto = 1, Manual = 2 };
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        Delta(replicator, baseline);

        ManualReplication.MarkDirty(server, "NoSuchMember");
        ManualReplication.MarkDirty(server, nameof(Holder.Auto));
        ManualReplication.MarkDirty(server, "");
        ManualReplication.MarkDirty(new object(), "Anything");
        NoDelta(replicator, baseline);

        // Помеченный обычный член сравнивается как обычно.
        ManualReplication.MarkDirty(server, nameof(Holder.Auto));
        NoDelta(replicator, baseline);
    }

    [Fact]
    public void MarkDirty_ValidatesArguments()
    {
        Assert.Throws<ArgumentNullException>(() => ManualReplication.MarkDirty(null, "A"));
        Assert.Throws<ArgumentNullException>(() => ManualReplication.MarkDirty(new Holder(), null));
        Assert.Throws<ArgumentException>(() => ManualReplication.MarkDirty(5, "A"));
    }

    [Fact]
    public void ManualProperty_SetterInvokedOnlyWhenSent()
    {
        Replicator replicator = CreateReplicator();
        var server = new ManualProperty { Value = 1 };
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var client = new ManualProperty();
        replicator.Apply(client, Delta(replicator, baseline));
        Assert.Equal(1, client.SetterCalls);

        server.Value = 2;
        NoDelta(replicator, baseline);

        ManualReplication.MarkDirty(server, nameof(ManualProperty.Value));
        replicator.Apply(client, Delta(replicator, baseline));
        Assert.Equal(2, client.SetterCalls);
        Assert.Equal(2, client.Value);
    }

    [Fact]
    public void ManualAutoPropertyBackingField_MarkedByPropertyName()
    {
        Replicator replicator = CreateReplicator();
        var server = new ManualBackingField { Value = 1 };
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var client = new ManualBackingField();
        replicator.Apply(client, Delta(replicator, baseline));
        Assert.Equal(1, client.Value);

        server.Value = 2;
        NoDelta(replicator, baseline);

        ManualReplication.MarkDirty(server, nameof(ManualBackingField.Value));
        replicator.Apply(client, Delta(replicator, baseline));
        Assert.Equal(2, client.Value);
        NoDelta(replicator, baseline);
    }

    // ---------- snapshots ----------

    [Fact]
    public void Snapshot_ContainsLiveManualValue_WithoutMarkDirty()
    {
        Replicator replicator = CreateReplicator();
        var server = new Holder { Auto = 1, Manual = 2, Text = "a" };
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var client = new Holder();
        replicator.Apply(client, Delta(replicator, baseline));

        server.Manual = 3;
        server.Text = "b";
        server.Auto = 10; // после последней дельты: снимок берет значение из базовой копии

        var late = new Holder { Auto = -1, Manual = -1, Text = "junk" };
        replicator.Apply(late, Snapshot(replicator, baseline));
        Assert.Equal(1, late.Auto);
        Assert.Equal(3, late.Manual);
        Assert.Equal("b", late.Text);

        // Снимок не меняет базовую копию: manual-члены по-прежнему не помечены.
        byte[] delta = Delta(replicator, baseline);
        replicator.Apply(client, delta);
        replicator.Apply(late, delta);
        Assert.Equal(10, late.Auto);
        Assert.Equal(3, late.Manual);
        Assert.Equal(2, client.Manual);
        NoDelta(replicator, baseline);

        // После пометки все клиенты сходятся.
        ManualReplication.MarkDirty(server, nameof(Holder.Manual));
        ManualReplication.MarkDirty(server, nameof(Holder.Text));
        delta = Delta(replicator, baseline);
        replicator.Apply(client, delta);
        replicator.Apply(late, delta);
        Assert.Equal(3, client.Manual);
        Assert.Equal("b", client.Text);
        Assert.Equal(3, late.Manual);
        Assert.Equal("b", late.Text);
    }

    [Fact]
    public void Snapshot_MidFrameAfterMarkDirty_ThenDelta_Converges()
    {
        Replicator replicator = CreateReplicator();
        var server = new OnlyManual { Value = 1 };
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        Delta(replicator, baseline);

        server.Value = 2;
        ManualReplication.MarkDirty(server, nameof(OnlyManual.Value));
        var late = new OnlyManual();
        replicator.Apply(late, Snapshot(replicator, baseline));
        Assert.Equal(2, late.Value);

        // Значение вернулось до конца кадра: пометка есть, поэтому дельта исправит позднего клиента.
        server.Value = 1;
        replicator.Apply(late, Delta(replicator, baseline));
        Assert.Equal(1, late.Value);
    }

    [Fact]
    public void Snapshot_NestedObjectReplaced_ReadsManualMembersFromOldObject()
    {
        Replicator replicator = CreateReplicator();
        var oldChild = new Child { A = 1, M = 5 };
        var server = new Parent { Child = oldChild };
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var client = new Parent();
        replicator.Apply(client, Delta(replicator, baseline));

        oldChild.A = 2;
        oldChild.M = 6; // без пометки
        server.Child = new Child { A = 10, M = 9 };

        var late = new Parent();
        replicator.Apply(late, Snapshot(replicator, baseline));

        // Снимок описывает последнюю отправленную ссылку: обычные члены из тени,
        // manual-члены из живого (прежнего) объекта.
        Assert.Equal(1, late.Child.A);
        Assert.Equal(6, late.Child.M);
        Child lateChild = late.Child;

        // Следующая дельта пишет новый объект целиком и выравнивает всех клиентов.
        byte[] delta = Delta(replicator, baseline);
        replicator.Apply(client, delta);
        replicator.Apply(late, delta);
        AssertChildEqual(server.Child, client.Child);
        AssertChildEqual(server.Child, late.Child);
        Assert.Same(lateChild, late.Child);
        NoDelta(replicator, baseline);
    }

    [Fact]
    public void Snapshot_ListItemReplaced_ReadsManualMembersFromOldItem()
    {
        Replicator replicator = CreateReplicator();
        var server = new ListParent();
        var first = new Child { A = 1, M = 1 };
        server.Items.Add(first);
        server.Items.Add(new Child { A = 2, M = 2 });
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var client = new ListParent();
        replicator.Apply(client, Delta(replicator, baseline));

        first.M = 11; // без пометки
        server.Items.RemoveAt(0);
        server.Items.Add(new Child { A = 3, M = 3 }); // переиспользует слот 0

        var late = new ListParent();
        replicator.Apply(late, Snapshot(replicator, baseline));
        Assert.Equal(2, late.Items.Count);
        Assert.Equal(11, late.Items[0].M);
        Assert.Equal(1, late.Items[0].A);

        byte[] delta = Delta(replicator, baseline);
        replicator.Apply(client, delta);
        replicator.Apply(late, delta);
        AssertListEqual(server.Items, client.Items);
        AssertListEqual(server.Items, late.Items);
    }

    [Fact]
    public void Snapshot_DictionaryValueReplaced_ReadsManualMembersFromOldValue()
    {
        Replicator replicator = CreateReplicator();
        var server = new MapParent();
        var old = new Child { A = 1, M = 1 };
        server.Map[1] = old;
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var client = new MapParent();
        replicator.Apply(client, Delta(replicator, baseline));

        old.M = 7;
        server.Map[1] = new Child { A = 5, M = 8 };

        var late = new MapParent();
        replicator.Apply(late, Snapshot(replicator, baseline));
        Assert.Equal(1, late.Map[1].A);
        Assert.Equal(7, late.Map[1].M);

        byte[] delta = Delta(replicator, baseline);
        replicator.Apply(client, delta);
        replicator.Apply(late, delta);
        AssertMapEqual(server.Map, client.Map);
        AssertMapEqual(server.Map, late.Map);
    }

    // ---------- manual nested objects and collections ----------

    [Fact]
    public void ManualNestedObject_SendsWholeSubtreeOnlyAfterMarkDirty()
    {
        Replicator replicator = CreateReplicator();
        var server = new Parent { ManualChild = new Child { A = 1, M = 2 } };
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var client = new Parent();
        replicator.Apply(client, Delta(replicator, baseline));
        Child clientChild = client.ManualChild;
        AssertChildEqual(server.ManualChild, clientChild);

        // Изменения внутри manual-поддерева не отправляются, в том числе пометка вложенного manual-члена.
        server.ManualChild.A = 3;
        server.ManualChild.M = 4;
        ManualReplication.MarkDirty(server.ManualChild, nameof(Child.M));
        NoDelta(replicator, baseline);

        // Замена ссылки внутри manual-члена тоже не отправляется.
        Child replaced = new Child { A = 5, M = 6 };
        server.ManualChild = replaced;
        NoDelta(replicator, baseline);

        replaced.A = 7;
        ManualReplication.MarkDirty(server, nameof(Parent.ManualChild));
        replicator.Apply(client, Delta(replicator, baseline));
        Assert.Same(clientChild, client.ManualChild);
        AssertChildEqual(replaced, client.ManualChild);
        NoDelta(replicator, baseline);

        // Пометка — всегда все поддерево, даже если изменился один член.
        client.ManualChild.M = 777;
        replaced.A = 8;
        ManualReplication.MarkDirty(server, nameof(Parent.ManualChild));
        replicator.Apply(client, Delta(replicator, baseline));
        AssertChildEqual(replaced, client.ManualChild);
        NoDelta(replicator, baseline);

        // null тоже отправляется только после пометки.
        server.ManualChild = null;
        NoDelta(replicator, baseline);
        ManualReplication.MarkDirty(server, nameof(Parent.ManualChild));
        replicator.Apply(client, Delta(replicator, baseline));
        Assert.Null(client.ManualChild);
    }

    [Fact]
    public void ManualNestedObject_SnapshotWritesLiveSubtree()
    {
        Replicator replicator = CreateReplicator();
        var server = new Parent { ManualChild = new Child { A = 1, M = 2 } };
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        Delta(replicator, baseline);

        server.ManualChild.A = 3;
        server.ManualChild.M = 4;
        var late = new Parent();
        replicator.Apply(late, Snapshot(replicator, baseline));
        AssertChildEqual(server.ManualChild, late.ManualChild);

        server.ManualChild = new Child { A = 9, M = 9 };
        late = new Parent();
        replicator.Apply(late, Snapshot(replicator, baseline));
        AssertChildEqual(server.ManualChild, late.ManualChild);
        NoDelta(replicator, baseline);

        ManualReplication.MarkDirty(server, nameof(Parent.ManualChild));
        replicator.Apply(late, Delta(replicator, baseline));
        AssertChildEqual(server.ManualChild, late.ManualChild);
    }

    [Fact]
    public void ManualListOfObjects_ItemChangesNotSentUntilMarkDirty()
    {
        Replicator replicator = CreateReplicator();
        var server = new ListParent();
        server.ManualItems.Add(new Child { A = 1, M = 1 });
        server.ManualItems.Add(new Child { A = 2, M = 2 });
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var client = new ListParent();
        replicator.Apply(client, Delta(replicator, baseline));
        AssertListEqual(server.ManualItems, client.ManualItems);
        Child clientFirst = client.ManualItems[0];

        server.ManualItems[0].A = 10;
        server.ManualItems.Add(new Child { A = 3, M = 3 });
        NoDelta(replicator, baseline);

        ManualReplication.MarkDirty(server, nameof(ListParent.ManualItems));
        replicator.Apply(client, Delta(replicator, baseline));
        AssertListEqual(server.ManualItems, client.ManualItems);
        Assert.Same(clientFirst, client.ManualItems[0]);
        NoDelta(replicator, baseline);

        // После пометки (запись в настоящую тень сбросом) изменение члена элемента без пометки не отправляется.
        server.ManualItems[1].A = 20;
        ManualReplication.MarkDirty(server.ManualItems[1], nameof(Child.M));
        NoDelta(replicator, baseline);
        Assert.Equal(2, client.ManualItems[1].A);

        server.ManualItems.RemoveAt(0);
        NoDelta(replicator, baseline);

        ManualReplication.MarkDirty(server, nameof(ListParent.ManualItems));
        replicator.Apply(client, Delta(replicator, baseline));
        AssertListEqual(server.ManualItems, client.ManualItems);
        NoDelta(replicator, baseline);
    }

    [Fact]
    public void ManualListOfValues_SentOnlyAfterMarkDirty()
    {
        Replicator replicator = CreateReplicator();
        var server = new ListParent();
        server.ManualNumbers.Add(1);
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var client = new ListParent();
        replicator.Apply(client, Delta(replicator, baseline));
        Assert.Equal(new[] { 1 }, client.ManualNumbers);

        server.ManualNumbers.Add(2);
        server.ManualNumbers.Insert(0, 0);
        NoDelta(replicator, baseline);

        var late = new ListParent();
        late.ManualNumbers.Add(99);
        replicator.Apply(late, Snapshot(replicator, baseline));
        Assert.Equal(new[] { 0, 1, 2 }, late.ManualNumbers);

        ManualReplication.MarkDirty(server, nameof(ListParent.ManualNumbers));
        byte[] delta = Delta(replicator, baseline);
        replicator.Apply(client, delta);
        replicator.Apply(late, delta);
        Assert.Equal(new[] { 0, 1, 2 }, client.ManualNumbers);
        Assert.Equal(new[] { 0, 1, 2 }, late.ManualNumbers);
    }

    [Fact]
    public void ManualDictionaryOfObjects_ValueChangesNotSentUntilMarkDirty()
    {
        Replicator replicator = CreateReplicator();
        var server = new MapParent();
        server.ManualMap[1] = new Child { A = 1, M = 1 };
        server.ManualMap[2] = new Child { A = 2, M = 2 };
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var client = new MapParent();
        replicator.Apply(client, Delta(replicator, baseline));
        AssertMapEqual(server.ManualMap, client.ManualMap);
        Child clientOne = client.ManualMap[1];

        server.ManualMap[1].A = 10;
        server.ManualMap[3] = new Child { A = 3, M = 3 };
        server.ManualMap.Remove(2);
        NoDelta(replicator, baseline);

        ManualReplication.MarkDirty(server, nameof(MapParent.ManualMap));
        replicator.Apply(client, Delta(replicator, baseline));
        AssertMapEqual(server.ManualMap, client.ManualMap);
        Assert.Same(clientOne, client.ManualMap[1]);
        NoDelta(replicator, baseline);

        // После пометки (запись в настоящую тень сбросом) изменение члена значения без пометки не отправляется.
        server.ManualMap[1].A = 100;
        server.ManualMap[1].M = 100;
        ManualReplication.MarkDirty(server.ManualMap[1], nameof(Child.M));
        NoDelta(replicator, baseline);
        Assert.Equal(10, client.ManualMap[1].A);

        // Пометка снова отправляет все поддерево.
        client.ManualMap[3].M = -5;
        ManualReplication.MarkDirty(server, nameof(MapParent.ManualMap));
        replicator.Apply(client, Delta(replicator, baseline));
        AssertMapEqual(server.ManualMap, client.ManualMap);
        NoDelta(replicator, baseline);
    }

    [Fact]
    public void ManualDictionaryOfValues_SnapshotAndMarkDirty()
    {
        Replicator replicator = CreateReplicator();
        var server = new MapParent();
        server.ManualCounters["a"] = 1;
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var client = new MapParent();
        replicator.Apply(client, Delta(replicator, baseline));

        server.ManualCounters["a"] = 2;
        server.ManualCounters["b"] = 3;
        NoDelta(replicator, baseline);

        var late = new MapParent();
        late.ManualCounters["junk"] = 1;
        replicator.Apply(late, Snapshot(replicator, baseline));
        Assert.Equal(2, late.ManualCounters.Count);
        Assert.Equal(2, late.ManualCounters["a"]);
        Assert.Equal(3, late.ManualCounters["b"]);

        ManualReplication.MarkDirty(server, nameof(MapParent.ManualCounters));
        byte[] delta = Delta(replicator, baseline);
        replicator.Apply(client, delta);
        replicator.Apply(late, delta);
        Assert.Equal(server.ManualCounters.OrderBy(p => p.Key), client.ManualCounters.OrderBy(p => p.Key));
        Assert.Equal(server.ManualCounters.OrderBy(p => p.Key), late.ManualCounters.OrderBy(p => p.Key));
    }

    // ---------- manual members inside collections of objects ----------

    [Fact]
    public void ManualMembersOfListItems_SentAsItemDeltaAfterMarkDirty()
    {
        Replicator replicator = CreateReplicator();
        var server = new ListParent();
        server.Items.Add(new Child { A = 1, M = 1 });
        server.Items.Add(new Child { A = 2, M = 2 });
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var client = new ListParent();
        replicator.Apply(client, Delta(replicator, baseline));
        AssertListEqual(server.Items, client.Items);
        Child clientSecond = client.Items[1];

        server.Items[1].M = 20;
        NoDelta(replicator, baseline);

        client.Items[1].A = -1; // обычный член элемента не менялся и не должен отправиться
        ManualReplication.MarkDirty(server.Items[1], nameof(Child.M));
        replicator.Apply(client, Delta(replicator, baseline));
        Assert.Same(clientSecond, client.Items[1]);
        Assert.Equal(20, client.Items[1].M);
        Assert.Equal(-1, client.Items[1].A);
        Assert.Equal(1, client.Items[0].M);
        NoDelta(replicator, baseline);

        // Новый элемент отправляется целиком, включая manual-члены.
        server.Items.Add(new Child { A = 3, M = 30 });
        replicator.Apply(client, Delta(replicator, baseline));
        Assert.Equal(30, client.Items[2].M);
    }

    [Fact]
    public void ManualMembersOfDictionaryValues_SentAfterMarkDirty()
    {
        Replicator replicator = CreateReplicator();
        var server = new MapParent();
        server.Map[1] = new Child { A = 1, M = 1 };
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var client = new MapParent();
        replicator.Apply(client, Delta(replicator, baseline));

        server.Map[1].M = 5;
        NoDelta(replicator, baseline);

        var late = new MapParent();
        replicator.Apply(late, Snapshot(replicator, baseline));
        Assert.Equal(5, late.Map[1].M);

        ManualReplication.MarkDirty(server.Map[1], nameof(Child.M));
        byte[] delta = Delta(replicator, baseline);
        replicator.Apply(client, delta);
        replicator.Apply(late, delta);
        AssertMapEqual(server.Map, client.Map);
        AssertMapEqual(server.Map, late.Map);
    }

    [Fact]
    public void SharedObjectInTwoPlaces_MarkDirtySentForEachPath()
    {
        Replicator replicator = CreateReplicator();
        var shared = new Child { A = 1, M = 1 };
        var server = new ListParent();
        server.Items.Add(shared);
        server.Items.Add(shared);
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var client = new ListParent();
        replicator.Apply(client, Delta(replicator, baseline));

        shared.M = 2;
        ManualReplication.MarkDirty(shared, nameof(Child.M));
        replicator.Apply(client, Delta(replicator, baseline));
        Assert.Equal(2, client.Items[0].M);
        Assert.Equal(2, client.Items[1].M);
        NoDelta(replicator, baseline);
    }

    // ---------- schema hash ----------

    [Fact]
    public void SchemaHash_IncludesManualFlag()
    {
        Replicator replicator = CreateReplicator();
        Assert.NotEqual(replicator.GetSchemaHash(typeof(SchemaAuto.Entity)), replicator.GetSchemaHash(typeof(SchemaManual.Entity)));
        Assert.NotEqual(replicator.GetSchemaHash(typeof(SchemaNestedAuto.Entity)),
            replicator.GetSchemaHash(typeof(SchemaNestedManual.Entity)));
        Assert.Equal(replicator.GetSchemaHash(typeof(SchemaManual.Entity)),
            CreateReplicator().GetSchemaHash(typeof(SchemaManual.Entity)));
    }

    // ---------- randomized consistency ----------

    [Fact]
    public void Randomized_WellBehavedMarkDirty_AllClientsMatchServer()
    {
        var random = new Random(9);
        Replicator replicator = CreateReplicator();
        var server = new World();
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var client = new World();
        var lateClients = new List<World>();

        Child NewChild()
        {
            return new Child { A = random.Next(100), M = random.Next(100) };
        }

        // Изменение внутри manual-члена корня помечает сам член (поддерево manual целиком).
        void MutateChild(Child child, string rootMember)
        {
            if (random.Next(2) == 0)
            {
                child.A = random.Next(100);
            }
            else
            {
                child.M = random.Next(100);
            }

            if (rootMember != null)
            {
                ManualReplication.MarkDirty(server, rootMember);
            }
            else if (random.Next(4) != 0)
            {
                // Иногда забываем пометить manual-член вложенного объекта ниже и помечаем его позже.
                ManualReplication.MarkDirty(child, nameof(Child.M));
            }
        }

        void MutateList(ReplicatedList<Child> list, string rootMember)
        {
            switch (random.Next(5))
            {
                case 0:
                    list.Add(NewChild());
                    break;
                case 1:
                    if (list.Count > 0)
                    {
                        list.RemoveAt(random.Next(list.Count));
                    }

                    break;
                case 2:
                    list.Insert(random.Next(list.Count + 1), NewChild());
                    break;
                case 3:
                    if (list.Count > 0)
                    {
                        list[random.Next(list.Count)] = NewChild();
                    }

                    break;
                default:
                    if (list.Count > 0)
                    {
                        MutateChild(list[random.Next(list.Count)], rootMember);
                    }

                    break;
            }

            if (rootMember != null)
            {
                ManualReplication.MarkDirty(server, rootMember);
            }
        }

        void MutateMap(ReplicatedDictionary<int, Child> map, string rootMember)
        {
            int key = random.Next(8);
            switch (random.Next(3))
            {
                case 0:
                    map[key] = NewChild();
                    break;
                case 1:
                    map.Remove(key);
                    break;
                default:
                    if (map.TryGetValue(key, out Child child))
                    {
                        MutateChild(child, rootMember);
                    }

                    break;
            }

            if (rootMember != null)
            {
                ManualReplication.MarkDirty(server, rootMember);
            }
        }

        var pendingMarks = new List<Child>();

        void Mutate()
        {
            switch (random.Next(10))
            {
                case 0:
                    server.Tick++;
                    break;
                case 1:
                    server.Title = "t" + random.Next(1000);
                    ManualReplication.MarkDirty(server, nameof(World.Title));
                    break;
                case 2:
                    if (server.Child == null || random.Next(3) == 0)
                    {
                        server.Child = random.Next(4) == 0 ? null : NewChild();
                    }
                    else
                    {
                        server.Child.A = random.Next(100);
                        server.Child.M = random.Next(100);
                        pendingMarks.Add(server.Child);
                    }

                    break;
                case 3:
                    if (server.ManualChild == null || random.Next(3) == 0)
                    {
                        server.ManualChild = random.Next(4) == 0 ? null : NewChild();
                        ManualReplication.MarkDirty(server, nameof(World.ManualChild));
                    }
                    else
                    {
                        MutateChild(server.ManualChild, nameof(World.ManualChild));
                    }

                    break;
                case 4:
                    MutateList(server.Items, null);
                    break;
                case 5:
                    MutateList(server.ManualItems, nameof(World.ManualItems));
                    break;
                case 6:
                    MutateMap(server.Map, null);
                    break;
                case 7:
                    MutateMap(server.ManualMap, nameof(World.ManualMap));
                    break;
                default:
                    // Члены M элементов обычных коллекций: пометка в том же кадре.
                    if (server.Items.Count > 0)
                    {
                        Child item = server.Items[random.Next(server.Items.Count)];
                        item.M = random.Next(100);
                        pendingMarks.Add(item);
                    }

                    break;
            }
        }

        for (int frame = 0; frame < 1000; frame++)
        {
            int mutations = random.Next(4);
            for (int m = 0; m < mutations; m++)
            {
                Mutate();

                if (random.Next(20) == 0 && baseline.IsWritten)
                {
                    var late = new World { Tick = -1, Title = "junk", ManualChild = new Child { A = -1 } };
                    late.ManualItems.Add(new Child());
                    late.Map[100] = new Child();
                    replicator.Apply(late, Snapshot(replicator, baseline));
                    lateClients.Add(late);
                    if (lateClients.Count > 4)
                    {
                        lateClients.RemoveAt(0);
                    }
                }
            }

            // Пользователь помечает manual-члены обычных вложенных объектов до конца кадра.
            foreach (Child child in pendingMarks)
            {
                ManualReplication.MarkDirty(child, nameof(Child.M));
            }

            pendingMarks.Clear();

            // Непомеченные manual-члены вложенных объектов (MutateChild без пометки) выравниваем явно.
            MarkAllNested(server);

            if (replicator.TryWriteDelta(baseline, out byte[] delta))
            {
                replicator.Apply(client, delta);
                foreach (World late in lateClients)
                {
                    replicator.Apply(late, delta);
                }
            }

            AssertWorldEqual(server, client);
            foreach (World late in lateClients)
            {
                AssertWorldEqual(server, late);
            }

            Assert.False(replicator.TryWriteDelta(baseline, out _));
        }
    }

    /// <summary>
    /// Помечает manual-член M всех вложенных объектов вне manual-поддеревьев, у которых он мог измениться
    /// без пометки (пометка идемпотентна по смыслу: лишняя пометка просто отправляет значение еще раз).
    /// </summary>
    private static void MarkAllNested(World world)
    {
        if (world.Child != null)
        {
            ManualReplication.MarkDirty(world.Child, nameof(Child.M));
        }

        foreach (Child item in world.Items)
        {
            ManualReplication.MarkDirty(item, nameof(Child.M));
        }

        foreach (KeyValuePair<int, Child> pair in world.Map)
        {
            ManualReplication.MarkDirty(pair.Value, nameof(Child.M));
        }
    }

    // ---------- thread safety ----------

    [Fact]
    public void MarkDirty_ConcurrentWithDeltas_IsEventuallySent()
    {
        Replicator replicator = CreateReplicator();
        var servers = new OnlyManual[8];
        var clients = new OnlyManual[servers.Length];
        var baselines = new ReplicationBaseline[servers.Length];
        for (int i = 0; i < servers.Length; i++)
        {
            servers[i] = new OnlyManual();
            clients[i] = new OnlyManual();
            baselines[i] = replicator.CreateBaseline(servers[i]);
            replicator.Apply(clients[i], Delta(replicator, baselines[i]));
        }

        using var stop = new CancellationTokenSource();
        var threads = new Thread[4];
        for (int t = 0; t < threads.Length; t++)
        {
            int seed = t;
            threads[t] = new Thread(() =>
            {
                var random = new Random(seed);
                for (int n = 0; n < 20000; n++)
                {
                    OnlyManual target = servers[random.Next(servers.Length)];
                    Interlocked.Increment(ref target.Value);
                    ManualReplication.MarkDirty(target, nameof(OnlyManual.Value));
                    ManualReplication.MarkDirty(target, "Other" + (n & 3));
                }
            });
            threads[t].Start();
        }

        while (threads.Any(thread => thread.IsAlive))
        {
            for (int i = 0; i < servers.Length; i++)
            {
                if (replicator.TryWriteDelta(baselines[i], out byte[] delta))
                {
                    replicator.Apply(clients[i], delta);
                }
            }
        }

        foreach (Thread thread in threads)
        {
            thread.Join();
        }

        // Каждая пометка, сделанная до записи дельты, попадает в эту или следующую дельту.
        for (int i = 0; i < servers.Length; i++)
        {
            if (replicator.TryWriteDelta(baselines[i], out byte[] delta))
            {
                replicator.Apply(clients[i], delta);
            }

            Assert.Equal(servers[i].Value, clients[i].Value);
            Assert.False(replicator.TryWriteDelta(baselines[i], out _));
        }

        Assert.Equal(threads.Length * 20000, servers.Sum(server => server.Value));
    }

    // ---------- allocations ----------

    [Fact]
    public void UnchangedManualDelta_DoesNotAllocate()
    {
        Replicator replicator = CreateReplicator();
        var holder = new Holder { Auto = 1, Manual = 2, Text = "x" };
        var unmarked = new Holder { Auto = 1 };
        var parent = new ListParent();
        parent.Items.Add(new Child { A = 1, M = 1 });
        parent.Items.Add(new Child { A = 2, M = 2 });
        parent.ManualItems.Add(new Child());
        var mapParent = new MapParent();
        mapParent.Map[1] = new Child();
        mapParent.ManualMap[1] = new Child();

        ReplicationBaseline[] baselines =
        [
            replicator.CreateBaseline(holder),
            replicator.CreateBaseline(unmarked),
            replicator.CreateBaseline(parent),
            replicator.CreateBaseline(mapParent)
        ];

        var writer = new BitWriter(1024);
        foreach (ReplicationBaseline baseline in baselines)
        {
            Assert.True(replicator.TryWriteDelta(baseline, writer));
        }

        // Таблицы пометок существуют, но с последней записи ничего не помечалось.
        ManualReplication.MarkDirty(holder, nameof(Holder.Manual));
        ManualReplication.MarkDirty(parent.Items[0], nameof(Child.M));
        ManualReplication.MarkDirty(parent, nameof(ListParent.ManualItems));
        ManualReplication.MarkDirty(mapParent, "Unknown");
        writer.Reset();
        foreach (ReplicationBaseline baseline in baselines)
        {
            replicator.TryWriteDelta(baseline, writer);
        }

        writer.Reset();
        AllocationAssert.DoesNotAllocate(() =>
        {
            for (int i = 0; i < 1000; i++)
            {
                foreach (ReplicationBaseline baseline in baselines)
                {
                    Assert.False(replicator.TryWriteDelta(baseline, writer));
                }
            }
        });
    }

    [Fact]
    public void MarkedManualDeltaAndApply_DoNotAllocate()
    {
        Replicator replicator = CreateReplicator();
        var server = new Holder { Auto = 1, Manual = 2 };
        var parent = new ListParent();
        parent.Items.Add(new Child());
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        ReplicationBaseline parentBaseline = replicator.CreateBaseline(parent);
        var client = new Holder();
        var parentClient = new ListParent();
        var writer = new BitWriter(1024);

        void Frame(int i)
        {
            writer.Reset();
            server.Manual = i;
            ManualReplication.MarkDirty(server, nameof(Holder.Manual));
            ManualReplication.MarkDirty(server, "Unknown");
            Child item = parent.Items[0];
            item.M = i;
            ManualReplication.MarkDirty(item, nameof(Child.M));
            replicator.TryWriteDelta(baseline, writer);
            replicator.TryWriteDelta(parentBaseline, writer);
            var reader = new BitReader(writer.AsSpan());
            replicator.Apply(client, ref reader);
            replicator.Apply(parentClient, ref reader);
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
        Assert.Equal(999, client.Manual);
        Assert.Equal(999, parentClient.Items[0].M);
    }
}
