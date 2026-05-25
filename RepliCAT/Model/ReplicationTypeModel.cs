using System.Reflection;
using System.Text;
using RepliCAT.Bits;
using RepliCAT.Nodes;

namespace RepliCAT.Model;

/// <summary>
/// Состояние (тень) одного объекта: тени его членов в порядке модели и версии manual-членов.
/// Состояние относится ровно к одному живому объекту (корню базовой копии или ссылке в тени узла объекта).
/// </summary>
internal sealed class ObjectState
{
    public ObjectState(Shadow[] members, int[] manualVersions)
    {
        Members = members;
        ManualVersions = manualVersions;
    }

    /// <summary>
    /// Тени членов, индекс совпадает с индексом члена в модели.
    /// </summary>
    public Shadow[] Members { get; }

    /// <summary>
    /// Версии manual-членов (<see cref="ManualReplication.MarkDirty"/>) на момент их последней отправки,
    /// индекс совпадает с индексом члена в модели (для обычных членов не используется).
    /// <c>null</c>, если у типа нет manual-членов.
    /// </summary>
    public int[] ManualVersions { get; }

    /// <summary>
    /// Общая версия таблицы пометок объекта (<see cref="ManualVersionTable.Stamp"/>), прочитанная
    /// при последней проверке manual-членов. Пока она не изменилась, версии членов не перечитываются.
    /// </summary>
    public int ManualStamp { get; set; }
}

/// <summary>
/// Модель одного runtime-типа: упорядоченный список реплицируемых членов и операции над телом объекта
/// (маска членов + полезные нагрузки измененных членов).<br/>
/// Модель кладется в кэш построителя до построения членов (для рекурсивных типов),
/// поэтому до завершения построения <see cref="Members"/> равен <c>null</c>.
/// </summary>
internal sealed class ReplicationTypeModel
{
    private const int StackMaskWords = 8;
    private const string PathDataKey = "RepliCAT.MemberPath";

    private MemberReplicator[] _members;
    private bool _hasManualMembers;
    private bool _hasSchemaHash;
    private ulong _schemaHash;

    public ReplicationTypeModel(Type type)
    {
        Type = type;
    }

    /// <summary>
    /// Тип, описываемый моделью.
    /// </summary>
    public Type Type { get; }

    /// <summary>
    /// Члены в порядке модели (порядок на проводе).
    /// </summary>
    public MemberReplicator[] Members => _members;

    /// <summary>
    /// Завершает построение модели.
    /// </summary>
    internal void SetMembers(MemberReplicator[] members)
    {
        _members = members;
        _hasManualMembers = Array.Exists(members, static member => member.IsManual);
    }

    /// <summary>
    /// Создает пустое состояние объекта ("ничего не отправлено").
    /// </summary>
    public ObjectState CreateState()
    {
        var shadows = new Shadow[_members.Length];
        for (int i = 0; i < shadows.Length; i++)
        {
            shadows[i] = _members[i].CreateShadow();
        }

        return new ObjectState(shadows, _hasManualMembers ? new int[shadows.Length] : null);
    }

