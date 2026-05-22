using RepliCAT.Bits;
using RepliCAT.Codecs;
using RepliCAT.Model;
using Serilog;

namespace RepliCAT;

/// <summary>
/// Дельта-репликация объектов по членам, помеченным <see cref="ReplicatedAttribute"/>.<br/>
/// Сервер: <see cref="CreateBaseline"/> для объекта, затем в конце каждого кадра <see cref="TryWriteDelta(ReplicationBaseline, out byte[])"/>;
/// новому клиенту в любой момент кадра — <see cref="TryWriteSnapshot(ReplicationBaseline, out byte[])"/>.
/// Клиент: <see cref="Apply(object, ReadOnlySpan{byte})"/> к существующему объекту (члены присваиваются, объект не пересоздается).<br/>
/// Контракты:
/// <list type="bullet">
/// <item>после того как <c>TryWriteDelta</c> вернул <c>true</c>, данные <b>обязаны</b> дойти до всех клиентов:
/// базовая копия уже обновлена. Транспорт должен быть надежным и упорядоченным;</item>
/// <item>если <c>TryWriteDelta</c> бросил исключение, писатель откатывается, а базовая копия сбрасывается
/// в "ничего не отправлено", поэтому следующая дельта запишет все и восстановит клиентов;</item>
/// <item>снимок строится из базовой копии, а не из живого объекта, поэтому снимок плюс любая последующая
/// дельта всегда дают согласованное состояние. Исключение — manual-члены (<see cref="ReplicatedAttribute.Manual"/>):
/// они пишутся из живого объекта, так что новый клиент получает их текущие значения;</item>
/// <item>кодеки, зарегистрированные в <see cref="Codecs"/> после построения модели типа, на эту модель не влияют.</item>
/// </list>
/// Экземпляр не потокобезопасен.
/// </summary>
public sealed class Replicator
{
    private readonly ReplicationContext _context;
    private readonly BitWriter _scratch = new();

    /// <summary>
    /// Создает репликатор.
    /// </summary>
    /// <param name="typeIds">Отображение типов для полиморфных членов или <c>null</c>, если полиморфизм не нужен</param>
    /// <param name="codecs">Реестр кодеков; <c>null</c> — новый реестр с кодеками по умолчанию</param>
    /// <param name="limits">Ограничения; <c>null</c> — значения по умолчанию. Значения копируются при создании</param>
    /// <param name="logger">Логгер для предупреждений; <c>null</c> — <c>Serilog.Log.ForContext&lt;Replicator&gt;()</c></param>
    /// <exception cref="ArgumentOutOfRangeException">Некорректные значения <paramref name="limits"/></exception>
    public Replicator(ITypeIdMapping typeIds = null, ReplicationCodecs codecs = null, ReplicationLimits limits = null,
        ILogger logger = null)
    {
        var limitsCopy = new ReplicationLimits();
        if (limits != null)
        {
            if (limits.MaxDepth < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(limits), limits.MaxDepth, "MaxDepth must be at least 1.");
            }

            if (limits.MaxCollectionCount < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(limits), limits.MaxCollectionCount,
                    "MaxCollectionCount must be at least 1.");
            }

