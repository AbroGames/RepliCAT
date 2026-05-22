using RepliCAT;
using RepliCAT.Bits;

namespace RepliCAT.Tests.Hardening;

/// <summary>
/// Фазз-тест: случайные, обрезанные и испорченные данные применяются к объектам сложной модели.
/// Допустимы только успех и <see cref="ReplicationFormatException"/>; зависаний и больших аллокаций нет.
/// </summary>
public class FuzzTests
{
    // Ограничения фазз-репликатора: память получателя пропорциональна MaxCollectionCount
    // (массивы слотов списка), поэтому для проверки "нет больших аллокаций" лимит уменьшен.
    private const int MaxCollectionCount = 1024;
    private const long MaxAllocationPerApply = 1 << 20;

    private static Replicator CreateReplicator()
    {
        var replicator = new Replicator(new ComplexTypeIds(),
            limits: new ReplicationLimits { MaxCollectionCount = MaxCollectionCount, MaxDepth = 32 },
            logger: MemorySink.CreateLogger(out _));

        // Прогрев: построение моделей и JIT не должны попадать в замер аллокаций одного Apply.
        List<byte[]> payloads = CollectValidPayloads(replicator, 1, 50);
        var client = new World();
        foreach (byte[] payload in payloads)
        {
            replicator.Apply(client, payload);
        }

        return replicator;
    }

    private sealed class FuzzStats
    {
        public int Applied;
        public int Rejected;
        public long MaxAllocated;
    }

    /// <summary>
    /// Применяет данные и проверяет, что допустимо только <see cref="ReplicationFormatException"/>.
    /// </summary>
    private static void ApplyChecked(Replicator replicator, object target, ReadOnlySpan<byte> data, FuzzStats stats,
        string description)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        try
        {
            replicator.Apply(target, data);
            stats.Applied++;
        }
        catch (ReplicationFormatException)
        {
            stats.Rejected++;
        }
        catch (Exception e)
        {
            Assert.Fail($"{description}: unexpected {e.GetType().Name}: {e}\ndata: {Convert.ToHexString(data)}");
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        stats.MaxAllocated = Math.Max(stats.MaxAllocated, allocated);
        if (allocated > MaxAllocationPerApply)
        {
            Assert.Fail($"{description}: allocated {allocated} bytes\ndata: {Convert.ToHexString(data)}");
        }
    }

    /// <summary>
    /// Собирает корректные дельты и снимки сквозного сценария.
    /// </summary>
    private static List<byte[]> CollectValidPayloads(Replicator replicator, int seed, int frames)
    {
        var random = new Random(seed);
        var mutator = new WorldMutator(random);
        World server = mutator.CreateWorld();
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var payloads = new List<byte[]>();

        for (int frame = 0; frame < frames; frame++)
        {
            int mutations = random.Next(1, 8);
            for (int i = 0; i < mutations; i++)
            {
                mutator.Mutate(server);
            }

            if (replicator.TryWriteDelta(baseline, out byte[] delta))
            {
                payloads.Add(delta);
            }

            if (frame % 10 == 0 && replicator.TryWriteSnapshot(baseline, out byte[] snapshot))
            {
                payloads.Add(snapshot);
            }
        }

        return payloads;
    }

    [Fact]
    public void RandomBytes_OnlyFormatExceptions()
    {
        var random = new Random(10);
        Replicator replicator = CreateReplicator();
        var stats = new FuzzStats();
        var buffer = new byte[96];

        for (int i = 0; i < 20000; i++)
        {
            int length = random.Next(buffer.Length + 1);
            Span<byte> data = buffer.AsSpan(0, length);
            random.NextBytes(data);
            // Первый бит маски почти всегда включен, чтобы данные доходили до вложенных узлов.
            if (length > 0 && random.Next(4) != 0)
            {
                data[0] |= 0xFF;
            }

            ApplyChecked(replicator, new World(), data, stats, $"random #{i}");
        }

        Assert.True(stats.Rejected > 0);
    }

