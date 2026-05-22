using System.Runtime.CompilerServices;
using System.Text;
using RepliCAT.Bits;
using RepliCAT.Model;

namespace RepliCAT.Nodes;

/// <summary>
/// Узел вложенного объекта. Полезная нагрузка:
/// <c>[1 бит present]</c>, для незапечатанного объявленного типа <c>[1 бит isDeclaredType]</c>
/// и <c>varuint typeId</c> из <see cref="RepliCAT.ITypeIdMapping"/>, если бит равен 0,
/// затем тело объекта runtime-типа (маска членов + измененные члены).<br/>
/// Отправитель: та же ссылка — дельта членов (пустая дельта откатывается); новая ссылка — новое состояние
/// и запись всех членов. Получатель переиспользует существующий экземпляр, если его runtime-тип совпадает
/// с полученным, иначе создает новый через конструктор без параметров.
/// </summary>
/// <typeparam name="T">Объявленный тип члена (класс, в том числе абстрактный, или интерфейс)</typeparam>
internal sealed class ObjectNode<T> : ReplicationNode<T> where T : class
{
    /// <summary>
    /// Значение <see cref="ObjectShadow.TypeId"/>, означающее, что runtime-тип равен объявленному.
    /// </summary>
    private const int DeclaredTypeId = -1;

    private readonly ReplicationContext _context;
    private readonly ReplicationTypeModel _declaredModel;
    private readonly string _path;
    private readonly bool _isSealed;
    private readonly bool _canBeDeclared;

    // Кэш модели последнего встреченного производного типа, чтобы не брать блокировку построителя каждый кадр.
    private ReplicationTypeModel _lastModel;

    /// <summary>
    /// Создает узел объекта.
    /// </summary>
    /// <param name="context">Контекст репликатора</param>
    /// <param name="declaredModel">Модель объявленного типа или <c>null</c>, если объявленный тип — интерфейс</param>
    /// <param name="path">Путь к члену для сообщений</param>
    public ObjectNode(ReplicationContext context, ReplicationTypeModel declaredModel, string path)
    {
        _context = context;
        _declaredModel = declaredModel;
        _path = path;
        _isSealed = typeof(T).IsSealed;
        _canBeDeclared = !typeof(T).IsAbstract && !typeof(T).IsInterface;
    }

    /// <inheritdoc/>
    public override bool RequiresSetter => false;

    /// <inheritdoc/>
    public override bool HasInnerState => true;

    /// <inheritdoc/>
    public override Shadow CreateShadow()
    {
        return new ObjectShadow();
    }

    /// <inheritdoc/>
    public override void AppendSchema(StringBuilder sb, Dictionary<Type, int> visited)
    {
        sb.Append("object(").Append(_isSealed ? "sealed" : "poly").Append(',');
        if (_declaredModel != null)
        {
            _declaredModel.AppendSchema(sb, visited);
        }
        else
        {
            sb.Append("interface ").Append(typeof(T).Name);
        }

        sb.Append(')');
    }

    /// <inheritdoc/>
    public override bool WriteDelta(T current, Shadow shadow, BitWriter writer, bool forceAll)
    {
        var objectShadow = (ObjectShadow)shadow;

        if (current == null)
        {
            if (!forceAll && objectShadow.Written && objectShadow.Ref == null)
            {
                return false;
            }

            writer.WriteBool(false);
            objectShadow.SetNull();
            return true;
        }

        int start = writer.BitPosition;

        if (objectShadow.Written && ReferenceEquals(objectShadow.Ref, current))
        {
            // Та же ссылка: тот же runtime-тип, пишем только изменившиеся члены.
            writer.WriteBool(true);
            WriteTypeInfo(objectShadow.TypeId, writer);

            bool written;
            _context.EnterWrite(_path);
            try
            {
                written = objectShadow.Model.WriteMembers(current, objectShadow.State, writer, forceAll);
            }
            finally
            {
                _context.Exit();
            }

            if (!written)
            {
                writer.Rewind(start);
                return false;
            }

            return true;
        }

        // Новая ссылка: новое состояние, все члены.
        Type runtimeType = current.GetType();
        int typeId = GetTypeId(runtimeType);
        ReplicationTypeModel model = GetModel(runtimeType);
        ObjectState state = model.CreateState();

        writer.WriteBool(true);
        WriteTypeInfo(typeId, writer);

        _context.EnterWrite(_path);
        try
        {
            model.WriteMembers(current, state, writer, true);
        }
        finally
        {
            _context.Exit();
        }

        objectShadow.Set(current, model, state, typeId);
        return true;
    }

