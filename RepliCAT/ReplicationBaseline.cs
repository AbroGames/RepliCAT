using RepliCAT.Model;

namespace RepliCAT;

/// <summary>
/// Базовая копия (baseline) одного корневого объекта: состояние, которое было отправлено последним.
/// Создается через <see cref="Replicator.CreateBaseline"/> и может использоваться только с тем же
/// <see cref="Replicator"/>. Содержимое непрозрачно для пользователя.<br/>
/// Первая дельта после создания (или после исключения при записи) содержит все члены.
/// </summary>
public sealed class ReplicationBaseline
{
    internal ReplicationBaseline(Replicator owner, object target, ReplicationTypeModel model)
    {
        Owner = owner;
        Target = target;
        Model = model;
        State = model.CreateState();
    }

    /// <summary>
    /// Реплицируемый объект.
    /// </summary>
    public object Target { get; }

    /// <summary>
    /// <c>true</c>, если хотя бы одна дельта уже записана, то есть снимок можно построить.
    /// </summary>
    public bool IsWritten { get; internal set; }

    internal Replicator Owner { get; }

    internal ReplicationTypeModel Model { get; }

    internal ObjectState State { get; private set; }

    /// <summary>
    /// Сбрасывает базовую копию в состояние "ничего не отправлено": следующая дельта запишет все.
    /// </summary>
    internal void Reset()
    {
        State = Model.CreateState();
        IsWritten = false;
    }
}
