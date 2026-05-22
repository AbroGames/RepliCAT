using System.Globalization;
using System.Text;
using Godot;
using RepliCAT;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace RepliCAT.Tests.Hardening;

// Сложная модель для сквозного, фазз- и нагрузочного тестов: вложенные объекты, полиморфизм,
// списки объектов со словарями внутри, квантованные значения, допуск и manual-члены.
// Все члены присваиваемые и у всех типов есть конструкторы без параметров, поэтому любые входные данные
// могут дать только ReplicationFormatException (конфигурационных ReplicationException здесь нет).

public enum Stance
{
    Idle,
    Moving,
    Attacking,
    Stunned,
    Dead
}

public class Stats
{
    [Replicated] public int Hp;
    [Replicated, Quantize(0, 100, 0.5)] public float Armor;
}

public sealed class Buff
{
    [Replicated] public int Kind;
    [Replicated, Quantize(0, 60, 0.1)] public double Remaining;
    [Replicated] public string Source;
}

public class Unit
{
    [Replicated] public int Id;
    [Replicated, Quantize(-512, 512, 0.01)] public Vector2 Position;
    [Replicated, Quantize(0.001)] public float Heading;
    [Replicated(Tolerance = 0.25)] public float Energy;
    [Replicated] public Stance Stance;
    [Replicated] public int? TargetId;
    [Replicated] public Stats Stats;
    [Replicated] public ReplicatedDictionary<string, int> Inventory = new();
    [Replicated] public ReplicatedDictionary<int, Buff> Buffs = new();
    [Replicated, Quantize(-100, 100, 0.1)] public ReplicatedList<float> Path = new();
    [Replicated(Manual = true)] public int Score;
    [Replicated(Manual = true)] public Stats ManualStats;
    [Replicated(Manual = true)] public ReplicatedList<int> ManualTags = new();
}

public class Hero : Unit
{
    [Replicated] public string Title;
    [Replicated] public ReplicatedList<ReplicatedList<int>> Grid = new();
}

public class Zone
{
    [Replicated] public string Name;
    [Replicated] public Color Tint;
    [Replicated] public ReplicatedList<Unit> Residents = new();
    [Replicated] public Zone Child;
}

public class World
{
    [Replicated] public int Tick;
    [Replicated] public string Name;
    [Replicated(Manual = true)] public string Motd;
    [Replicated] public Zone MainZone;
    [Replicated] public ReplicatedList<Unit> Units = new();
    [Replicated] public ReplicatedDictionary<int, Zone> Zones = new();
    [Replicated] public Unit Leader;
}

/// <summary>
/// Тип, для которого нельзя построить модель (член-массив). Нужен, чтобы проверить, что идентификатор типа
/// из данных, указывающий на такой тип, дает ошибку формата.
/// </summary>
public class BrokenUnit : Unit
{
    [Replicated] public int[] Bad;
}

/// <summary>
/// Тестовое отображение типов. Неизвестный идентификатор дает <see cref="ArgumentOutOfRangeException"/>,
/// а не <see cref="KeyNotFoundException"/>, как у реализаций на основе списка.
/// </summary>
public sealed class ComplexTypeIds : ITypeIdMapping
{
    public const int UnitId = 1;
    public const int HeroId = 2;
    public const int ListId = 3;
    public const int BrokenId = 4;
    public const int ZoneId = 5;

    private readonly Type[] _types =
    [
        null,
        typeof(Unit),
        typeof(Hero),
        typeof(ReplicatedList<int>),
        typeof(BrokenUnit),
        typeof(Zone)
    ];

    public int GetId(Type type)
    {
        int index = Array.IndexOf(_types, type);
        if (index <= 0)
        {
            throw new KeyNotFoundException(type.FullName);
        }

        return index;
    }

    public Type GetType(int id)
    {
        // Индексация массива: неизвестный id — IndexOutOfRangeException, id 0 — null.
        if (id >= _types.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown type id.");
        }

        return _types[id];
    }
}

/// <summary>
/// Приемник логов в памяти (тесты проверяют записи в лог, не трогая глобальный логгер Serilog).
/// </summary>
public sealed class MemorySink : ILogEventSink
{
    private int _count;

