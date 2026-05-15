namespace RepliCAT;

/// <summary>
/// Квантование реплицируемого значения. Поддерживаются <see cref="float"/>, <see cref="double"/>,
/// <c>Vector2</c>, <c>Vector3</c>, <c>Vector4</c>, <c>Quaternion</c>, <c>Color</c> (покомпонентно)
/// и целые <see cref="sbyte"/>, <see cref="byte"/>, <see cref="short"/>, <see cref="ushort"/>,
/// <see cref="int"/>, <see cref="uint"/>, <see cref="long"/>.<br/>
/// Две формы:
/// <list type="bullet">
/// <item><c>[Quantize(precision)]</c> — без границ: <c>round(v / precision)</c> пишется как zigzag varint,
/// размер зависит от величины значения;</item>
/// <item><c>[Quantize(min, max, precision)]</c> — с границами: фиксированное число бит
/// <c>bitLength(round((max - min) / precision))</c>, значения вне диапазона и NaN прижимаются к границам.</item>
/// </list>
/// Изменение значения определяется по квантованным шагам: изменение меньше одного шага не отправляется.
/// Параметры проверяются при построении модели типа; некорректные параметры приводят к <see cref="ReplicationException"/>.
/// На члене-коллекции атрибут применяется к значениям элементов (у словаря — к значениям, не к ключам).
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public sealed class QuantizeAttribute : Attribute
{
    /// <summary>
    /// Квантование без границ: значение пишется как zigzag varint от <c>round(v / precision)</c>.
    /// </summary>
    /// <param name="precision">Шаг квантования, должен быть больше нуля</param>
    public QuantizeAttribute(double precision)
    {
        Precision = precision;
        Min = double.NaN;
        Max = double.NaN;
        IsBounded = false;
    }

    /// <summary>
    /// Квантование с границами: значение пишется фиксированным числом бит.
    /// </summary>
    /// <param name="min">Минимальное значение диапазона</param>
    /// <param name="max">Максимальное значение диапазона, должно быть больше <paramref name="min"/></param>
    /// <param name="precision">Желаемый шаг квантования, должен быть больше нуля</param>
    public QuantizeAttribute(double min, double max, double precision = 1)
    {
        Min = min;
        Max = max;
        Precision = precision;
        IsBounded = true;
    }

    /// <summary>
    /// Минимальное значение диапазона. Для квантования без границ — <see cref="double.NaN"/>.
    /// </summary>
    public double Min { get; }

    /// <summary>
    /// Максимальное значение диапазона. Для квантования без границ — <see cref="double.NaN"/>.
    /// </summary>
    public double Max { get; }

    /// <summary>
    /// Шаг квантования.
    /// </summary>
    public double Precision { get; }

    /// <summary>
    /// <c>true</c> для квантования с границами (<see cref="Min"/>, <see cref="Max"/>).
    /// </summary>
    public bool IsBounded { get; }
}
