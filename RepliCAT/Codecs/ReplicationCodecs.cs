using System.Reflection;
using Godot;

namespace RepliCAT.Codecs;

/// <summary>
/// Реестр кодеков значений. В конструкторе регистрируются кодеки по умолчанию:
/// примитивы, <see cref="string"/> и структуры Godot (<see cref="Vector2"/>, <see cref="Color"/> и т.п.).<br/>
/// Кодеки для перечислений (<see cref="EnumCodec{TEnum}"/>) и <see cref="Nullable{T}"/> от кодируемых типов
/// (<see cref="NullableCodec{T}"/>) создаются лениво при первом запросе и кэшируются.
/// Явно зарегистрированный кодек всегда имеет приоритет над ленивым.<br/>
/// Кодеки, зарегистрированные после построения модели типа, на эту модель не влияют.
/// Реестр потокобезопасен.
/// </summary>
public sealed class ReplicationCodecs
{
    private static readonly MethodInfo CreateEnumCodecMethod =
        typeof(ReplicationCodecs).GetMethod(nameof(CreateEnumCodec), BindingFlags.NonPublic | BindingFlags.Static);

    private static readonly MethodInfo CreateNullableCodecMethod =
        typeof(ReplicationCodecs).GetMethod(nameof(CreateNullableCodec), BindingFlags.NonPublic | BindingFlags.Instance);

    private readonly object _lock = new();
    private readonly Dictionary<Type, object> _registered = new();
    private readonly Dictionary<Type, object> _derived = new();

    /// <summary>
    /// Создает реестр с кодеками по умолчанию.
    /// </summary>
    public ReplicationCodecs()
    {
        Register(BoolCodec.Instance);
        Register(ByteCodec.Instance);
        Register(SByteCodec.Instance);
        Register(Int16Codec.Instance);
        Register(UInt16Codec.Instance);
        Register(Int32Codec.Instance);
        Register(UInt32Codec.Instance);
        Register(Int64Codec.Instance);
        Register(UInt64Codec.Instance);
        Register(CharCodec.Instance);
        Register(SingleCodec.Instance);
        Register(DoubleCodec.Instance);
        Register(StringCodec.Default);

        Register(Vector2Codec.Instance);
        Register(Vector2ICodec.Instance);
        Register(Vector3Codec.Instance);
        Register(Vector3ICodec.Instance);
        Register(Vector4Codec.Instance);
        Register(Vector4ICodec.Instance);
        Register(ColorCodec.Instance);
        Register(QuaternionCodec.Instance);
        Register(Rect2Codec.Instance);
        Register(Rect2ICodec.Instance);
    }

    /// <summary>
    /// Регистрирует кодек для типа <typeparamref name="T"/>, заменяя ранее зарегистрированный
    /// (в том числе кодек по умолчанию). Лениво созданные кодеки сбрасываются,
    /// чтобы, например, <c>Nullable</c> от типа использовал новый кодек.
    /// </summary>
    /// <param name="codec">Кодек</param>
    /// <typeparam name="T">Тип значения</typeparam>
    public void Register<T>(IReplicationCodec<T> codec)
    {
        ArgumentNullException.ThrowIfNull(codec);

        lock (_lock)
        {
            _registered[typeof(T)] = codec;
            _derived.Clear();
        }
    }

    /// <summary>
    /// Пытается получить кодек для типа <typeparamref name="T"/>. Для перечислений и
    /// <see cref="Nullable{T}"/> от кодируемых типов кодек создается лениво.
    /// </summary>
    /// <param name="codec">Найденный кодек или <c>null</c></param>
    /// <typeparam name="T">Тип значения</typeparam>
    /// <returns><c>true</c>, если кодек найден</returns>
    public bool TryGet<T>(out IReplicationCodec<T> codec)
    {
        object found = GetOrCreate(typeof(T));
        codec = found as IReplicationCodec<T>;
        return codec != null;
    }

    /// <summary>
    /// Проверяет, может ли реестр закодировать значения указанного типа.
    /// </summary>
    /// <param name="type">Тип значения</param>
    /// <returns><c>true</c>, если для типа есть (или может быть создан) кодек</returns>
    public bool CanEncode(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        if (type.ContainsGenericParameters || type.IsByRef || type.IsPointer || type.IsByRefLike)
        {
            return false;
        }

        lock (_lock)
        {
            if (_registered.ContainsKey(type) || _derived.ContainsKey(type))
            {
                return true;
            }
        }

        if (type.IsEnum)
        {
            return true;
        }

        Type underlying = Nullable.GetUnderlyingType(type);
        return underlying != null && CanEncode(underlying);
    }

    private object GetOrCreate(Type type)
    {
        lock (_lock)
        {
            if (_registered.TryGetValue(type, out object codec) || _derived.TryGetValue(type, out codec))
            {
                return codec;
            }

            codec = CreateDerived(type);
            if (codec != null)
            {
                _derived[type] = codec;
            }

            return codec;
        }
    }

    /// <summary>
    /// Создает ленивый кодек. Вызывается под блокировкой.
    /// </summary>
    private object CreateDerived(Type type)
    {
        if (type.ContainsGenericParameters)
        {
            return null;
        }

        if (type.IsEnum)
        {
            return CreateEnumCodecMethod.MakeGenericMethod(type).Invoke(null, null);
        }

        Type underlying = Nullable.GetUnderlyingType(type);
        if (underlying == null)
        {
            return null;
        }

        object inner;
        if (!_registered.TryGetValue(underlying, out inner) && !_derived.TryGetValue(underlying, out inner))
        {
            inner = CreateDerived(underlying);
            if (inner == null)
            {
                return null;
            }

            _derived[underlying] = inner;
        }

        return CreateNullableCodecMethod.MakeGenericMethod(underlying).Invoke(this, [inner]);
    }

    private static object CreateEnumCodec<TEnum>() where TEnum : unmanaged, Enum
    {
        return new EnumCodec<TEnum>();
    }

    private object CreateNullableCodec<T>(object inner) where T : struct
    {
        return new NullableCodec<T>((IReplicationCodec<T>)inner);
    }
}
