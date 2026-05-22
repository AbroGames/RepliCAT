using Godot;
using RepliCAT;

namespace RepliCAT.Tests.Hardening;

/// <summary>
/// Случайные изменения серверного графа <see cref="World"/>. Каждое изменение manual-члена сразу помечается
/// через <see cref="ManualReplication.MarkDirty"/>, поэтому к концу кадра все клиенты должны совпадать с сервером.
/// Граф остается ациклическим (<see cref="Zone.Child"/> получает только новые зоны), но объекты разделяются:
/// один <see cref="Unit"/> может лежать в нескольких списках и быть лидером.
/// </summary>
public sealed class WorldMutator
{
    private const int MaxZoneDepth = 3;

    private readonly Random _random;
    private int _nextId;

    public WorldMutator(Random random)
    {
        _random = random;
    }

    public World CreateWorld()
    {
        var world = new World { Name = "world", Motd = "hello" };
        for (int i = 0; i < 4; i++)
        {
            world.Units.Add(NewUnit());
        }

        world.Zones.Add(1, NewZone(0));
        world.MainZone = world.Zones[1];
        world.Leader = world.Units[0];
        return world;
    }

    public void Mutate(World world)
    {
        switch (_random.Next(12))
        {
            case 0:
                world.Tick++;
                if (_random.Next(4) == 0)
                {
                    world.Name = RandomString();
                }

                break;
            case 1:
                world.Motd = _random.Next(5) == 0 ? null : RandomString();
                ManualReplication.MarkDirty(world, nameof(World.Motd));
                break;
            case 2:
                world.MainZone = _random.Next(4) switch
                {
                    0 => null,
                    1 => NewZone(0),
                    _ => AnyZone(world)
                };
                break;
            case 3:
            case 4:
                MutateUnitList(world, world.Units);
                break;
            case 5:
            case 6:
            case 7:
                if (AnyUnit(world) is { } unit)
                {
                    MutateUnit(unit);
                }

                break;
            case 8:
                MutateZones(world);
                break;
            case 9:
                if (AnyZone(world) is { } zone)
                {
                    MutateZone(world, zone, 0);
                }

                break;
            case 10:
                world.Leader = _random.Next(3) switch
                {
                    0 => null,
                    1 => NewUnit(),
                    _ => AnyUnit(world)
                };
                break;
            default:
                if (_random.Next(40) == 0)
                {
                    // Редкая замена экземпляра коллекции: сброс на проводе, клиент сохраняет свой экземпляр.
                    world.Units = new ReplicatedList<Unit>(world.Units);
                }
                else if (world.Leader != null)
                {
                    MutateUnit(world.Leader);
                }

                break;
        }
    }

    private void MutateUnitList(World world, ReplicatedList<Unit> list)
    {
        switch (_random.Next(8))
        {
            case 0:
            case 1:
                list.Add(NewUnit());
                break;
            case 2:
                if (list.Count > 0)
                {
                    list.RemoveAt(_random.Next(list.Count));
                }

                break;
            case 3:
                list.Insert(_random.Next(list.Count + 1), _random.Next(3) == 0 && AnyUnit(world) is { } shared
                    ? shared
                    : NewUnit());
                break;
            case 4:
                if (list.Count > 0)
                {
                    list[_random.Next(list.Count)] = NewUnit();
                }

                break;
            case 5:
                list.Sort((a, b) => a.Id.CompareTo(b.Id) != 0 ? a.Id.CompareTo(b.Id) : a.Energy.CompareTo(b.Energy));
                break;
            case 6:
                if (list.Count > 0)
                {
                    // Удаление и добавление в одном кадре: слот переиспользуется.
                    Unit unit = list[_random.Next(list.Count)];
                    list.Remove(unit);
                    list.Add(_random.Next(2) == 0 ? unit : NewUnit());
                }

                break;
            default:
                if (_random.Next(10) == 0)
                {
                    list.Clear();
                }
                else if (list.Count > 12)
                {
                    list.RemoveAt(0);
                }

                break;
        }
    }

