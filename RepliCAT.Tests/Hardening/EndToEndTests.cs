using RepliCAT;
using RepliCAT.Bits;

namespace RepliCAT.Tests.Hardening;

/// <summary>
/// Сквозной рандомизированный тест: сервер со сложным графом, основной клиент с первого кадра
/// и поздние клиенты, подключающиеся по снимку в случайный момент кадра (в том числе посреди изменений).
/// После каждого кадра каждый клиент равен серверу с точностью квантования и допуска, а клиенты равны
/// друг другу точно.
/// </summary>
public class EndToEndTests
{
    private const int MaxLateClients = 4;
    private const ulong PacketHeader = 7;

    private sealed class Client
    {
        public readonly World World = new();
        public readonly int JoinedFrame;

        public Client(int joinedFrame)
        {
            JoinedFrame = joinedFrame;
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void RandomizedFrames_AllClientsMatchServer(int seed)
    {
        var random = new Random(seed);
        var mutator = new WorldMutator(random);
        var replicator = new Replicator(new ComplexTypeIds(), logger: MemorySink.CreateLogger(out MemorySink sink));

        World server = mutator.CreateWorld();
        ReplicationBaseline baseline = replicator.CreateBaseline(server);
        var main = new Client(0);
        var lateClients = new List<Client>();
        var batch = new BitWriter();
        int snapshots = 0;

        const int frames = 1500;
        for (int frame = 0; frame < frames; frame++)
        {
            int mutations = random.Next(0, 8);
            for (int i = 0; i < mutations; i++)
            {
                mutator.Mutate(server);
                if (random.Next(40) == 0)
                {
                    snapshots += TryJoin(replicator, baseline, lateClients, frame, random) ? 1 : 0;
                }
            }

            if (random.Next(30) == 0)
            {
                snapshots += TryJoin(replicator, baseline, lateClients, frame, random) ? 1 : 0;
            }

            // Конец кадра: одна дельта для всех клиентов. Иногда через пакетную запись в общий писатель
            // после служебного заголовка пакета.
            byte[] delta;
            bool batched = random.Next(4) == 0;
            if (batched)
            {
                batch.Reset();
                batch.WriteVarUInt(PacketHeader);
                delta = replicator.TryWriteDelta(baseline, batch) ? batch.ToArray() : null;
            }
            else
            {
                replicator.TryWriteDelta(baseline, out delta);
            }

            if (delta != null)
            {
                Apply(replicator, main, delta, batched);
                foreach (Client client in lateClients)
                {
                    Apply(replicator, client, delta, batched);
                }
            }

            // Повторная дельта без изменений пуста.
            Assert.False(replicator.TryWriteDelta(baseline, out _), $"frame {frame}: a repeated delta is not empty");

            AssertMatches(server, main, frame, true);
            foreach (Client client in lateClients)
            {
                AssertMatches(server, client, frame, true);
                AssertMatches(main.World, client, frame, false);
            }
        }

        Assert.True(snapshots > 20, $"only {snapshots} late clients joined");
        // Значения держатся в диапазоне квантования, предупреждений о выходе за границы нет.
        Assert.Equal(0, sink.Count);
    }

    private static void Apply(Replicator replicator, Client client, byte[] data, bool batched)
    {
        if (!batched)
        {
            replicator.Apply(client.World, data);
            return;
        }

        var reader = new BitReader(data);
        Assert.Equal(PacketHeader, reader.ReadVarUInt());
        replicator.Apply(client.World, ref reader);
        Assert.True(reader.RemainingBits < 8);
    }

    private static bool TryJoin(Replicator replicator, ReplicationBaseline baseline, List<Client> clients, int frame,
        Random random)
    {
        if (!replicator.TryWriteSnapshot(baseline, out byte[] snapshot))
        {
            return false;
        }

        if (clients.Count == MaxLateClients)
        {
            clients.RemoveAt(random.Next(clients.Count));
        }

        var client = new Client(frame);
        replicator.Apply(client.World, snapshot);
        clients.Add(client);
        return true;
    }

    private static void AssertMatches(World expected, Client client, int frame, bool approximate)
    {
        string diff = ComplexComparer.Diff(expected, client.World, approximate);
        if (diff != null)
        {
            Assert.Fail($"frame {frame}, client joined at frame {client.JoinedFrame}, " +
                        $"{(approximate ? "vs server" : "vs main client")}: {diff}");
        }
    }
}
