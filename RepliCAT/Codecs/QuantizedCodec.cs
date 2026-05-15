using RepliCAT.Bits;

namespace RepliCAT.Codecs;

/// <summary>
/// Кодек с квантованием: каждый компонент значения (<see cref="ComponentAdapter{T}"/>) переводится
/// в шаг квантователя (<see cref="Quantizer"/>) и пишется в поток.<br/>
/// <see cref="IsChanged"/> сравнивает квантованные шаги: изменение меньше одного шага не считается изменением,
/// а медленный дрейф отправляется, как только пересекает границу шага.<br/>
/// Чтение не выделяет память (компоненты собираются в буфере на стеке).
/// </summary>
/// <typeparam name="T">Тип значения</typeparam>
public sealed class QuantizedCodec<T> : IReplicationCodec<T>
{
    /// <summary>
    /// Максимальное число компонентов, поддерживаемое кодеком.
    /// </summary>
    public const int MaxComponents = 16;

    private readonly ComponentAdapter<T> _adapter;
    private readonly Quantizer _quantizer;
    private readonly int _count;

    /// <summary>
    /// Создает кодек с квантованием.
    /// </summary>
    /// <param name="adapter">Адаптер компонентов</param>
    /// <param name="quantizer">Квантователь (общий для всех компонентов)</param>
    public QuantizedCodec(ComponentAdapter<T> adapter, Quantizer quantizer)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _quantizer = quantizer ?? throw new ArgumentNullException(nameof(quantizer));
        _count = adapter.Count;
        if (_count < 1 || _count > MaxComponents)
        {
            throw new ArgumentException($"Component count must be in range 1..{MaxComponents}, got {_count}.", nameof(adapter));
        }
    }

    /// <summary>
    /// Адаптер компонентов.
    /// </summary>
    public ComponentAdapter<T> Adapter => _adapter;

    /// <summary>
    /// Квантователь.
    /// </summary>
    public Quantizer Quantizer => _quantizer;

    /// <inheritdoc/>
    public void Write(BitWriter writer, T value)
    {
        for (int i = 0; i < _count; i++)
        {
            _quantizer.WriteStep(writer, _quantizer.Quantize(_adapter.Get(value, i)));
        }
    }

    /// <inheritdoc/>
    public T Read(ref BitReader reader)
    {
        Span<double> components = stackalloc double[MaxComponents];
        for (int i = 0; i < _count; i++)
        {
            components[i] = _quantizer.Dequantize(_quantizer.ReadStep(ref reader));
        }

        return _adapter.Compose(components[.._count]);
    }

    /// <inheritdoc/>
    public bool IsChanged(T lastSent, T current)
    {
        for (int i = 0; i < _count; i++)
        {
            if (_quantizer.Quantize(_adapter.Get(lastSent, i)) != _quantizer.Quantize(_adapter.Get(current, i)))
            {
                return true;
            }
        }

        return false;
    }
}
