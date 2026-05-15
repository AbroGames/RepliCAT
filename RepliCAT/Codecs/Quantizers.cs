using System.Globalization;
using RepliCAT.Bits;
using Serilog;

namespace RepliCAT.Codecs;

/// <summary>
/// Квантователь одного числового компонента: переводит значение в целый шаг и обратно,
/// пишет и читает шаг в битовом потоке.<br/>
/// Значения вне допустимого диапазона и NaN прижимаются, а предупреждение в лог пишется
/// только один раз на экземпляр (то есть на член), чтобы не засорять лог каждый кадр.
/// Экземпляр создается на каждый член; квантователь не выделяет память на горячем пути.
/// </summary>
public abstract class Quantizer
{
    private readonly ILogger _logger;
    private readonly string _memberPath;
    private int _outOfRangeReported;

    /// <summary>
    /// Создает квантователь.
    /// </summary>
    /// <param name="logger">Логгер для предупреждений о прижатых значениях; <c>null</c> — не логировать</param>
    /// <param name="memberPath">Путь к члену для сообщений (<c>DeclaringType.FullName.MemberName</c>)</param>
    protected Quantizer(ILogger logger, string memberPath)
    {
        _logger = logger;
        _memberPath = memberPath;
    }

    /// <summary>
    /// Путь к члену, для которого создан квантователь.
    /// </summary>
    public string MemberPath => _memberPath;

    /// <summary>
    /// <c>true</c>, если хотя бы одно значение уже было прижато (и предупреждение уже записано).
    /// </summary>
    public bool HasReportedOutOfRange => Volatile.Read(ref _outOfRangeReported) != 0;

    /// <summary>
    /// Переводит значение в шаг квантования. Значения вне диапазона и NaN прижимаются,
    /// при первом таком значении в лог пишется предупреждение.
    /// </summary>
    /// <param name="value">Значение</param>
    /// <returns>Шаг</returns>
    public abstract long Quantize(double value);

    /// <summary>
    /// Переводит шаг квантования обратно в значение.
    /// </summary>
    /// <param name="step">Шаг</param>
    /// <returns>Значение</returns>
    public abstract double Dequantize(long step);

    /// <summary>
    /// Записывает шаг в поток.
    /// </summary>
    /// <param name="writer">Писатель</param>
    /// <param name="step">Шаг, полученный из <see cref="Quantize"/></param>
    public abstract void WriteStep(BitWriter writer, long step);

    /// <summary>
    /// Читает шаг из потока. Шаг вне допустимого диапазона — <see cref="ReplicationFormatException"/>.
    /// </summary>
    /// <param name="reader">Читатель</param>
    /// <returns>Шаг</returns>
    public abstract long ReadStep(ref BitReader reader);

    /// <summary>
    /// Описание допустимого диапазона для сообщений.
    /// </summary>
    protected abstract string DescribeRange();

    /// <summary>
    /// Сообщает о прижатом значении. Предупреждение пишется только при первом вызове.
    /// </summary>
    /// <param name="value">Исходное значение</param>
    protected void ReportOutOfRange(double value)
    {
        if (Volatile.Read(ref _outOfRangeReported) != 0 || Interlocked.Exchange(ref _outOfRangeReported, 1) != 0)
        {
            return;
        }

        _logger?.Warning(
            "Replication: value {Value} of member {Member} is out of the quantization range {Range} and was clamped. " +
            "Further warnings for this member are suppressed.",
            value, _memberPath, DescribeRange());
    }
}

/// <summary>
/// Квантование с границами (<c>[Quantize(min, max, precision)]</c>).<br/>
/// Число шагов <c>steps = round((max - min) / precision)</c>, на проводе шаг занимает
/// <c>bitLength(steps)</c> бит (возможно 0). Фактический размер шага — <c>(max - min) / steps</c>,
/// поэтому <c>min</c> и <c>max</c> представимы точно. Значения вне диапазона прижимаются, NaN становится <c>min</c>.
/// </summary>
public sealed class BoundedQuantizer : Quantizer
{
    /// <summary>
    /// Максимально допустимое число шагов (2^62).
    /// </summary>
    public const long MaxSteps = 1L << 62;

