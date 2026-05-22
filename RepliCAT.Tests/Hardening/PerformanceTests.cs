using RepliCAT;
using RepliCAT.Bits;

namespace RepliCAT.Tests.Hardening;

/// <summary>
/// Проверка производительности (не бенчмарк): проход дельт по неизмененным объектам не выделяет память.
/// Снимки здесь не измеряются: они редки и создают одноразовые тени manual-членов.
/// </summary>
public class PerformanceTests
{
    private const int ObjectCount = 1000;

    private static Replicator CreateReplicator()
    {
        return new Replicator(new ComplexTypeIds(), logger: MemorySink.CreateLogger(out _));
    }

    [Fact]
    public void UnchangedDeltaPass_Over1000RootObjects_DoesNotAllocate()
    {
        var mutator = new WorldMutator(new Random(40));
        Replicator replicator = CreateReplicator();
        var baselines = new ReplicationBaseline[ObjectCount];
        for (int i = 0; i < ObjectCount; i++)
        {
            Unit unit = mutator.NewUnit();
            // У части объектов есть таблица пометок manual-членов, но новых пометок нет.
            if (i % 2 == 0)
            {
                ManualReplication.MarkDirty(unit, nameof(Unit.Score));
            }

            baselines[i] = replicator.CreateBaseline(unit);
        }

        var writer = new BitWriter(1 << 16);
        foreach (ReplicationBaseline baseline in baselines)
        {
            writer.Reset();
            Assert.True(replicator.TryWriteDelta(baseline, writer));
        }

        // Прогрев путей "ничего не изменилось" (JIT, ленивые поля).
        writer.Reset();
        foreach (ReplicationBaseline baseline in baselines)
        {
            Assert.False(replicator.TryWriteDelta(baseline, writer));
            Assert.False(replicator.TryWriteDelta(baseline, out _));
        }

        AllocationAssert.DoesNotAllocate(() =>
        {
            for (int pass = 0; pass < 10; pass++)
            {
                foreach (ReplicationBaseline baseline in baselines)
                {
                    if (replicator.TryWriteDelta(baseline, writer) | replicator.TryWriteDelta(baseline, out _))
                    {
                        Assert.Fail("an unchanged object produced a delta");
                    }
                }
            }
        });
        Assert.Equal(0, writer.BitPosition);
    }

    [Fact]
    public void UnchangedDeltaPass_OverWorldWith1000NestedObjects_DoesNotAllocate()
    {
        var mutator = new WorldMutator(new Random(41));
        Replicator replicator = CreateReplicator();
        World world = mutator.CreateWorld();
        for (int i = 0; i < ObjectCount; i++)
        {
            world.Units.Add(mutator.NewUnit());
        }

        world.MainZone.Residents.Add(world.Units[10]);
        ReplicationBaseline baseline = replicator.CreateBaseline(world);
        var writer = new BitWriter(1 << 20);
        Assert.True(replicator.TryWriteDelta(baseline, writer));
        writer.Reset();
        Assert.False(replicator.TryWriteDelta(baseline, writer));

        AllocationAssert.DoesNotAllocate(() =>
        {
            for (int pass = 0; pass < 10; pass++)
            {
                Assert.False(replicator.TryWriteDelta(baseline, writer));
            }
        });
    }
}
