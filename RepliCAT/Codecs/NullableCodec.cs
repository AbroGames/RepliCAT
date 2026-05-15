using RepliCAT.Bits;

namespace RepliCAT.Codecs;

/// <summary>
/// Кодек <see cref="Nullable{T}"/>: бит наличия значения, затем значение через внутренний кодек.
/// </summary>
/// <typeparam name="T">Тип внутреннего значения</typeparam>
public sealed class NullableCodec<T> : IReplicationCodec<T?> where T : struct
{
    private readonly IReplicationCodec<T> _inner;

    /// <summary>
    /// Создает кодек поверх кодека внутреннего значения.
    /// </summary>
    /// <param name="inner">Кодек внутреннего значения</param>
    public NullableCodec(IReplicationCodec<T> inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    /// <summary>
    /// Кодек внутреннего значения.
    /// </summary>
    public IReplicationCodec<T> Inner => _inner;

    /// <inheritdoc/>
    public void Write(BitWriter writer, T? value)
    {
        writer.WriteBool(value.HasValue);
        if (value.HasValue)
        {
            _inner.Write(writer, value.GetValueOrDefault());
        }
    }

    /// <inheritdoc/>
    public T? Read(ref BitReader reader)
    {
        if (!reader.ReadBool())
        {
            return null;
        }

        return _inner.Read(ref reader);
    }

    /// <inheritdoc/>
    public bool IsChanged(T? lastSent, T? current)
    {
        if (lastSent.HasValue != current.HasValue)
        {
            return true;
        }

        return lastSent.HasValue && _inner.IsChanged(lastSent.GetValueOrDefault(), current.GetValueOrDefault());
    }
}
