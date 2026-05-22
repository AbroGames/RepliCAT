using RepliCAT.Bits;

namespace RepliCAT.Codecs;

/// <summary>
/// Кодек значения для репликации: запись и чтение значения в битовый поток,
/// а также определение изменения значения на уровне данных на проводе.<br/>
/// Реализации не должны упаковывать (boxing) значения и выделять память на горячем пути записи.<br/>
/// <b>Собственный кодек для класса:</b> значения должны быть неизменяемыми (менять только заменой экземпляра).
/// Базовая копия хранит ссылку на последнее отправленное значение, а не копию, поэтому для объекта, измененного
/// на месте, <see cref="IsChanged"/> получит один и тот же экземпляр в обоих аргументах, и изменение не будет
/// отправлено. Подробнее см. <see cref="ReplicationCodecs.Register{T}"/>.
/// </summary>
/// <typeparam name="T">Тип значения</typeparam>
public interface IReplicationCodec<T>
{
    /// <summary>
    /// Записывает значение в поток.
    /// </summary>
    /// <param name="writer">Писатель</param>
    /// <param name="value">Значение</param>
    void Write(BitWriter writer, T value);

    /// <summary>
    /// Читает значение из потока.
    /// При некорректных данных бросает <see cref="ReplicationFormatException"/>.
    /// </summary>
    /// <param name="reader">Читатель</param>
    /// <returns>Прочитанное значение</returns>
    T Read(ref BitReader reader);

    /// <summary>
    /// Определяет, отличается ли текущее значение от последнего отправленного настолько,
    /// что его нужно отправить снова.
    /// </summary>
    /// <param name="lastSent">Последнее отправленное значение</param>
    /// <param name="current">Текущее значение</param>
    /// <returns><c>true</c>, если значение нужно отправить</returns>
    bool IsChanged(T lastSent, T current);
}