    private readonly double _min;
    private readonly double _max;
    private readonly double _stepSize;
    private readonly long _steps;
    private readonly int _bitCount;

    /// <summary>
    /// Создает квантователь с границами.
    /// </summary>
    /// <param name="min">Минимум диапазона</param>
    /// <param name="max">Максимум диапазона, больше <paramref name="min"/></param>
    /// <param name="precision">Желаемый шаг, больше нуля</param>
    /// <param name="logger">Логгер для предупреждений; <c>null</c> — не логировать</param>
    /// <param name="memberPath">Путь к члену для сообщений</param>
    /// <exception cref="ArgumentException">Некорректные параметры</exception>
    public BoundedQuantizer(double min, double max, double precision, ILogger logger = null, string memberPath = null)
        : base(logger, memberPath)
    {
        string error = Validate(min, max, precision);
        if (error != null)
        {
            throw new ArgumentException(error);
        }

        _min = min;
        _max = max;
        _steps = ComputeSteps(min, max, precision);
        _stepSize = _steps == 0 ? 0 : (max - min) / _steps;
        _bitCount = 64 - System.Numerics.BitOperations.LeadingZeroCount((ulong)_steps);
    }

    /// <summary>
    /// Минимум диапазона.
    /// </summary>
    public double Min => _min;

    /// <summary>
    /// Максимум диапазона.
    /// </summary>
    public double Max => _max;

    /// <summary>
    /// Число шагов; допустимые шаги на проводе — от 0 до <see cref="Steps"/> включительно.
    /// </summary>
    public long Steps => _steps;

    /// <summary>
    /// Фактический размер шага, <c>(max - min) / steps</c> (0, если шагов 0).
    /// </summary>
    public double StepSize => _stepSize;

    /// <summary>
    /// Число бит одного шага на проводе.
    /// </summary>
    public int BitCount => _bitCount;

    /// <inheritdoc/>
    public override long Quantize(double value)
    {
        if (double.IsNaN(value) || value < _min)
        {
            ReportOutOfRange(value);
            return 0;
        }

        if (value > _max)
        {
            ReportOutOfRange(value);
            return _steps;
        }

        if (_steps == 0)
        {
            return 0;
        }

        double step = Math.Round((value - _min) / _stepSize, MidpointRounding.AwayFromZero);
        if (step <= 0)
        {
            return 0;
        }

        return step >= _steps ? _steps : (long)step;
    }

    /// <inheritdoc/>
    public override double Dequantize(long step)
    {
        if (step <= 0)
        {
            return _min;
        }

        if (step >= _steps)
        {
            return _max;
        }

        double value = _min + step * _stepSize;
        return value > _max ? _max : value;
    }

    /// <inheritdoc/>
    public override void WriteStep(BitWriter writer, long step)
    {
        if ((ulong)step > (ulong)_steps)
        {
            throw new ArgumentOutOfRangeException(nameof(step), step, $"Step must be in range 0..{_steps}.");
        }

        writer.WriteBits((ulong)step, _bitCount);
    }

    /// <inheritdoc/>
    public override long ReadStep(ref BitReader reader)
    {
        ulong step = reader.ReadBits(_bitCount);
        if (step > (ulong)_steps)
        {
            throw new ReplicationFormatException($"Quantized step {step} is above the maximum {_steps}.");
        }

        return (long)step;
    }

    /// <inheritdoc/>
    protected override string DescribeRange()
    {
        return string.Create(CultureInfo.InvariantCulture, $"[{_min}, {_max}]");
    }

    /// <summary>
    /// Проверяет параметры. Возвращает текст ошибки или <c>null</c>, если параметры корректны.
    /// </summary>
    internal static string Validate(double min, double max, double precision)
    {
        if (!double.IsFinite(precision) || precision <= 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"Quantize precision must be a finite number greater than 0, got {precision}.");
        }

