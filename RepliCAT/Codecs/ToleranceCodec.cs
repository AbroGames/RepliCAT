using RepliCAT.Bits;

namespace RepliCAT.Codecs;

/// <summary>
/// Декоратор кодека с допуском сравнения (<c>[Replicated(Tolerance = x)]</c>).
/// Запись и чтение делегируются внутреннему кодеку.<br/>
/// Значение считается изменившимся, если его считает изменившимся внутренний кодек
/// <b>и</b> максимальная по компонентам разница с последним отправленным значением больше допуска.
/// Если разница — NaN, используется только сравнение внутреннего кодека (точное: NaN равен NaN и отличается от числа).
/// Поэтому в сочетании с квантованием должны выполняться оба условия.
/// </summary>
/// <typeparam name="T">Тип значения</typeparam>
public sealed class ToleranceCodec<T> : IReplicationCodec<T>
{
    private readonly IReplicationCodec<T> _inner;
    private readonly ComponentAdapter<T> _adapter;
    private readonly double _tolerance;

    /// <summary>
    /// Создает декоратор с допуском.
    /// </summary>
    /// <param name="inner">Внутренний кодек</param>
    /// <param name="adapter">Адаптер компонентов</param>
    /// <param name="tolerance">Допуск, конечное неотрицательное число</param>
    public ToleranceCodec(IReplicationCodec<T> inner, ComponentAdapter<T> adapter, double tolerance)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        if (!double.IsFinite(tolerance) || tolerance < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tolerance), tolerance, "Tolerance must be a finite non-negative number.");
        }

        _tolerance = tolerance;
    }

    /// <summary>
    /// Внутренний кодек.
    /// </summary>
    public IReplicationCodec<T> Inner => _inner;

    /// <summary>
    /// Допуск.
    /// </summary>
    public double Tolerance => _tolerance;

    /// <inheritdoc/>
    public void Write(BitWriter writer, T value)
    {
        _inner.Write(writer, value);
    }

    /// <inheritdoc/>
    public T Read(ref BitReader reader)
    {
        return _inner.Read(ref reader);
    }

    /// <inheritdoc/>
    public bool IsChanged(T lastSent, T current)
    {
        if (!_inner.IsChanged(lastSent, current))
        {
            return false;
        }

        double delta = _adapter.MaxDelta(lastSent, current);
        return double.IsNaN(delta) || delta > _tolerance;
    }
}
