using System.Text;
using RepliCAT.Bits;
using RepliCAT.Codecs;
using RepliCAT.Model;

namespace RepliCAT.Nodes;

/// <summary>
/// Узел <see cref="ReplicatedSet{T}"/>. Множество хранится как словарь элементов с пустыми значениями,
/// поэтому узел передает работу <see cref="DictionaryNode{TKey, TValue}"/> с кодеком значения нулевой ширины.
/// Полезная нагрузка совпадает с форматом словаря без значений:
/// <code>
/// [1 бит present]
/// [1 бит reset]
/// если !reset: удаления ([1][элемент])* [0]
/// вставки: ([1][элемент])* [0]
/// </code>
/// Политика чтения (повторы, <c>null</c>, лимиты) и уровень вложенности — те же, что у словаря.
/// </summary>
/// <typeparam name="T">Тип элементов</typeparam>
internal sealed class SetNode<T> : ReplicationNode<ReplicatedSet<T>>
{
    private readonly ReplicationNode _itemNode;
    private readonly DictionaryNode<T, NoValue> _dictionaryNode;

    /// <summary>
    /// Создает узел множества.
    /// </summary>
    /// <param name="context">Контекст репликатора</param>
    /// <param name="itemNode">Узел элементов (используются его кодек и описание для хэша схемы)</param>
    /// <param name="path">Путь к члену для сообщений</param>
    public SetNode(ReplicationContext context, ValueNode<T> itemNode, string path)
    {
        _itemNode = itemNode;
        var valueNode = new ValueNode<NoValue>(NoValueCodec.Instance, "none");
        _dictionaryNode = new DictionaryNode<T, NoValue>(context, itemNode, valueNode, path, "set");
    }

    /// <inheritdoc/>
    public override bool RequiresSetter => false;

    /// <inheritdoc/>
    public override bool HasInnerState => true;

    /// <inheritdoc/>
    public override Shadow CreateShadow()
    {
        return _dictionaryNode.CreateShadow();
    }

    /// <inheritdoc/>
    public override void AppendSchema(StringBuilder sb, Dictionary<Type, int> visited)
    {
        sb.Append("set(");
        _itemNode.AppendSchema(sb, visited);
        sb.Append(')');
    }

    /// <inheritdoc/>
    public override bool WriteDelta(ReplicatedSet<T> current, Shadow shadow, BitWriter writer, bool forceAll)
    {
        // Множество владеет своим словарем, поэтому ссылка на словарь в тени однозначно задает множество.
        return _dictionaryNode.WriteDelta(current?.Dictionary, shadow, writer, forceAll);
    }

    /// <inheritdoc/>
    public override void WriteShadow(Shadow shadow, BitWriter writer)
    {
        _dictionaryNode.WriteShadow(shadow, writer);
    }

    /// <inheritdoc/>
    public override ReplicatedSet<T> Read(ReplicatedSet<T> existing, ref BitReader reader)
    {
        ReplicatedDictionary<T, NoValue> dictionary = _dictionaryNode.Read(existing?.Dictionary, ref reader);
        if (dictionary == null)
        {
            return null;
        }

        // Узел словаря переиспользует переданный словарь, поэтому существующее множество остается прежним.
        return existing ?? new ReplicatedSet<T>(dictionary);
    }
}

/// <summary>
/// Пустое значение словаря, на котором построено <see cref="ReplicatedSet{T}"/>.
/// </summary>
internal readonly struct NoValue
{
}

/// <summary>
/// Кодек <see cref="NoValue"/>: ничего не пишет и не читает, значение никогда не меняется.
/// </summary>
internal sealed class NoValueCodec : IReplicationCodec<NoValue>
{
    public static readonly NoValueCodec Instance = new();

    private NoValueCodec()
    {
    }

    /// <inheritdoc/>
    public void Write(BitWriter writer, NoValue value)
    {
    }

    /// <inheritdoc/>
    public NoValue Read(ref BitReader reader)
    {
        return default;
    }

    /// <inheritdoc/>
    public bool IsChanged(NoValue lastSent, NoValue current)
    {
        return false;
    }
}