        if (!double.IsFinite(min) || !double.IsFinite(max))
        {
            return string.Create(CultureInfo.InvariantCulture, $"Quantize min and max must be finite, got [{min}, {max}].");
        }

        if (max <= min)
        {
            return string.Create(CultureInfo.InvariantCulture, $"Quantize max must be greater than min, got [{min}, {max}].");
        }

        double steps = Math.Round((max - min) / precision, MidpointRounding.AwayFromZero);
        if (!double.IsFinite(steps) || steps > MaxSteps)
        {
            return string.Create(CultureInfo.InvariantCulture, $"Quantize range [{min}, {max}] with precision {precision} has more than 2^62 steps.");
        }

        return null;
    }

    private static long ComputeSteps(double min, double max, double precision)
    {
        return (long)Math.Round((max - min) / precision, MidpointRounding.AwayFromZero);
    }
}

/// <summary>
/// Квантование без границ (<c>[Quantize(precision)]</c>).<br/>
/// Шаг <c>round(v / precision)</c> пишется как zigzag varint, размер зависит от величины значения.
/// Шаги по модулю больше 2^62 и NaN прижимаются (NaN становится 0).
/// </summary>
public sealed class UnboundedQuantizer : Quantizer
{
    /// <summary>
    /// Максимально допустимый модуль шага (2^62).
    /// </summary>
    public const long MaxAbsStep = 1L << 62;

    private readonly double _precision;

    /// <summary>
    /// Создает квантователь без границ.
    /// </summary>
    /// <param name="precision">Шаг, больше нуля</param>
    /// <param name="logger">Логгер для предупреждений; <c>null</c> — не логировать</param>
    /// <param name="memberPath">Путь к члену для сообщений</param>
    /// <exception cref="ArgumentException">Некорректный шаг</exception>
    public UnboundedQuantizer(double precision, ILogger logger = null, string memberPath = null)
        : base(logger, memberPath)
    {
        string error = Validate(precision);
        if (error != null)
        {
            throw new ArgumentException(error);
        }

        _precision = precision;
    }

    /// <summary>
    /// Шаг квантования.
    /// </summary>
    public double Precision => _precision;

    /// <inheritdoc/>
    public override long Quantize(double value)
    {
        double step = Math.Round(value / _precision, MidpointRounding.AwayFromZero);
        if (double.IsNaN(step))
        {
            ReportOutOfRange(value);
            return 0;
        }

        if (step > MaxAbsStep)
        {
            ReportOutOfRange(value);
            return MaxAbsStep;
        }

        if (step < -MaxAbsStep)
        {
            ReportOutOfRange(value);
            return -MaxAbsStep;
        }

        return (long)step;
    }

    /// <inheritdoc/>
    public override double Dequantize(long step)
    {
        return step * _precision;
    }

    /// <inheritdoc/>
    public override void WriteStep(BitWriter writer, long step)
    {
        if (step > MaxAbsStep || step < -MaxAbsStep)
        {
            throw new ArgumentOutOfRangeException(nameof(step), step, "Step magnitude must not exceed 2^62.");
        }

        writer.WriteVarInt(step);
    }

    /// <inheritdoc/>
    public override long ReadStep(ref BitReader reader)
    {
        long step = reader.ReadVarInt();
        if (step > MaxAbsStep || step < -MaxAbsStep)
        {
            throw new ReplicationFormatException($"Quantized step {step} exceeds 2^62 in magnitude.");
        }

        return step;
    }

    /// <inheritdoc/>
    protected override string DescribeRange()
    {
        return string.Create(CultureInfo.InvariantCulture, $"[{-MaxAbsStep * _precision}, {MaxAbsStep * _precision}]");
    }

    /// <summary>
    /// Проверяет шаг. Возвращает текст ошибки или <c>null</c>, если шаг корректен.
    /// </summary>
    internal static string Validate(double precision)
    {
        if (!double.IsFinite(precision) || precision <= 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"Quantize precision must be a finite number greater than 0, got {precision}.");
        }

        return null;
    }
}