    private void MutateUnit(Unit unit)
    {
        switch (_random.Next(14))
        {
            case 0:
                unit.Position = new Vector2(RandomFloat(-500, 500), RandomFloat(-500, 500));
                break;
            case 1:
                // Медленный дрейф ниже кванта, который со временем пересекает границу шага.
                unit.Position += new Vector2(RandomFloat(-0.004f, 0.004f), RandomFloat(-0.004f, 0.004f));
                unit.Heading += RandomFloat(-0.0004f, 0.0004f);
                break;
            case 2:
                unit.Heading = RandomFloat(-10, 10);
                break;
            case 3:
                unit.Energy += RandomFloat(-0.2f, 0.2f);
                if (_random.Next(5) == 0)
                {
                    unit.Energy = RandomFloat(0, 100);
                }

                break;
            case 4:
                unit.Stance = (Stance)_random.Next(5);
                unit.TargetId = _random.Next(3) == 0 ? null : _random.Next(100);
                break;
            case 5:
                unit.Stats = _random.Next(3) switch
                {
                    0 => null,
                    1 => new Stats { Hp = _random.Next(100), Armor = RandomFloat(0, 100) },
                    _ => unit.Stats
                };
                if (unit.Stats != null)
                {
                    unit.Stats.Hp += _random.Next(-5, 6);
                    unit.Stats.Armor = RandomFloat(0, 100);
                }

                break;
            case 6:
                MutateInventory(unit.Inventory);
                break;
            case 7:
            case 8:
                MutateBuffs(unit.Buffs);
                break;
            case 9:
                MutatePath(unit.Path);
                break;
            case 10:
                unit.Score += _random.Next(1, 10);
                ManualReplication.MarkDirty(unit, nameof(Unit.Score));
                break;
            case 11:
                // Manual-поддерево: изменение внутри ManualStats помечается на владельце.
                if (unit.ManualStats == null || _random.Next(4) == 0)
                {
                    unit.ManualStats = _random.Next(3) == 0 ? null : new Stats();
                }

                if (unit.ManualStats != null)
                {
                    unit.ManualStats.Hp = _random.Next(1000);
                    unit.ManualStats.Armor = RandomFloat(0, 100);
                }

                ManualReplication.MarkDirty(unit, nameof(Unit.ManualStats));
                break;
            case 12:
                if (unit.ManualTags.Count > 0 && _random.Next(2) == 0)
                {
                    unit.ManualTags.RemoveAt(_random.Next(unit.ManualTags.Count));
                }
                else
                {
                    unit.ManualTags.Insert(_random.Next(unit.ManualTags.Count + 1), _random.Next(1000));
                }

                ManualReplication.MarkDirty(unit, nameof(Unit.ManualTags));
                break;
            default:
                if (unit is Hero hero)
                {
                    MutateHero(hero);
                }
                else
                {
                    unit.Id = _random.Next(1000);
                }

                break;
        }
    }

    private void MutateHero(Hero hero)
    {
        switch (_random.Next(4))
        {
            case 0:
                hero.Title = _random.Next(4) == 0 ? null : RandomString();
                break;
            case 1:
                hero.Grid.Add(new ReplicatedList<int> { _random.Next(10), _random.Next(10) });
                break;
            case 2:
                if (hero.Grid.Count > 0)
                {
                    ReplicatedList<int> row = hero.Grid[_random.Next(hero.Grid.Count)];
                    if (row.Count > 0 && _random.Next(2) == 0)
                    {
                        row[_random.Next(row.Count)] = _random.Next(100);
                    }
                    else
                    {
                        row.Add(_random.Next(100));
                    }
                }

                break;
            default:
                if (hero.Grid.Count > 0)
                {
                    hero.Grid.RemoveAt(_random.Next(hero.Grid.Count));
                }

                break;
        }
    }

    private void MutateInventory(ReplicatedDictionary<string, int> inventory)
    {
        string key = "item" + _random.Next(6);
        switch (_random.Next(4))
        {
            case 0:
                inventory.Remove(key);
                break;
            case 1:
                if (_random.Next(10) == 0)
                {
                    inventory.Clear();
                }

                break;
            default:
                inventory[key] = _random.Next(100);
                break;
        }
    }

    private void MutateBuffs(ReplicatedDictionary<int, Buff> buffs)
    {
        int key = _random.Next(5);
        switch (_random.Next(5))
        {
            case 0:
                buffs.Remove(key);
                break;
            case 1:
                buffs[key] = NewBuff();
                break;
            case 2:
                if (buffs.Remove(key, out Buff removed))
                {
                    // Удаление и повторное добавление в одном кадре (тот же или другой объект).
                    buffs[key] = _random.Next(2) == 0 ? removed : NewBuff();
                }

                break;
            default:
                if (buffs.TryGetValue(key, out Buff buff))
                {
                    buff.Remaining = Math.Max(0, buff.Remaining - _random.NextDouble() * 5);
                    if (_random.Next(3) == 0)
                    {
                        buff.Source = RandomString();
                    }
                }
                else
                {
                    buffs.Add(key, NewBuff());
                }

                break;
        }
    }

