using RepliCAT.Bits;
using RepliCAT.Codecs;
using Serilog;

namespace RepliCAT.Model;

/// <summary>
/// Общий контекст одного <see cref="Replicator"/>: ограничения, отображение типов, реестр кодеков,
/// построитель моделей, логгер и счетчик глубины. Передается узлам через конструктор.
/// Не потокобезопасен (как и <see cref="Replicator"/>), кроме построителя моделей.
/// </summary>
internal sealed class ReplicationContext
{
    private int _depth;

    public ReplicationContext(ReplicationLimits limits, ITypeIdMapping typeIds, ReplicationCodecs codecs, ILogger logger)
    {
        Limits = limits;
        TypeIds = typeIds;
        Codecs = codecs;
        Logger = logger;
        Builder = new ReplicationModelBuilder(this);
    }

    /// <summary>
    /// Ограничения (копия, сделанная при создании <see cref="Replicator"/>).
    /// </summary>
    public ReplicationLimits Limits { get; }

    /// <summary>
    /// Отображение типов для полиморфизма или <c>null</c>.
    /// </summary>
    public ITypeIdMapping TypeIds { get; }

    /// <summary>
    /// Реестр кодеков значений.
    /// </summary>
    public ReplicationCodecs Codecs { get; }

    /// <summary>
    /// Логгер для предупреждений (не <c>null</c>).
    /// </summary>
    public ILogger Logger { get; }

    /// <summary>
    /// Построитель и кэш моделей типов.
    /// </summary>
    public ReplicationModelBuilder Builder { get; }

    /// <summary>
    /// Текущая глубина вложенности при записи или чтении.
    /// </summary>
    public int Depth => _depth;

    /// <summary>
    /// Входит на следующий уровень вложенности при записи.
    /// При превышении <see cref="ReplicationLimits.MaxDepth"/> бросает <see cref="ReplicationException"/>
    /// (обычно это цикл в графе объектов).
    /// </summary>
    /// <param name="path">Путь к члену для сообщения</param>
    public void EnterWrite(string path)
    {
        if (_depth >= Limits.MaxDepth)
        {
            throw ReplicationTypeModel.TagPath(new ReplicationException(
                $"{path}: maximum replication depth {Limits.MaxDepth} exceeded while writing (a cycle in the object graph?)."),
                path);
        }

        _depth++;
    }

    /// <summary>
    /// Входит на следующий уровень вложенности при чтении.
    /// При превышении <see cref="ReplicationLimits.MaxDepth"/> бросает <see cref="ReplicationFormatException"/>.
    /// </summary>
    /// <param name="path">Путь к члену для сообщения</param>
    public void EnterRead(string path)
    {
        if (_depth >= Limits.MaxDepth)
        {
            throw ReplicationTypeModel.TagPath(new ReplicationFormatException(
                $"{path}: maximum replication depth {Limits.MaxDepth} exceeded while reading."), path);
        }

        _depth++;
    }

    /// <summary>
    /// Выходит с текущего уровня вложенности.
    /// </summary>
    public void Exit()
    {
        _depth--;
    }

    /// <summary>
    /// Проверяет при записи, что коллекция не превышает <see cref="ReplicationLimits.MaxCollectionCount"/>:
    /// иначе получатель отверг бы данные.
    /// </summary>
    /// <param name="count">Количество элементов (или идентификатор слота + 1)</param>
    /// <param name="path">Путь к члену для сообщения</param>
    /// <exception cref="ReplicationException">Лимит превышен</exception>
    public void CheckCollectionCount(int count, string path)
    {
        if (count > Limits.MaxCollectionCount)
        {
            throw new ReplicationException(
                $"{path}: the collection exceeds ReplicationLimits.MaxCollectionCount ({Limits.MaxCollectionCount}).");
        }
    }

    /// <summary>
    /// Читает идентификатор слота коллекции (<c>varuint</c>), который должен быть меньше
    /// <see cref="ReplicationLimits.MaxCollectionCount"/>.
    /// </summary>
    /// <param name="reader">Читатель</param>
    /// <exception cref="ReplicationFormatException">Идентификатор вне допустимого диапазона</exception>
    public int ReadSlotId(ref BitReader reader)
    {
        ulong slot = reader.ReadVarUInt();
        if (slot >= (ulong)Limits.MaxCollectionCount)
        {
            throw new ReplicationFormatException(
                $"collection slot id {slot} exceeds ReplicationLimits.MaxCollectionCount ({Limits.MaxCollectionCount}).");
        }

        return (int)slot;
    }

    /// <summary>
    /// Читает количество элементов коллекции (<c>varuint</c>), которое не должно превышать
    /// <see cref="ReplicationLimits.MaxCollectionCount"/>.
    /// </summary>
    /// <param name="reader">Читатель</param>
    /// <exception cref="ReplicationFormatException">Количество вне допустимого диапазона</exception>
    public int ReadCollectionCount(ref BitReader reader)
    {
        ulong count = reader.ReadVarUInt();
        if (count > (ulong)Limits.MaxCollectionCount)
        {
            throw new ReplicationFormatException(
                $"collection count {count} exceeds ReplicationLimits.MaxCollectionCount ({Limits.MaxCollectionCount}).");
        }

        return (int)count;
    }

    /// <summary>
    /// Сбрасывает глубину. Вызывается корневыми операциями после исключения.
    /// </summary>
    public void ResetDepth()
    {
        _depth = 0;
    }
}
