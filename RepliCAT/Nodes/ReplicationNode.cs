using System.Text;
using RepliCAT.Bits;

namespace RepliCAT.Nodes;

/// <summary>
/// Базовая копия (shadow) одного реплицируемого значения: то, что было отправлено последним.
/// Конкретный тип тени определяет узел, который ее создал (<see cref="ReplicationNode.CreateShadow"/>).
/// </summary>
internal abstract class Shadow
{
}

/// <summary>
/// Узел репликации: обрабатывает одно реплицируемое значение (член объекта, элемент коллекции,
/// значение словаря). Узлы не хранят состояние конкретного объекта — оно живет в <see cref="Shadow"/>,
/// поэтому один узел обслуживает все экземпляры члена (и все элементы коллекции).<br/>
/// Узлы строятся один раз на член при построении модели типа и переиспользуются
/// для дельты, снимка и чтения.
/// </summary>
internal abstract class ReplicationNode
{
    /// <summary>
    /// Тип значения, которое обрабатывает узел.
    /// </summary>
    public abstract Type ValueType { get; }

    /// <summary>
    /// <c>true</c>, если при чтении значение всегда нужно присваивать члену (узлы значений).
    /// Такой член обязан иметь сеттер.
    /// </summary>
    public abstract bool RequiresSetter { get; }

    /// <summary>
    /// <c>true</c> для узлов объектов и коллекций: содержимое может измениться, пока ссылка остается прежней.
    /// Для таких узлов при чтении передается существующее значение члена, а сеттер вызывается,
    /// только если узел вернул другую ссылку.
    /// </summary>
    public abstract bool HasInnerState { get; }

    /// <summary>
    /// Создает пустую тень ("ничего еще не отправлено").
    /// </summary>
    public abstract Shadow CreateShadow();

    /// <summary>
    /// Дописывает каноническое описание узла для хэша схемы.
    /// </summary>
    /// <param name="sb">Построитель описания</param>
    /// <param name="visited">
    /// Уже описанные типы объектов и их порядковые номера в порядке обхода
    /// (повторное упоминание типа описывается ссылкой на номер, что работает и для рекурсивных типов)
    /// </param>
    public abstract void AppendSchema(StringBuilder sb, Dictionary<Type, int> visited);
}

/// <summary>
/// Типизированный узел репликации.
/// </summary>
/// <typeparam name="T">Тип значения</typeparam>
internal abstract class ReplicationNode<T> : ReplicationNode
{
    /// <inheritdoc/>
    public sealed override Type ValueType => typeof(T);

    /// <summary>
    /// Сравнивает текущее значение с тенью, пишет полезную нагрузку и обновляет тень.
    /// Если ничего не изменилось и <paramref name="forceAll"/> == <c>false</c>, возвращает <c>false</c>
    /// и оставляет писатель в исходной позиции.
    /// </summary>
    /// <param name="current">Текущее (живое) значение</param>
    /// <param name="shadow">Тень, созданная этим узлом</param>
    /// <param name="writer">Писатель</param>
    /// <param name="forceAll">Записать все, независимо от тени</param>
    /// <returns><c>true</c>, если что-то записано</returns>
    public abstract bool WriteDelta(T current, Shadow shadow, BitWriter writer, bool forceAll);

    /// <summary>
    /// Путь снимка: пишет содержимое тени как полную (forceAll) полезную нагрузку.
    /// Живое состояние не читается (кроме manual-членов).
    /// </summary>
    /// <param name="shadow">Тень, в которую уже что-то было записано</param>
    /// <param name="writer">Писатель</param>
    public abstract void WriteShadow(Shadow shadow, BitWriter writer);

    /// <summary>
    /// Путь получателя: читает полезную нагрузку. Узлы объектов и коллекций возвращают
    /// <paramref name="existing"/>, если его можно переиспользовать.
    /// </summary>
    /// <param name="existing">Текущее значение члена на получателе (для узлов значений не используется)</param>
    /// <param name="reader">Читатель</param>
    /// <returns>Новое значение члена</returns>
    public abstract T Read(T existing, ref BitReader reader);
}