    /// <inheritdoc/>
    public override void WriteShadow(Shadow shadow, BitWriter writer)
    {
        var objectShadow = (ObjectShadow)shadow;
        if (!objectShadow.Written)
        {
            throw new InvalidOperationException("Object shadow has never been written.");
        }

        if (objectShadow.Ref == null)
        {
            writer.WriteBool(false);
            return;
        }

        writer.WriteBool(true);
        WriteTypeInfo(objectShadow.TypeId, writer);

        _context.EnterWrite(_path);
        try
        {
            // Владелец для manual-членов — последняя отправленная ссылка (к ней относится состояние),
            // даже если член уже указывает на другой объект: следующая дельта запишет новый объект целиком.
            objectShadow.Model.WriteShadowMembers(objectShadow.Ref, objectShadow.State, writer);
        }
        finally
        {
            _context.Exit();
        }
    }

    /// <inheritdoc/>
    public override T Read(T existing, ref BitReader reader)
    {
        if (!reader.ReadBool())
        {
            return null;
        }

        ReplicationTypeModel model = ReadModel(ref reader);
        T result = existing != null && existing.GetType() == model.Type
            ? existing
            : Unsafe.As<T>(model.CreateInstance());

        _context.EnterRead(_path);
        try
        {
            model.ReadMembers(result, ref reader);
        }
        finally
        {
            _context.Exit();
        }

        return result;
    }

    private void WriteTypeInfo(int typeId, BitWriter writer)
    {
        if (_isSealed)
        {
            return;
        }

        if (typeId == DeclaredTypeId)
        {
            writer.WriteBool(true);
            return;
        }

        writer.WriteBool(false);
        writer.WriteVarUInt((ulong)typeId);
    }

    /// <summary>
    /// Возвращает идентификатор runtime-типа для записи (<see cref="DeclaredTypeId"/> для объявленного типа).
    /// Вызывается только для новой ссылки, поэтому отображение не опрашивается каждый кадр.
    /// </summary>
    private int GetTypeId(Type runtimeType)
    {
        if (runtimeType == typeof(T))
        {
            return DeclaredTypeId;
        }

        if (_context.TypeIds == null)
        {
            throw new ReplicationException(
                $"the runtime type {runtimeType.FullName} differs from the declared type {typeof(T).FullName}: " +
                "polymorphic members require an ITypeIdMapping passed to the Replicator.");
        }

        int id;
        try
        {
            id = _context.TypeIds.GetId(runtimeType);
        }
        catch (KeyNotFoundException e)
        {
            throw new ReplicationException(
                $"the runtime type {runtimeType.FullName} is not registered in the ITypeIdMapping.", e);
        }

        if (id < 0)
        {
            throw new ReplicationException(
                $"the ITypeIdMapping returned a negative id {id} for the type {runtimeType.FullName}.");
        }

        return id;
    }

    /// <summary>
    /// Читает информацию о типе и возвращает модель runtime-типа.
    /// </summary>
    private ReplicationTypeModel ReadModel(ref BitReader reader)
    {
        if (_isSealed || reader.ReadBool())
        {
            if (!_canBeDeclared)
            {
                throw new ReplicationFormatException(
                    $"the declared type {typeof(T).FullName} is abstract and cannot be instantiated.");
            }

            return _declaredModel;
        }

        ulong id = reader.ReadVarUInt();
        if (id > int.MaxValue)
        {
            throw new ReplicationFormatException($"type id {id} is out of range.");
        }

        if (_context.TypeIds == null)
        {
            throw new ReplicationFormatException(
                $"received type id {id} for the declared type {typeof(T).FullName}, but no ITypeIdMapping is configured.");
        }

        Type type;
        try
        {
            type = _context.TypeIds.GetType((int)id);
        }
        catch (KeyNotFoundException e)
        {
            throw new ReplicationFormatException($"unknown type id {id}.", e);
        }

        if (type == null)
        {
            throw new ReplicationFormatException($"unknown type id {id}.");
        }

        if (!typeof(T).IsAssignableFrom(type) || type.IsAbstract || type.IsInterface || type.IsValueType
            || type.ContainsGenericParameters)
        {
            throw new ReplicationFormatException(
                $"type id {id} ({type.FullName}) is not a concrete type assignable to {typeof(T).FullName}.");
        }

        return GetModel(type);
    }

    private ReplicationTypeModel GetModel(Type type)
    {
        if (type == typeof(T))
        {
            return _declaredModel;
        }

        ReplicationTypeModel cached = _lastModel;
        if (cached != null && cached.Type == type)
        {
            return cached;
        }

        cached = _context.Builder.GetModel(type);
        _lastModel = cached;
        return cached;
    }

    /// <summary>
    /// Тень объекта: последняя отправленная ссылка, ее модель, состояние членов и идентификатор типа.
    /// </summary>
    private sealed class ObjectShadow : Shadow
    {
        public object Ref;
        public bool Written;
        public ReplicationTypeModel Model;
        public ObjectState State;
        public int TypeId;

        public void SetNull()
        {
            Ref = null;
            Model = null;
            State = null;
            TypeId = DeclaredTypeId;
            Written = true;
        }

        public void Set(object reference, ReplicationTypeModel model, ObjectState state, int typeId)
        {
            Ref = reference;
            Model = model;
            State = state;
            TypeId = typeId;
            Written = true;
        }
    }
}
