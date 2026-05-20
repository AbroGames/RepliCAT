using System.Reflection;
using System.Text;
using RepliCAT.Reflection;
using RepliCAT.Codecs;
using RepliCAT.Nodes;

namespace RepliCAT.Model;

/// <summary>
/// Построитель и кэш моделей типов. Потокобезопасен: построение и поиск идут под блокировкой.
/// Модель кладется в кэш до построения членов, поэтому рекурсивные типы строятся корректно.
/// Если построение завершилось ошибкой, из кэша удаляются все модели, созданные в рамках
/// внешнего вызова <see cref="GetModel"/>, чтобы в кэше не осталось ссылок на недостроенные модели.
/// </summary>
internal sealed class ReplicationModelBuilder
{
    private const BindingFlags LevelFlags = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static
                                            | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly MethodInfo CreateValueMemberMethod =
        typeof(ReplicationModelBuilder).GetMethod(nameof(CreateValueMember), BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly MethodInfo CreateValueNodeMethod =
        typeof(ReplicationModelBuilder).GetMethod(nameof(CreateValueNode), BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly MethodInfo CreateObjectNodeMethod =
        typeof(ReplicationModelBuilder).GetMethod(nameof(CreateObjectNode), BindingFlags.NonPublic | BindingFlags.Instance);

    private readonly ReplicationContext _context;
    private readonly object _lock = new();
    private readonly Dictionary<Type, ReplicationTypeModel> _models = new();
    private readonly List<ReplicationTypeModel> _pending = new();
    private int _buildDepth;

    public ReplicationModelBuilder(ReplicationContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Возвращает модель типа, строя ее при первом обращении.
    /// При рекурсивном обращении во время построения может вернуть модель, члены которой еще строятся.
    /// </summary>
    /// <param name="type">Тип объекта (класс)</param>
    /// <exception cref="ReplicationException">Тип или его члены не поддерживаются</exception>
    public ReplicationTypeModel GetModel(Type type)
    {
        lock (_lock)
        {
            if (_models.TryGetValue(type, out ReplicationTypeModel model))
            {
                return model;
            }

            ValidateModelType(type);

            model = new ReplicationTypeModel(type);
            _models.Add(type, model);
            _pending.Add(model);
            _buildDepth++;
            try
            {
                model.SetMembers(BuildMembers(type));
            }
            catch
            {
                if (_buildDepth == 1)
                {
                    foreach (ReplicationTypeModel pending in _pending)
                    {
                        _models.Remove(pending.Type);
                    }

                    _pending.Clear();
                }

                throw;
            }
            finally
            {
                _buildDepth--;
            }

            if (_buildDepth == 0)
            {
                _pending.Clear();
            }

            return model;
        }
    }

    /// <summary>
    /// Возвращает хэш схемы типа.
    /// </summary>
    public ulong GetSchemaHash(Type type)
    {
        lock (_lock)
        {
            return GetModel(type).GetSchemaHash();
        }
    }

    /// <summary>
    /// Создает узел для значения указанного типа. Используется для членов, а на следующих шагах —
    /// для элементов коллекций и значений словарей.
    /// </summary>
    /// <param name="valueType">Тип значения</param>
    /// <param name="options">Квантование и допуск</param>
    /// <param name="path">Путь для сообщений и предупреждений</param>
    /// <exception cref="ReplicationException">Тип не поддерживается</exception>
    public ReplicationNode CreateNode(Type valueType, ValueOptions options, string path)
    {
        if (valueType.IsByRef || valueType.IsPointer || valueType.IsByRefLike || valueType.ContainsGenericParameters)
        {
            throw new ReplicationException($"{path}: type {valueType} is not supported.");
        }

        if (_context.Codecs.CanEncode(valueType))
        {
            return (ReplicationNode)Invoke(CreateValueNodeMethod.MakeGenericMethod(valueType), [options, path]);
        }

        string collectionHint = GetCollectionHint(valueType);
        if (collectionHint != null)
        {
            throw new ReplicationException($"{path}: {collectionHint}");
        }

        if (valueType.IsValueType)
        {
            throw new ReplicationException(
                $"{path}: struct {valueType.FullName} has no replication codec. " +
                "Register a codec via ReplicationCodecs.Register before the type model is built.");
        }

        if (typeof(Delegate).IsAssignableFrom(valueType))
        {
            throw new ReplicationException($"{path}: delegates ({valueType.FullName}) cannot be replicated.");
        }

        // Шаги 7–8: ReplicatedList, ReplicatedDictionary — до объектной маршрутизации.

        // Вложенный объект (класс, в том числе абстрактный, или интерфейс).
        if (!options.IsDefault)
        {
            throw new ReplicationException(
                $"{path}: [Quantize] and Tolerance cannot be applied to an object member of type {valueType.FullName}.");
        }

        return (ReplicationNode)Invoke(CreateObjectNodeMethod.MakeGenericMethod(valueType), [path]);
    }

    /// <summary>
    /// Проверяет, может ли тип иметь модель. Абстрактные классы допускаются: их модель описывает
    /// объявленный тип полиморфного члена (для хэша схемы), но экземпляры создаются только для
    /// конкретных runtime-типов.
    /// </summary>
    private static void ValidateModelType(Type type)
    {
        if (type.IsValueType || type.IsInterface || type.IsArray || type.IsPointer || type.IsByRef
            || type.ContainsGenericParameters || typeof(Delegate).IsAssignableFrom(type))
        {
            throw new ReplicationException(
                $"Type {type.FullName} cannot be replicated: only classes are supported as replicated objects.");
        }

        string collectionHint = GetCollectionHint(type);
        if (collectionHint != null)
        {
            throw new ReplicationException($"Type {type.FullName} cannot be replicated: {collectionHint}");
        }
    }

    private MemberReplicator[] BuildMembers(Type type)
    {
        // Иерархия от самого базового типа к самому производному, без object.
        var levels = new List<Type>();
        for (Type level = type; level != null && level != typeof(object); level = level.BaseType)
        {
            levels.Add(level);
        }

        levels.Reverse();

        var result = new List<MemberReplicator>();
        var levelMembers = new List<MemberInfo>();
        foreach (Type level in levels)
        {
            levelMembers.Clear();
            foreach (FieldInfo field in level.GetFields(LevelFlags))
            {
                if (field.IsDefined(typeof(ReplicatedAttribute), false))
                {
                    levelMembers.Add(field);
                }
            }

            foreach (PropertyInfo property in level.GetProperties(LevelFlags))
            {
                if (property.IsDefined(typeof(ReplicatedAttribute), false))
                {
                    levelMembers.Add(property);
                }
            }

            // Порядок GetFields/GetProperties не гарантирован, поэтому сортируем по имени (ordinal).
            levelMembers.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));

            foreach (MemberInfo member in levelMembers)
            {
                MemberReplicator replicator = BuildMember(member);
                if (replicator != null)
                {
                    result.Add(replicator);
                }
            }
        }

        return result.ToArray();
    }

    private MemberReplicator BuildMember(MemberInfo member)
    {
        string path = GetPath(member);
        var replicated = member.GetCustomAttribute<ReplicatedAttribute>(false);

        Type memberType;
        if (member is FieldInfo field)
        {
            if (field.IsStatic)
            {
                throw new ReplicationException($"{path}: static members cannot be replicated.");
            }

            if (field.IsLiteral)
            {
                throw new ReplicationException($"{path}: constants cannot be replicated.");
            }

            memberType = field.FieldType;
        }
        else
        {
            var property = (PropertyInfo)member;
            MethodInfo getter = property.GetGetMethod(true);
            MethodInfo setter = property.GetSetMethod(true);
            MethodInfo accessor = getter ?? setter;

            if (accessor.IsStatic)
            {
                throw new ReplicationException($"{path}: static members cannot be replicated.");
            }

            MethodInfo baseDefinition = accessor.GetBaseDefinition();
            if (baseDefinition.DeclaringType != accessor.DeclaringType)
            {
                // Переопределение: член описывается базовым объявлением, которое уже обработано на своем уровне.
                PropertyInfo baseProperty = FindDeclaredProperty(baseDefinition.DeclaringType, property.Name);
                if (baseProperty != null && baseProperty.IsDefined(typeof(ReplicatedAttribute), false))
                {
                    return null;
                }

                throw new ReplicationException(
                    $"{path}: [Replicated] on an overriding property is not allowed, " +
                    "put the attribute on the base declaration of the property.");
            }

            if (property.GetIndexParameters().Length > 0)
            {
                throw new ReplicationException($"{path}: indexers cannot be replicated.");
            }

            if (getter == null)
            {
                throw new ReplicationException($"{path}: a replicated property must have a getter.");
            }

            memberType = property.PropertyType;
        }

        ValueOptions options = ValueOptions.FromMember(member, replicated);
        ReplicationNode node = CreateNode(memberType, options, path);

        if (node.RequiresSetter && !TypedAccessors.CanWrite(member))
        {
            throw new ReplicationException(
                $"{path}: a replicated value member must be writable (not a readonly field or a get-only property).");
        }

        MethodInfo factory = CreateValueMemberMethod.MakeGenericMethod(member.DeclaringType, memberType);
        return (MemberReplicator)Invoke(factory, [member, path, node]);
    }

    private static PropertyInfo FindDeclaredProperty(Type type, string name)
    {
        foreach (PropertyInfo candidate in type.GetProperties(LevelFlags))
        {
            if (candidate.Name == name && candidate.GetIndexParameters().Length == 0)
            {
                return candidate;
            }
        }

        return null;
    }

    private static string GetCollectionHint(Type type)
    {
        if (type.IsArray)
        {
            return $"arrays ({type.Name}) cannot be replicated, use ReplicatedList<T> instead.";
        }

        if (!type.IsGenericType)
        {
            return null;
        }

        Type definition = type.GetGenericTypeDefinition();
        if (definition == typeof(List<>))
        {
            return "List<T> cannot be replicated, use ReplicatedList<T> instead.";
        }

        if (definition == typeof(Dictionary<,>))
        {
            return "Dictionary<TKey, TValue> cannot be replicated, use ReplicatedDictionary<TKey, TValue> instead.";
        }

        if (definition == typeof(HashSet<>))
        {
            return "HashSet<T> cannot be replicated, use ReplicatedDictionary<TKey, TValue> or ReplicatedList<T> instead.";
        }

        return null;
    }

    private static string GetPath(MemberInfo member)
    {
        return $"{member.DeclaringType?.FullName}.{member.Name}";
    }

    private object Invoke(MethodInfo method, object[] arguments)
    {
        return method.Invoke(this, BindingFlags.DoNotWrapExceptions, null, arguments, null);
    }

    private ValueNode<T> CreateValueNode<T>(ValueOptions options, string path)
    {
        if (!_context.Codecs.TryGet(out IReplicationCodec<T> baseCodec))
        {
            throw new ReplicationException($"{path}: no codec for type {typeof(T).FullName}.");
        }

        // Один кодек на член: он же используется для дельты, снимка и чтения,
        // поэтому предупреждения квантователя выдаются один раз на член.
        IReplicationCodec<T> codec = CodecOptions.Apply(baseCodec, options.Quantize, options.Tolerance, path, _context.Logger);

        var schema = new StringBuilder();
        SchemaHash.AppendTypeName(schema, typeof(T));
        schema.Append(";codec=");
        SchemaHash.AppendCodec(schema, baseCodec);
        options.AppendSchema(schema);

        return new ValueNode<T>(codec, schema.ToString());
    }

    private ObjectNode<T> CreateObjectNode<T>(string path) where T : class
    {
        // Модель объявленного типа строится сразу: ошибки вложенных типов проявляются при построении
        // модели корня. Для рекурсивных типов возвращается модель, члены которой еще строятся.
        // У интерфейса модели нет: его члены не входят в модели реализаций.
        ReplicationTypeModel declaredModel = typeof(T).IsInterface ? null : GetModel(typeof(T));
        return new ObjectNode<T>(_context, declaredModel, path);
    }

    private MemberReplicator CreateValueMember<TOwner, TValue>(MemberInfo member, string path, ReplicationNode node)
        where TOwner : class
    {
        Func<TOwner, TValue> getter;
        Action<TOwner, TValue> setter;
        try
        {
            getter = TypedAccessors.CreateGetter<TOwner, TValue>(member);
            setter = TypedAccessors.CreateSetter<TOwner, TValue>(member);
        }
        catch (ArgumentException e)
        {
            throw new ReplicationException($"{path}: {e.Message}", e);
        }

        return new MemberReplicator<TOwner, TValue>(member, path, getter, setter, (ReplicationNode<TValue>)node);
    }
}
