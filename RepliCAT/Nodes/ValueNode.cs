using System.Text;
using RepliCAT.Bits;
using RepliCAT.Codecs;

namespace RepliCAT.Nodes;

/// <summary>
/// Узел значения: оборачивает <see cref="IReplicationCodec{T}"/>.
/// Тень хранит последнее отправленное <b>сырое</b> значение (не деквантованное), поэтому
/// квантованные кодеки и кодеки с допуском сравнивают сырые значения, а снимок перекодирует
/// то же сырое значение тем же кодеком и дает те же шаги.<br/>
/// Для ссылочных типов значений (например, <see cref="string"/>) тень хранит ссылку,
/// поэтому такие значения должны быть неизменяемыми.
/// </summary>
/// <typeparam name="T">Тип значения</typeparam>
internal sealed class ValueNode<T> : ReplicationNode<T>
{
    private readonly IReplicationCodec<T> _codec;
    private readonly string _schema;

    /// <summary>
    /// Создает узел значения.
    /// </summary>
    /// <param name="codec">Итоговый кодек члена (с учетом квантования и допуска), один на член</param>
    /// <param name="schema">Каноническое описание кодека и параметров для хэша схемы</param>
    public ValueNode(IReplicationCodec<T> codec, string schema)
    {
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        _schema = schema;
    }

    /// <summary>
    /// Кодек узла.
    /// </summary>
    public IReplicationCodec<T> Codec => _codec;

    /// <inheritdoc/>
    public override bool RequiresSetter => true;

    /// <inheritdoc/>
    public override bool HasInnerState => false;

    /// <inheritdoc/>
    public override Shadow CreateShadow()
    {
        return new ValueShadow();
    }

    /// <inheritdoc/>
    public override void AppendSchema(StringBuilder sb, Dictionary<Type, int> visited)
    {
        sb.Append("value(").Append(_schema).Append(')');
    }

    /// <inheritdoc/>
    public override bool WriteDelta(T current, Shadow shadow, BitWriter writer, bool forceAll)
    {
        var valueShadow = (ValueShadow)shadow;
        if (!forceAll && valueShadow.HasValue && !_codec.IsChanged(valueShadow.LastSent, current))
        {
            return false;
        }

        _codec.Write(writer, current);
        valueShadow.LastSent = current;
        valueShadow.HasValue = true;
        return true;
    }

    /// <inheritdoc/>
    public override void WriteShadow(Shadow shadow, BitWriter writer)
    {
        var valueShadow = (ValueShadow)shadow;
        if (!valueShadow.HasValue)
        {
            throw new InvalidOperationException("Value shadow has never been written.");
        }

        _codec.Write(writer, valueShadow.LastSent);
    }

    /// <inheritdoc/>
    public override T Read(T existing, ref BitReader reader)
    {
        return _codec.Read(ref reader);
    }

    private sealed class ValueShadow : Shadow
    {
        public T LastSent;
        public bool HasValue;
    }
}