    private void MutatePath(ReplicatedList<float> path)
    {
        switch (_random.Next(4))
        {
            case 0:
                path.Add(RandomFloat(-100, 100));
                break;
            case 1:
                if (path.Count > 0)
                {
                    path.RemoveAt(_random.Next(path.Count));
                }

                break;
            case 2:
                if (path.Count > 0)
                {
                    path[_random.Next(path.Count)] = RandomFloat(-100, 100);
                }

                break;
            default:
                path.Sort();
                break;
        }
    }

    private void MutateZones(World world)
    {
        int key = _random.Next(1, 6);
        switch (_random.Next(4))
        {
            case 0:
                world.Zones.Remove(key);
                break;
            case 1:
                world.Zones[key] = NewZone(0);
                break;
            case 2:
                if (world.MainZone != null)
                {
                    // Разделяемая зона: один объект в словаре и в MainZone.
                    world.Zones[key] = world.MainZone;
                }

                break;
            default:
                if (_random.Next(15) == 0)
                {
                    world.Zones.Clear();
                }

                break;
        }
    }

    private void MutateZone(World world, Zone zone, int depth)
    {
        switch (_random.Next(6))
        {
            case 0:
                zone.Name = RandomString();
                break;
            case 1:
                zone.Tint = new Color(RandomFloat(0, 1), RandomFloat(0, 1), RandomFloat(0, 1), RandomFloat(0, 1));
                break;
            case 2:
                MutateUnitList(world, zone.Residents);
                break;
            case 3:
                if (zone.Residents.Count > 0)
                {
                    MutateUnit(zone.Residents[_random.Next(zone.Residents.Count)]);
                }
                else if (AnyUnit(world) is { } shared)
                {
                    zone.Residents.Add(shared);
                }

                break;
            case 4:
                zone.Child = _random.Next(3) == 0 || depth >= MaxZoneDepth ? null : NewZone(depth + 1);
                break;
            default:
                if (zone.Child != null && depth < MaxZoneDepth)
                {
                    MutateZone(world, zone.Child, depth + 1);
                }

                break;
        }
    }

    private Unit AnyUnit(World world)
    {
        if (world.Units.Count > 0 && _random.Next(4) != 0)
        {
            return world.Units[_random.Next(world.Units.Count)];
        }

        Zone zone = AnyZone(world);
        if (zone != null && zone.Residents.Count > 0)
        {
            return zone.Residents[_random.Next(zone.Residents.Count)];
        }

        return world.Leader;
    }

    private Zone AnyZone(World world)
    {
        if (world.Zones.Count == 0 || _random.Next(5) == 0)
        {
            return world.MainZone;
        }

        int index = _random.Next(world.Zones.Count);
        foreach (Zone zone in world.Zones.Values)
        {
            if (index-- == 0)
            {
                return zone;
            }
        }

        return null;
    }

    public Unit NewUnit()
    {
        Unit unit = _random.Next(3) == 0
            ? new Hero { Title = RandomString(), Grid = { new ReplicatedList<int> { 1, 2 } } }
            : new Unit();
        unit.Id = _nextId++;
        unit.Position = new Vector2(RandomFloat(-500, 500), RandomFloat(-500, 500));
        unit.Heading = RandomFloat(-10, 10);
        unit.Energy = RandomFloat(0, 100);
        unit.Stance = (Stance)_random.Next(5);
        unit.Stats = _random.Next(2) == 0 ? null : new Stats { Hp = _random.Next(100), Armor = RandomFloat(0, 100) };
        unit.Inventory["gold"] = _random.Next(1000);
        unit.Buffs[0] = NewBuff();
        unit.Path.Add(RandomFloat(-100, 100));
        unit.Score = _random.Next(100);
        unit.ManualStats = _random.Next(2) == 0 ? null : new Stats { Hp = 1 };
        unit.ManualTags.Add(_random.Next(10));
        return unit;
    }

    private Buff NewBuff()
    {
        return new Buff { Kind = _random.Next(8), Remaining = _random.NextDouble() * 60, Source = RandomString() };
    }

    private Zone NewZone(int depth)
    {
        var zone = new Zone
        {
            Name = RandomString(),
            Tint = new Color(RandomFloat(0, 1), RandomFloat(0, 1), RandomFloat(0, 1))
        };
        if (_random.Next(2) == 0)
        {
            zone.Residents.Add(NewUnit());
        }

        if (depth < MaxZoneDepth && _random.Next(3) == 0)
        {
            zone.Child = NewZone(depth + 1);
        }

        return zone;
    }

    private float RandomFloat(float min, float max)
    {
        return min + (float)_random.NextDouble() * (max - min);
    }

    private string RandomString()
    {
        return _random.Next(6) switch
        {
            0 => "",
            1 => "юникод-" + _random.Next(100),
            _ => "s" + _random.Next(1000)
        };
    }
}