    [Fact]
    public void TruncatedPayloads_OnlyFormatExceptions()
    {
        Replicator replicator = CreateReplicator();
        var stats = new FuzzStats();
        List<byte[]> payloads = CollectValidPayloads(replicator, 20, 300);

        foreach (byte[] payload in payloads)
        {
            // Дельта не самодостаточна (например, порядок списка без его элементов), поэтому даже полный пакет
            // на новом объекте может дать только ошибку формата.
            ApplyChecked(replicator, new World(), payload, stats, "full payload");

            for (int length = 0; length < payload.Length; length++)
            {
                ApplyChecked(replicator, new World(), payload.AsSpan(0, length), stats, $"prefix {length}/{payload.Length}");
            }
        }

        Assert.True(stats.Rejected > 0);
    }

    [Fact]
    public void CorruptedPayloads_OnlyFormatExceptions()
    {
        var random = new Random(30);
        Replicator replicator = CreateReplicator();
        var stats = new FuzzStats();
        List<byte[]> payloads = CollectValidPayloads(replicator, 31, 300);
        var client = new World();

        foreach (byte[] payload in payloads)
        {
            for (int attempt = 0; attempt < 20; attempt++)
            {
                byte[] corrupted = (byte[])payload.Clone();
                int flips = random.Next(1, 4);
                for (int i = 0; i < flips; i++)
                {
                    int bit = random.Next(corrupted.Length * 8);
                    corrupted[bit / 8] ^= (byte)(1 << (bit % 8));
                }

                if (random.Next(5) == 0)
                {
                    // Случайный хвост поверх корректного начала.
                    int start = random.Next(corrupted.Length);
                    random.NextBytes(corrupted.AsSpan(start));
                }

                ApplyChecked(replicator, new World(), corrupted, stats, "corrupted, fresh target");
                // Тот же мусор поверх уже заполненного клиента: коллекции и вложенные объекты переиспользуются.
                ApplyChecked(replicator, client, corrupted, stats, "corrupted, populated target");
            }

            // Корректный пакет по-прежнему применяется к клиенту, пережившему мусор.
            ApplyChecked(replicator, client, payload, stats, "valid after corrupted");
        }

        Assert.True(stats.Rejected > 0);
        Assert.True(stats.Applied > 0);
    }

    [Fact]
    public void PolymorphicTypeIds_FromData_AreFormatErrors()
    {
        Replicator replicator = CreateReplicator();

        byte[] LeaderWithTypeId(ulong typeId)
        {
            var writer = new BitWriter();
            // Маска World (7 членов, порядок по имени): Leader, MainZone, Motd, Name, Tick, Units, Zones.
            writer.WriteBits(0b0000001, 7);
            writer.WriteBool(true); // present
            writer.WriteBool(false); // isDeclaredType
            writer.WriteVarUInt(typeId);
            writer.WriteBits(0, 15); // пустая маска Hero (13 членов Unit + 2 члена Hero)
            return writer.ToArray();
        }

        // Корректные идентификаторы
        var world = new World();
        replicator.Apply(world, LeaderWithTypeId(ComplexTypeIds.HeroId));
        Assert.IsType<Hero>(world.Leader);

        // id 0 — отображение возвращает null
        Assert.Throws<ReplicationFormatException>(() => replicator.Apply(new World(), LeaderWithTypeId(0)));
        // Неизвестный id — отображение бросает ArgumentOutOfRangeException, а не KeyNotFoundException
        var e = Assert.Throws<ReplicationFormatException>(() => replicator.Apply(new World(), LeaderWithTypeId(99)));
        Assert.IsType<ArgumentOutOfRangeException>(e.InnerException?.InnerException ?? e.InnerException);
        // id указывает на ReplicatedList<int>, который нельзя сделать моделью
        Assert.Throws<ReplicationFormatException>(() =>
            replicator.Apply(new World(), LeaderWithTypeId(ComplexTypeIds.ListId)));
        // id указывает на тип, модель которого не строится (член-массив)
        Assert.Throws<ReplicationFormatException>(() =>
            replicator.Apply(new World(), LeaderWithTypeId(ComplexTypeIds.BrokenId)));
        // id указывает на тип, не совместимый с объявленным
        Assert.Throws<ReplicationFormatException>(() =>
            replicator.Apply(new World(), LeaderWithTypeId(ComplexTypeIds.ZoneId)));
    }
}
