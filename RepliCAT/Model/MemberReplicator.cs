using System.Reflection;
using System.Runtime.CompilerServices;
using RepliCAT.Bits;
using RepliCAT.Nodes;

namespace RepliCAT.Model;

/// <summary>
/// Реплицируемый член модели типа: связывает поле или свойство владельца с узлом репликации.
/// Владелец передается как <see cref="object"/>, но модель гарантирует, что он имеет нужный тип,
/// поэтому внутри используется приведение без проверки.
/// </summary>
internal abstract class MemberReplicator
{
    protected MemberReplicator(MemberInfo member, string path)
    {
        Member = member;
        Name = member.Name;
        Path = path;
    }

    /// <summary>
    /// Поле или свойство.
    /// </summary>
    public MemberInfo Member { get; }

    /// <summary>
    /// Имя члена.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Путь к члену для сообщений: <c>DeclaringType.FullName.MemberName</c>.
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// Узел, обрабатывающий значение члена.
    /// </summary>
    public abstract ReplicationNode Node { get; }

    /// <summary>
    /// Создает пустую тень члена.
    /// </summary>
    public Shadow CreateShadow()
    {
        return Node.CreateShadow();
    }

    /// <summary>
    /// Пишет дельту значения члена (см. <see cref="ReplicationNode{T}.WriteDelta"/>).
    /// </summary>
    public abstract bool WriteDelta(object owner, Shadow shadow, BitWriter writer, bool forceAll);

    /// <summary>
    /// Пишет содержимое тени члена (путь снимка). Владелец нужен только manual-членам (шаг 9).
    /// </summary>
    public abstract void WriteShadow(object owner, Shadow shadow, BitWriter writer);

    /// <summary>
    /// Читает значение члена и присваивает его владельцу по правилам присваивания.
    /// </summary>
    public abstract void Read(object owner, ref BitReader reader);
}

/// <summary>
/// Типизированный реплицируемый член. <typeparamref name="TValue"/> в точности равен типу члена,
/// поэтому доступ к значимым типам не упаковывает значения.
/// </summary>
/// <typeparam name="TOwner">Тип, объявляющий член</typeparam>
/// <typeparam name="TValue">Тип члена</typeparam>
internal sealed class MemberReplicator<TOwner, TValue> : MemberReplicator where TOwner : class
{
    private readonly Func<TOwner, TValue> _getter;
    private readonly Action<TOwner, TValue> _setter;
    private readonly ReplicationNode<TValue> _node;
    private readonly bool _alwaysSet;

    /// <summary>
    /// Создает член.
    /// </summary>
    /// <param name="member">Поле или свойство</param>
    /// <param name="path">Путь к члену для сообщений</param>
    /// <param name="getter">Геттер</param>
    /// <param name="setter">Сеттер или <c>null</c>, если член недоступен для записи</param>
    /// <param name="node">Узел значения</param>
    public MemberReplicator(MemberInfo member, string path, Func<TOwner, TValue> getter, Action<TOwner, TValue> setter,
        ReplicationNode<TValue> node) : base(member, path)
    {
        _getter = getter;
        _setter = setter;
        _node = node;
        _alwaysSet = !node.HasInnerState;
    }

    /// <inheritdoc/>
    public override ReplicationNode Node => _node;

    /// <inheritdoc/>
    public override bool WriteDelta(object owner, Shadow shadow, BitWriter writer, bool forceAll)
    {
        return _node.WriteDelta(_getter(Unsafe.As<TOwner>(owner)), shadow, writer, forceAll);
    }

    /// <inheritdoc/>
    public override void WriteShadow(object owner, Shadow shadow, BitWriter writer)
    {
        _node.WriteShadow(shadow, writer);
    }

    /// <inheritdoc/>
    public override void Read(object owner, ref BitReader reader)
    {
        TOwner typedOwner = Unsafe.As<TOwner>(owner);

        if (_alwaysSet)
        {
            // Узел значения: существующее значение не нужно, присваиваем всегда.
            TValue value = _node.Read(default, ref reader);
            SetValue(typedOwner, value);
            return;
        }

        // Узел объекта или коллекции: переиспользуем существующий экземпляр,
        // сеттер нужен только если узел вернул другую ссылку.
        TValue existing = _getter(typedOwner);
        TValue result = _node.Read(existing, ref reader);
        if (!ReferenceEquals(existing, result))
        {
            SetValue(typedOwner, result);
        }
    }

    private void SetValue(TOwner owner, TValue value)
    {
        if (_setter == null)
        {
            throw ReplicationTypeModel.TagPath(new ReplicationException(
                $"{Path}: the member has no setter, but the received value requires assigning a new instance."), Path);
        }

        _setter(owner, value);
    }
}
