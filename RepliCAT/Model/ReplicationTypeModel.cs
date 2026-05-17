using System.Text;
using RepliCAT.Bits;
using RepliCAT.Nodes;

namespace RepliCAT.Model;

/// <summary>
/// Состояние (тень) одного объекта: тени его членов в порядке модели.
/// </summary>
internal sealed class ObjectState
{
    public ObjectState(Shadow[] members)
    {
        Members = members;
    }

    /// <summary>
    /// Тени членов, индекс совпадает с индексом члена в модели.
    /// </summary>
    public Shadow[] Members { get; }

    /// <summary>
    /// Версии manual-членов на момент последней отправки. <c>null</c>, если у типа нет manual-членов
    /// (manual-члены появятся на шаге 9, сейчас всегда <c>null</c>).
    /// </summary>
    public int[] ManualVersions { get; set; }
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

        return new ObjectState(shadows);
    }

    /// <summary>
    /// Пишет тело объекта: маску членов и полезные нагрузки измененных членов.
    /// Если ничего не изменилось и <paramref name="forceAll"/> == <c>false</c>,
    /// откатывает писатель и возвращает <c>false</c>.
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

        bool any = false;
        int i = 0;
        try
        {
            for (; i < members.Length; i++)
            {
                int position = writer.BitPosition;
                if (members[i].WriteDelta(owner, shadows[i], writer, forceAll))
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

        if (!any && !forceAll)
        {
            writer.Rewind(maskStart);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Путь снимка: пишет тело объекта из теней, все биты маски установлены.
    /// </summary>
    /// <param name="owner">Живой объект (нужен только manual-членам, шаг 9)</param>
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
                members[i].WriteShadow(owner, shadows[i], writer);
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
    /// Тип, уже описанный выше по дереву, описывается только ссылкой (рекурсивные типы).
    /// </summary>
    public void AppendSchema(StringBuilder sb, HashSet<Type> visited)
    {
        if (!visited.Add(Type))
        {
            sb.Append("ref(").Append(Type.Name).Append(')');
            return;
        }

        sb.Append(Type.Name).Append('{');
        foreach (MemberReplicator member in _members)
        {
            sb.Append(member.Name).Append(':');
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
            AppendSchema(sb, new HashSet<Type>());
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
}
