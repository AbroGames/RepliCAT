using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace RepliCAT;

/// <summary>
/// Пометка manual-членов (<see cref="ReplicatedAttribute.Manual"/>) как измененных.<br/>
/// Пометка увеличивает версию члена у конкретного объекта. Каждая базовая копия, через которую объект
/// реплицируется, помнит версию, отправленную последней, и отправляет член в ближайшей дельте, если версия
/// изменилась. Поэтому пометку никто не "потребляет", порядок вызовов не важен, а объект, достижимый
/// через несколько базовых копий или путей, отправляется по каждому из них.<br/>
/// Версии хранятся в <see cref="ConditionalWeakTable{TKey,TValue}"/> и не продлевают жизнь объектов.
/// Методы потокобезопасны: помечать можно из любого потока, в том числе во время записи дельты
/// (такая пометка попадет в текущую или в следующую дельту).
/// </summary>
public static class ManualReplication
{
    private static readonly ConditionalWeakTable<object, ManualVersionTable> Tables = new();

    /// <summary>
    /// Помечает manual-член объекта как измененный: член будет отправлен в следующей дельте.
    /// Имя, которому не соответствует ни один manual-член, ни на что не влияет.<br/>
    /// Пометка члена объекта, который находится внутри manual-поддерева (manual-объекта или manual-коллекции),
    /// не отправляется, пока не помечен сам manual-член, содержащий поддерево.
    /// </summary>
    /// <param name="owner">Объект, объявляющий член (экземпляр класса)</param>
    /// <param name="memberName">
    /// Имя члена, обычно <c>nameof(Member)</c>. Для поля автосвойства (<c>[field: Replicated(Manual = true)]</c>)
    /// — имя свойства
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="owner"/> или <paramref name="memberName"/> равен <c>null</c></exception>
    /// <exception cref="ArgumentException"><paramref name="owner"/> — упакованная структура</exception>
    public static void MarkDirty(object owner, string memberName)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(memberName);
        if (owner.GetType().IsValueType)
        {
            throw new ArgumentException("Only class instances can own replicated members.", nameof(owner));
        }

        Tables.GetOrCreateValue(owner).Increment(memberName);
    }

    /// <summary>
    /// Возвращает таблицу версий объекта или <c>null</c>, если его члены ни разу не помечались.
    /// Не выделяет память.
    /// </summary>
    internal static ManualVersionTable GetTable(object owner)
    {
        return Tables.TryGetValue(owner, out ManualVersionTable table) ? table : null;
    }
}

/// <summary>
/// Версии manual-членов одного объекта: имя члена → число вызовов <see cref="ManualReplication.MarkDirty"/>.
/// <see cref="Stamp"/> увеличивается при каждой пометке любого члена, поэтому запись дельты, у которой
/// сохраненная отметка совпадает с текущей, пропускает поиск версий по именам.
/// </summary>
internal sealed class ManualVersionTable
{
    private readonly Dictionary<string, int> _versions = new(StringComparer.Ordinal);
    private int _stamp;

    /// <summary>
    /// Общая версия таблицы. Читается до версий членов: пометка сначала увеличивает версию члена,
    /// потом отметку, поэтому пометка, пропущенная при чтении версий, обязательно изменит отметку
    /// относительно прочитанной.
    /// </summary>
    public int Stamp => Volatile.Read(ref _stamp);

    /// <summary>
    /// Увеличивает версию члена и общую версию таблицы.
    /// </summary>
    public void Increment(string memberName)
    {
        lock (_versions)
        {
            CollectionsMarshal.GetValueRefOrAddDefault(_versions, memberName, out _)++;
            Volatile.Write(ref _stamp, _stamp + 1);
        }
    }

    /// <summary>
    /// Возвращает версию члена (0, если член ни разу не помечался). Не выделяет память.
    /// </summary>
    public int GetVersion(string memberName)
    {
        lock (_versions)
        {
            return _versions.TryGetValue(memberName, out int version) ? version : 0;
        }
    }
}