    /// <summary>
    /// Пишет тело объекта: маску членов и полезные нагрузки измененных членов.
    /// Если ничего не изменилось и <paramref name="forceAll"/> == <c>false</c>,
    /// откатывает писатель и возвращает <c>false</c>.<br/>
    /// Manual-член не сравнивается с тенью: он пишется целиком (<c>forceAll</c>) при <paramref name="forceAll"/>
    /// или если его версия (<see cref="ManualReplication.MarkDirty"/>) отличается от отправленной последней,
    /// иначе пропускается вместе со всем своим поддеревом. Если с последней проверки объект не помечался,
    /// версии членов не перечитываются, и путь не выделяет память.
    /// </summary>
    /// <param name="owner">Живой объект типа <see cref="Type"/></param>
    /// <param name="state">Состояние объекта, созданное этой моделью</param>
    /// <param name="writer">Писатель</param>
    /// <param name="forceAll">Записать все члены</param>
    /// <returns><c>true</c>, если что-то записано</returns>
    public bool WriteMembers(object owner, ObjectState state, BitWriter writer, bool forceAll)
    {
        MemberReplicator[] members = _members;
        Shadow[] shadows = state.Members;
        int maskStart = writer.BitPosition;
        WriteZeroBits(writer, members.Length);

        // Manual-члены проверяются, только если с последней проверки объект помечался (или forceAll).
        // Отметка читается до версий членов (см. ManualVersionTable.Stamp).
        ManualVersionTable manualTable = null;
        int manualStamp = 0;
        bool checkManual = false;
        if (_hasManualMembers)
        {
            manualTable = ManualReplication.GetTable(owner);
            manualStamp = manualTable?.Stamp ?? 0;
            checkManual = forceAll || (manualTable != null && manualStamp != state.ManualStamp);
        }

        bool any = false;
        int i = 0;
        try
        {
            for (; i < members.Length; i++)
            {
                MemberReplicator member = members[i];
                if (member.IsManual)
                {
                    if (!checkManual)
                    {
                        continue;
                    }

                    int version = manualTable?.GetVersion(member.DirtyName) ?? 0;
                    if (!forceAll && version == state.ManualVersions[i])
                    {
                        continue;
                    }

                    // Весь член (и его поддерево) целиком, в настоящую тень.
                    member.WriteDelta(owner, shadows[i], writer, true);
                    state.ManualVersions[i] = version;
                    writer.SetBit(maskStart + i, true);
                    any = true;
                    continue;
                }

                int position = writer.BitPosition;
                if (member.WriteDelta(owner, shadows[i], writer, forceAll))
                {
                    writer.SetBit(maskStart + i, true);
                    any = true;
                }
                else
                {
                    writer.Rewind(position);
                }
            }
        }
        catch (Exception e) when (ShouldWrap(e))
        {
            throw WithPath(e, members[i].Path, false);
        }

        if (checkManual)
        {
            state.ManualStamp = manualStamp;
        }

        if (!any && !forceAll)
        {
            writer.Rewind(maskStart);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Путь снимка: пишет тело объекта из теней, все биты маски установлены.<br/>
    /// Manual-члены пишутся из <b>живого</b> владельца целиком, через <see cref="MemberReplicator.WriteDelta"/>
    /// с одноразовой тенью: новый клиент получает текущее значение, а базовая копия не меняется.
    /// Это согласуется с последующими дельтами: manual-член в них пишется только целиком
    /// (объект — всеми членами, коллекция — сбросом), что перезаписывает все, что прислал снимок.
    /// </summary>
    /// <param name="owner">
    /// Живой объект, к которому относится <paramref name="state"/>. Для вложенного объекта это последняя
    /// отправленная ссылка: если ее уже заменили, manual-члены читаются из прежнего объекта, что согласуется
    /// с остальным снимком (тип и прочие члены тоже прежние), а следующая дельта запишет новый объект целиком
    /// </param>
    /// <param name="state">Состояние объекта, в которое уже была записана хотя бы одна дельта</param>
    /// <param name="writer">Писатель</param>
    public void WriteShadowMembers(object owner, ObjectState state, BitWriter writer)
    {
        MemberReplicator[] members = _members;
        Shadow[] shadows = state.Members;
        WriteOneBits(writer, members.Length);

        int i = 0;
        try
        {
            for (; i < members.Length; i++)
            {
                MemberReplicator member = members[i];
                if (member.IsManual)
                {
                    member.WriteDelta(owner, member.CreateShadow(), writer, true);
                }
                else
                {
                    member.WriteShadow(owner, shadows[i], writer);
                }
            }
        }
        catch (Exception e) when (ShouldWrap(e))
        {
            throw WithPath(e, members[i].Path, false);
        }
    }

    /// <summary>
    /// Читает тело объекта и присваивает прочитанные члены.
    /// </summary>
    /// <param name="owner">Объект типа <see cref="Type"/></param>
    /// <param name="reader">Читатель</param>
    public void ReadMembers(object owner, ref BitReader reader)
    {
        MemberReplicator[] members = _members;
        int count = members.Length;
        int words = (count + 63) >> 6;
        Span<ulong> mask = words <= StackMaskWords ? stackalloc ulong[StackMaskWords] : new ulong[words];
        for (int w = 0; w < words; w++)
        {
            mask[w] = reader.ReadBits(Math.Min(64, count - (w << 6)));
        }

        int i = 0;
        try
        {
            for (; i < count; i++)
            {
                if ((mask[i >> 6] & (1UL << (i & 63))) != 0)
                {
                    members[i].Read(owner, ref reader);
                }
            }
        }
        catch (Exception e) when (ShouldWrap(e))
        {
            throw WithPath(e, members[i].Path, true);
        }
    }

    /// <summary>
    /// Дописывает каноническое описание модели для хэша схемы.
    /// Тип, уже описанный ранее при обходе, описывается только ссылкой на свой порядковый номер
    /// (<c>ref(#N)</c>): так описание однозначно, даже если у двух разных типов одинаковое короткое имя,
    /// и конечно для рекурсивных типов.
    /// </summary>
    public void AppendSchema(StringBuilder sb, Dictionary<Type, int> visited)
    {
        if (visited.TryGetValue(Type, out int index))
        {
            sb.Append("ref(#").Append(index).Append(')');
            return;
        }

        visited.Add(Type, visited.Count);
        sb.Append(Type.Name).Append('{');
        foreach (MemberReplicator member in _members)
        {
            sb.Append(member.Name);
            if (member.IsManual)
            {
                sb.Append("!manual");
            }

            sb.Append(':');
            member.Node.AppendSchema(sb, visited);
            sb.Append(';');
        }

        sb.Append('}');
    }

    /// <summary>
    /// Хэш схемы модели (вычисляется один раз).
    /// </summary>
    public ulong GetSchemaHash()
    {
        if (!_hasSchemaHash)
        {
            var sb = new StringBuilder();
            AppendSchema(sb, new Dictionary<Type, int>());
            _schemaHash = SchemaHash.Compute(sb.ToString());
            _hasSchemaHash = true;
        }

        return _schemaHash;
    }

    private static void WriteZeroBits(BitWriter writer, int count)
    {
        while (count > 0)
        {
            int chunk = Math.Min(64, count);
            writer.WriteBits(0, chunk);
            count -= chunk;
        }
    }

    private static void WriteOneBits(BitWriter writer, int count)
    {
        while (count > 0)
        {
            int chunk = Math.Min(64, count);
            writer.WriteBits(ulong.MaxValue, chunk);
            count -= chunk;
        }
    }

    /// <summary>
    /// Оборачиваются исключения, еще не содержащие путь к члену. Внутренние уровни (вложенные объекты)
    /// оборачивают первыми, поэтому в сообщении оказывается путь самого глубокого члена.
    /// </summary>
    private static bool ShouldWrap(Exception e)
    {
        return e is not OutOfMemoryException && !e.Data.Contains(PathDataKey);
    }

    /// <summary>
    /// Добавляет к исключению путь к члену. <see cref="ReplicationFormatException"/> остается
    /// <see cref="ReplicationFormatException"/>, прочие исключения (в том числе из пользовательских кодеков)
    /// становятся <see cref="ReplicationException"/>.
    /// </summary>
    private static Exception WithPath(Exception e, string path, bool reading)
    {
        string message = $"{path}: {(reading ? "failed to read" : "failed to write")}: {e.Message}";
        ReplicationException wrapped = e is ReplicationFormatException
            ? new ReplicationFormatException(message, e)
            : new ReplicationException(message, e);
        wrapped.Data[PathDataKey] = path;
        return wrapped;
    }

    /// <summary>
    /// Помечает исключение, сообщение которого уже содержит путь к члену, чтобы внешние уровни
    /// не оборачивали его повторно (иначе путь дублируется в сообщении).
    /// </summary>
    /// <param name="e">Исключение с путем в сообщении</param>
    /// <param name="path">Путь к члену</param>
    /// <returns><paramref name="e"/></returns>
    internal static TException TagPath<TException>(TException e, string path) where TException : Exception
    {
        e.Data[PathDataKey] = path;
        return e;
    }

    /// <summary>
    /// Создает экземпляр типа модели через конструктор без параметров (допускается непубличный).
    /// Используется получателем, когда существующий экземпляр нельзя переиспользовать.
    /// Исключение конструктора пробрасывается как есть, без обертки в <see cref="TargetInvocationException"/>.
    /// </summary>
    /// <exception cref="ReplicationException">У типа нет конструктора без параметров</exception>
    public object CreateInstance()
    {
        try
        {
            return Activator.CreateInstance(
                Type,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DoNotWrapExceptions,
                binder: null,
                args: null,
                culture: null);
        }
        catch (MissingMethodException e)
        {
            throw new ReplicationException(
                $"Type {Type.FullName} has no parameterless constructor, so the receiver cannot create an instance of it.", e);
        }
    }
}
