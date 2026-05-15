using System.Globalization;
using Serilog;

namespace RepliCAT.Codecs;

/// <summary>
/// Применение параметров члена (квантование, допуск сравнения) к базовому кодеку значения.
/// </summary>
public static class CodecOptions
{
    /// <summary>
    /// Возвращает кодек с учетом квантования и допуска.
    /// Без квантования и с нулевым допуском возвращается <paramref name="baseCodec"/> без изменений.
    /// С квантованием базовый кодек не используется: значение пишется через <see cref="QuantizedCodec{T}"/>.
    /// Допуск больше нуля оборачивает результат в <see cref="ToleranceCodec{T}"/>.
    /// </summary>
    /// <param name="baseCodec">Базовый кодек типа</param>
    /// <param name="quantize">Параметры квантования или <c>null</c></param>
    /// <param name="tolerance">Допуск сравнения, 0 — точное сравнение</param>
    /// <param name="memberPath">Путь к члену для сообщений (<c>DeclaringType.FullName.MemberName</c>)</param>
    /// <param name="logger">Логгер для предупреждений о прижатых значениях; <c>null</c> — не логировать</param>
    /// <typeparam name="T">Тип значения</typeparam>
    /// <returns>Итоговый кодек</returns>
    /// <exception cref="ReplicationException">Тип не поддерживает квантование или допуск, либо параметры некорректны</exception>
    public static IReplicationCodec<T> Apply<T>(IReplicationCodec<T> baseCodec, QuantizeAttribute quantize, double tolerance,
        string memberPath, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(baseCodec);

        if (double.IsNaN(tolerance) || double.IsInfinity(tolerance) || tolerance < 0)
        {
            throw new ReplicationException(string.Create(CultureInfo.InvariantCulture,
                $"{memberPath}: Tolerance must be a finite non-negative number, got {tolerance}."));
        }

        if (quantize == null && tolerance == 0)
        {
            return baseCodec;
        }

        if (!ComponentAdapter.TryGet(out ComponentAdapter<T> adapter))
        {
            string what = quantize != null ? "Quantize" : "Tolerance";
            throw new ReplicationException(
                $"{memberPath}: {what} is not supported for type {typeof(T).FullName}. " +
                "Supported types: float, double, Vector2, Vector3, Vector4, Quaternion, Color, sbyte, byte, short, ushort, int, uint, long.");
        }

        IReplicationCodec<T> codec = baseCodec;
        if (quantize != null)
        {
            Quantizer quantizer;
            if (quantize.IsBounded)
            {
                string error = BoundedQuantizer.Validate(quantize.Min, quantize.Max, quantize.Precision);
                if (error != null)
                {
                    throw new ReplicationException($"{memberPath}: {error}");
                }

                quantizer = new BoundedQuantizer(quantize.Min, quantize.Max, quantize.Precision, logger, memberPath);
            }
            else
            {
                string error = UnboundedQuantizer.Validate(quantize.Precision);
                if (error != null)
                {
                    throw new ReplicationException($"{memberPath}: {error}");
                }

                quantizer = new UnboundedQuantizer(quantize.Precision, logger, memberPath);
            }

            codec = new QuantizedCodec<T>(adapter, quantizer);
        }

        if (tolerance > 0)
        {
            codec = new ToleranceCodec<T>(codec, adapter, tolerance);
        }

        return codec;
    }
}