    public int Count => Volatile.Read(ref _count);

    public void Emit(LogEvent logEvent)
    {
        Interlocked.Increment(ref _count);
    }

    public static ILogger CreateLogger(out MemorySink sink)
    {
        sink = new MemorySink();
        return new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
    }
}

/// <summary>
/// Структурное сравнение графов модели. В приближенном режиме квантованные члены и член с допуском
/// сравниваются с погрешностью своего кодека (сервер против клиента), в точном — побитово (клиент против клиента).<br/>
/// Погрешности: у квантованного члена клиент хранит деквантованный шаг текущего значения сервера (изменение
/// внутри шага не отправляется, потому что сравниваются шаги), поэтому ошибка не больше половины шага
/// (у всех границ модели диапазон делится на precision нацело, так что шаг равен precision); у члена с допуском
/// клиент хранит последнее отправленное значение, а неотправленное изменение не больше допуска.
/// <see cref="Epsilon"/> покрывает округление float.
/// </summary>
public static class ComplexComparer
{
    private const double Epsilon = 1e-4;

    public static string Diff(World expected, World actual, bool approximate)
    {
        var context = new DiffContext(approximate);
        context.World(expected, actual, "World");
        return context.Result;
    }

    private sealed class DiffContext
    {
        private readonly bool _approximate;
        private readonly StringBuilder _result = new();

        public DiffContext(bool approximate)
        {
            _approximate = approximate;
        }

        public string Result => _result.Length == 0 ? null : _result.ToString();

        private bool Failed => _result.Length > 0;

        private void Fail(string path, object expected, object actual)
        {
            if (!Failed)
            {
                _result.Append(path).Append(": expected ").Append(Format(expected)).Append(", actual ")
                    .Append(Format(actual));
            }
        }

        private static string Format(object value)
        {
            return value switch
            {
                null => "null",
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString()
            };
        }

        private bool Presence(object expected, object actual, string path)
        {
            if (expected == null || actual == null)
            {
                if (expected != null || actual != null)
                {
                    Fail(path, expected, actual);
                }

                return false;
            }

            if (expected.GetType() != actual.GetType())
            {
                Fail(path + ".GetType()", expected.GetType().Name, actual.GetType().Name);
                return false;
            }

            return true;
        }