            limitsCopy.MaxDepth = limits.MaxDepth;
            limitsCopy.MaxCollectionCount = limits.MaxCollectionCount;
        }

        Codecs = codecs ?? new ReplicationCodecs();
        _context = new ReplicationContext(limitsCopy, typeIds, Codecs, logger ?? Log.ForContext<Replicator>());
    }

    /// <summary>
    /// Реестр кодеков значений. Регистрировать кодеки нужно до первого обращения к типам, которые их используют.
    /// </summary>
    public ReplicationCodecs Codecs { get; }

    /// <summary>
    /// Создает базовую копию для объекта. Первая дельта для нее запишет все члены.
    /// </summary>
    /// <param name="target">Реплицируемый объект (экземпляр класса)</param>
    /// <returns>Базовая копия</returns>
    /// <exception cref="ArgumentNullException"><paramref name="target"/> равен <c>null</c></exception>
    /// <exception cref="ReplicationException">Тип объекта или его члены не поддерживаются</exception>
    public ReplicationBaseline CreateBaseline(object target)
    {
        ArgumentNullException.ThrowIfNull(target);

        ReplicationTypeModel model = _context.Builder.GetModel(target.GetType());
        return new ReplicationBaseline(this, target, model);
    }

    /// <summary>
    /// Пишет дельту объекта относительно базовой копии и обновляет базовую копию.
    /// </summary>
    /// <param name="baseline">Базовая копия, созданная этим репликатором</param>
    /// <param name="data">Данные дельты или <c>null</c>, если ничего не изменилось</param>
    /// <returns><c>false</c>, если ничего не изменилось</returns>
    /// <exception cref="ReplicationException">Ошибка записи (базовая копия сброшена)</exception>
    public bool TryWriteDelta(ReplicationBaseline baseline, out byte[] data)
    {
        _scratch.Reset();
        if (!TryWriteDelta(baseline, _scratch))
        {
            data = null;
            return false;
        }

        data = _scratch.ToArray();
        _scratch.Reset();
        return true;
    }

    /// <summary>
    /// Пишет дельту объекта в существующий писатель (для упаковки многих объектов в один пакет)
    /// и обновляет базовую копию. Если ничего не изменилось, писатель остается нетронутым.
    /// При исключении писатель откатывается к исходной позиции, а базовая копия сбрасывается.
    /// </summary>
    /// <param name="baseline">Базовая копия, созданная этим репликатором</param>
    /// <param name="writer">Писатель</param>
    /// <returns><c>false</c>, если ничего не изменилось</returns>
    /// <exception cref="ReplicationException">Ошибка записи (базовая копия сброшена)</exception>
    public bool TryWriteDelta(ReplicationBaseline baseline, BitWriter writer)
    {
        ValidateBaseline(baseline);
        ArgumentNullException.ThrowIfNull(writer);

        int start = writer.BitPosition;
        try
        {
            bool written = baseline.Model.WriteMembers(baseline.Target, baseline.State, writer, !baseline.IsWritten);
            if (!written)
            {
                writer.Rewind(start);
                return false;
            }

            baseline.IsWritten = true;
            return true;
        }
        catch
        {
            writer.Rewind(start);
            baseline.Reset();
            _context.ResetDepth();
            throw;
        }
    }

    /// <summary>
    /// Пишет полный снимок объекта из базовой копии (а не из живого объекта), поэтому его можно
    /// брать в любой момент кадра: снимок плюс следующая дельта дают клиенту согласованное состояние.
    /// Manual-члены пишутся из живого объекта целиком (новый клиент получает текущее значение,
    /// даже если член не был помечен). Базовая копия не меняется.
    /// </summary>
    /// <param name="baseline">Базовая копия, созданная этим репликатором</param>
    /// <param name="data">Данные снимка или <c>null</c>, если дельта еще ни разу не записывалась</param>
    /// <returns><c>false</c>, если дельта еще ни разу не записывалась</returns>
    public bool TryWriteSnapshot(ReplicationBaseline baseline, out byte[] data)
    {
        _scratch.Reset();
        if (!TryWriteSnapshot(baseline, _scratch))
        {
            data = null;
            return false;
        }

        data = _scratch.ToArray();
        _scratch.Reset();
        return true;
    }

    /// <summary>
    /// Пишет полный снимок объекта из базовой копии в существующий писатель.
    /// При исключении писатель откатывается к исходной позиции.
    /// </summary>
    /// <param name="baseline">Базовая копия, созданная этим репликатором</param>
    /// <param name="writer">Писатель</param>
    /// <returns><c>false</c>, если дельта еще ни разу не записывалась (писатель не тронут)</returns>
    public bool TryWriteSnapshot(ReplicationBaseline baseline, BitWriter writer)
    {
        ValidateBaseline(baseline);
        ArgumentNullException.ThrowIfNull(writer);

        if (!baseline.IsWritten)
        {
            return false;
        }

        int start = writer.BitPosition;
        try
        {
            baseline.Model.WriteShadowMembers(baseline.Target, baseline.State, writer);
            return true;
        }
        catch
        {
            writer.Rewind(start);
            _context.ResetDepth();
            throw;
        }
    }

    /// <summary>
    /// Применяет данные дельты или снимка к существующему объекту.
    /// Данные должны содержать ровно один объект: остаток в 8 бит и более считается ошибкой формата.
    /// </summary>
    /// <param name="target">Объект того же типа, что и на отправителе</param>
    /// <param name="data">Данные</param>
    /// <exception cref="ArgumentNullException"><paramref name="target"/> равен <c>null</c></exception>
    /// <exception cref="ReplicationFormatException">Данные повреждены или не соответствуют схеме</exception>
    /// <exception cref="ReplicationException">Тип объекта не поддерживается</exception>
    public void Apply(object target, ReadOnlySpan<byte> data)
    {
        ArgumentNullException.ThrowIfNull(target);

        var reader = new BitReader(data);
        Apply(target, ref reader);
        if (reader.RemainingBits >= 8)
        {
            throw new ReplicationFormatException(
                $"Trailing data after the replicated object {target.GetType().FullName}: {reader.RemainingBits} bits left.");
        }
    }

    /// <summary>
    /// Читает ровно один объект из читателя и применяет его к существующему объекту
    /// (для пакетов, содержащих данные многих объектов).
    /// </summary>
    /// <param name="target">Объект того же типа, что и на отправителе</param>
    /// <param name="reader">Читатель</param>
    /// <exception cref="ArgumentNullException"><paramref name="target"/> равен <c>null</c></exception>
    /// <exception cref="ReplicationFormatException">Данные повреждены или не соответствуют схеме</exception>
    /// <exception cref="ReplicationException">Тип объекта не поддерживается</exception>
    public void Apply(object target, ref BitReader reader)
    {
        ArgumentNullException.ThrowIfNull(target);

        ReplicationTypeModel model = _context.Builder.GetModel(target.GetType());
        try
        {
            model.ReadMembers(target, ref reader);
        }
        catch
        {
            _context.ResetDepth();
            throw;
        }
    }

    /// <summary>
    /// Возвращает хэш схемы типа (FNV-1a 64 над каноническим описанием членов, их типов, кодеков,
    /// квантования и допуска). Сервер и клиент должны сравнить хэши при рукопожатии:
    /// разные хэши означают несовместимый формат данных.
    /// </summary>
    /// <param name="type">Тип реплицируемого объекта</param>
    /// <returns>Хэш схемы</returns>
    /// <exception cref="ArgumentNullException"><paramref name="type"/> равен <c>null</c></exception>
    /// <exception cref="ReplicationException">Тип или его члены не поддерживаются</exception>
    public ulong GetSchemaHash(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        return _context.Builder.GetSchemaHash(type);
    }

    private void ValidateBaseline(ReplicationBaseline baseline)
    {
        ArgumentNullException.ThrowIfNull(baseline);

        if (baseline.Owner != this)
        {
            throw new ReplicationException("The baseline was created by another Replicator.");
        }
    }
}