        private void Exact<T>(T expected, T actual, string path)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                Fail(path, expected, actual);
            }
        }

        private void Near(double expected, double actual, double tolerance, string path)
        {
            if (_approximate ? Math.Abs(expected - actual) > tolerance + Epsilon : !expected.Equals(actual))
            {
                Fail(path, expected, actual);
            }
        }

        public void World(World expected, World actual, string path)
        {
            Exact(expected.Tick, actual.Tick, path + ".Tick");
            Exact(expected.Name, actual.Name, path + ".Name");
            Exact(expected.Motd, actual.Motd, path + ".Motd");
            Zone(expected.MainZone, actual.MainZone, path + ".MainZone");
            UnitList(expected.Units, actual.Units, path + ".Units");
            if (Presence(expected.Zones, actual.Zones, path + ".Zones"))
            {
                Exact(expected.Zones.Count, actual.Zones.Count, path + ".Zones.Count");
                foreach ((int key, Zone zone) in expected.Zones)
                {
                    if (!actual.Zones.TryGetValue(key, out Zone other))
                    {
                        Fail(path + ".Zones[" + key + "]", "present", "missing");
                        return;
                    }

                    Zone(zone, other, path + ".Zones[" + key + "]");
                }
            }

            Unit(expected.Leader, actual.Leader, path + ".Leader");
        }

        private void Zone(Zone expected, Zone actual, string path)
        {
            if (Failed || !Presence(expected, actual, path))
            {
                return;
            }

            Exact(expected.Name, actual.Name, path + ".Name");
            Exact(expected.Tint, actual.Tint, path + ".Tint");
            UnitList(expected.Residents, actual.Residents, path + ".Residents");
            Zone(expected.Child, actual.Child, path + ".Child");
        }

        private void UnitList(ReplicatedList<Unit> expected, ReplicatedList<Unit> actual, string path)
        {
            if (Failed || !Presence(expected, actual, path))
            {
                return;
            }

            Exact(expected.Count, actual.Count, path + ".Count");
            for (int i = 0; i < expected.Count && !Failed; i++)
            {
                Unit(expected[i], actual[i], path + "[" + i + "]");
            }
        }

        private void Stats(Stats expected, Stats actual, string path)
        {
            if (Failed || !Presence(expected, actual, path))
            {
                return;
            }

            Exact(expected.Hp, actual.Hp, path + ".Hp");
            Near(expected.Armor, actual.Armor, 0.25, path + ".Armor");
        }

        private void Unit(Unit expected, Unit actual, string path)
        {
            if (Failed || !Presence(expected, actual, path))
            {
                return;
            }

            Exact(expected.Id, actual.Id, path + ".Id");
            Near(expected.Position.X, actual.Position.X, 0.005, path + ".Position.X");
            Near(expected.Position.Y, actual.Position.Y, 0.005, path + ".Position.Y");
            Near(expected.Heading, actual.Heading, 0.0005, path + ".Heading");
            Near(expected.Energy, actual.Energy, 0.25, path + ".Energy");
            Exact(expected.Stance, actual.Stance, path + ".Stance");
            Exact(expected.TargetId, actual.TargetId, path + ".TargetId");
            Stats(expected.Stats, actual.Stats, path + ".Stats");

            if (Presence(expected.Inventory, actual.Inventory, path + ".Inventory"))
            {
                Exact(expected.Inventory.Count, actual.Inventory.Count, path + ".Inventory.Count");
                foreach ((string key, int value) in expected.Inventory)
                {
                    if (!actual.Inventory.TryGetValue(key, out int other))
                    {
                        Fail(path + ".Inventory[" + key + "]", "present", "missing");
                        return;
                    }

                    Exact(value, other, path + ".Inventory[" + key + "]");
                }
            }

            if (Presence(expected.Buffs, actual.Buffs, path + ".Buffs"))
            {
                Exact(expected.Buffs.Count, actual.Buffs.Count, path + ".Buffs.Count");
                foreach ((int key, Buff buff) in expected.Buffs)
                {
                    string buffPath = path + ".Buffs[" + key + "]";
                    actual.Buffs.TryGetValue(key, out Buff other);
                    if (Presence(buff, other, buffPath))
                    {
                        Exact(buff.Kind, other.Kind, buffPath + ".Kind");
                        Near(buff.Remaining, other.Remaining, 0.05, buffPath + ".Remaining");
                        Exact(buff.Source, other.Source, buffPath + ".Source");
                    }
                }
            }

            if (Presence(expected.Path, actual.Path, path + ".Path"))
            {
                Exact(expected.Path.Count, actual.Path.Count, path + ".Path.Count");
                for (int i = 0; i < expected.Path.Count && !Failed; i++)
                {
                    Near(expected.Path[i], actual.Path[i], 0.05, path + ".Path[" + i + "]");
                }
            }

            Exact(expected.Score, actual.Score, path + ".Score");
            Stats(expected.ManualStats, actual.ManualStats, path + ".ManualStats");
            IntList(expected.ManualTags, actual.ManualTags, path + ".ManualTags");

            if (expected is Hero hero && actual is Hero otherHero)
            {
                Exact(hero.Title, otherHero.Title, path + ".Title");
                if (Presence(hero.Grid, otherHero.Grid, path + ".Grid"))
                {
                    Exact(hero.Grid.Count, otherHero.Grid.Count, path + ".Grid.Count");
                    for (int i = 0; i < hero.Grid.Count && !Failed; i++)
                    {
                        IntList(hero.Grid[i], otherHero.Grid[i], path + ".Grid[" + i + "]");
                    }
                }
            }
        }

        private void IntList(ReplicatedList<int> expected, ReplicatedList<int> actual, string path)
        {
            if (Failed || !Presence(expected, actual, path))
            {
                return;
            }

            Exact(expected.Count, actual.Count, path + ".Count");
            for (int i = 0; i < expected.Count && !Failed; i++)
            {
                Exact(expected[i], actual[i], path + "[" + i + "]");
            }
        }
    }
}
