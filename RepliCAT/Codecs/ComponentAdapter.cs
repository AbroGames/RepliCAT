using Godot;

namespace RepliCAT.Codecs;

/// <summary>
/// Представление значения типа <typeparamref name="T"/> как набора числовых компонентов (<see cref="double"/>).
/// Используется квантованием (<see cref="QuantizedCodec{T}"/>) и допуском сравнения (<see cref="ToleranceCodec{T}"/>).<br/>
/// Реализации не упаковывают значения и не выделяют память.
/// Готовые адаптеры для поддерживаемых типов возвращает <see cref="ComponentAdapter.TryGet{T}"/>.
/// </summary>
/// <typeparam name="T">Тип значения</typeparam>
public abstract class ComponentAdapter<T>
{
    /// <summary>
    /// Число компонентов значения.
    /// </summary>
    public abstract int Count { get; }

    /// <summary>
    /// <c>true</c>, если компоненты целочисленные: <see cref="Compose"/> округляет их до ближайшего целого
    /// (половины — от нуля) и прижимает к диапазону типа.
    /// </summary>
    public abstract bool IsInteger { get; }

    /// <summary>
    /// Возвращает компонент значения.
    /// </summary>
    /// <param name="value">Значение</param>
    /// <param name="index">Индекс компонента, от 0 до <see cref="Count"/> - 1</param>
    /// <returns>Компонент</returns>
    public abstract double Get(T value, int index);

    /// <summary>
    /// Собирает значение из компонентов.
    /// </summary>
    /// <param name="components">Компоненты; длина должна быть не меньше <see cref="Count"/></param>
    /// <returns>Значение</returns>
    public abstract T Compose(ReadOnlySpan<double> components);

    /// <summary>
    /// Максимальная по компонентам абсолютная разница двух значений.
    /// Если разница хотя бы одного компонента — NaN (NaN в компоненте, разность бесконечностей одного знака),
    /// результат — <see cref="double.NaN"/>.
    /// </summary>
    /// <param name="a">Первое значение</param>
    /// <param name="b">Второе значение</param>
    /// <returns>Максимальная разница компонентов или NaN</returns>
    public virtual double MaxDelta(T a, T b)
    {
        double max = 0;
        int count = Count;
        for (int i = 0; i < count; i++)
        {
            double delta = Math.Abs(Get(a, i) - Get(b, i));
            if (double.IsNaN(delta))
            {
                return double.NaN;
            }

            if (delta > max)
            {
                max = delta;
            }
        }

        return max;
    }

    /// <summary>
    /// Проверяет, что буфер компонентов достаточной длины.
    /// </summary>
    protected void CheckLength(ReadOnlySpan<double> components)
    {
        if (components.Length < Count)
        {
            throw new ArgumentException($"Expected at least {Count} components, got {components.Length}.", nameof(components));
        }
    }

    /// <summary>
    /// Бросает исключение для недопустимого индекса компонента.
    /// </summary>
    protected double ThrowIndex(int index)
    {
        throw new ArgumentOutOfRangeException(nameof(index), index, $"Component index must be in range 0..{Count - 1}.");
    }
}

/// <summary>
/// Реестр адаптеров компонентов для типов, поддерживающих квантование и допуск сравнения:
/// <see cref="float"/>, <see cref="double"/>, <see cref="Vector2"/>, <see cref="Vector3"/>, <see cref="Vector4"/>,
/// <see cref="Quaternion"/>, <see cref="Color"/> и целые <see cref="sbyte"/>, <see cref="byte"/>, <see cref="short"/>,
/// <see cref="ushort"/>, <see cref="int"/>, <see cref="uint"/>, <see cref="long"/>.
/// </summary>
public static class ComponentAdapter
{
    private static readonly Dictionary<Type, object> Adapters = new()
    {
        [typeof(float)] = new SingleComponentAdapter(),
        [typeof(double)] = new DoubleComponentAdapter(),
        [typeof(Vector2)] = new Vector2ComponentAdapter(),
        [typeof(Vector3)] = new Vector3ComponentAdapter(),
        [typeof(Vector4)] = new Vector4ComponentAdapter(),
        [typeof(Quaternion)] = new QuaternionComponentAdapter(),
        [typeof(Color)] = new ColorComponentAdapter(),
        [typeof(sbyte)] = new IntegerComponentAdapter<sbyte>(),
        [typeof(byte)] = new IntegerComponentAdapter<byte>(),
        [typeof(short)] = new IntegerComponentAdapter<short>(),
        [typeof(ushort)] = new IntegerComponentAdapter<ushort>(),
        [typeof(int)] = new IntegerComponentAdapter<int>(),
        [typeof(uint)] = new IntegerComponentAdapter<uint>(),
        [typeof(long)] = new IntegerComponentAdapter<long>(),
    };

    /// <summary>
    /// Пытается получить адаптер компонентов для типа <typeparamref name="T"/>.
    /// </summary>
    /// <param name="adapter">Адаптер или <c>null</c>, если тип не поддерживается</param>
    /// <typeparam name="T">Тип значения</typeparam>
    /// <returns><c>true</c>, если тип поддерживается</returns>
    public static bool TryGet<T>(out ComponentAdapter<T> adapter)
    {
        adapter = Cache<T>.Instance;
        return adapter != null;
    }

    /// <summary>
    /// Проверяет, поддерживает ли тип квантование и допуск сравнения.
    /// </summary>
    /// <param name="type">Тип значения</param>
    /// <returns><c>true</c>, если для типа есть адаптер</returns>
    public static bool IsSupported(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return Adapters.ContainsKey(type);
    }

    private static class Cache<T>
    {
        public static readonly ComponentAdapter<T> Instance =
            Adapters.TryGetValue(typeof(T), out object adapter) ? (ComponentAdapter<T>)adapter : null;
    }
}

internal sealed class SingleComponentAdapter : ComponentAdapter<float>
{
    public override int Count => 1;
    public override bool IsInteger => false;

    public override double Get(float value, int index)
    {
        return index == 0 ? value : ThrowIndex(index);
    }

    public override float Compose(ReadOnlySpan<double> components)
    {
        CheckLength(components);
        return (float)components[0];
    }

    public override double MaxDelta(float a, float b)
    {
        return Math.Abs((double)a - b);
    }
}

internal sealed class DoubleComponentAdapter : ComponentAdapter<double>
{
    public override int Count => 1;
    public override bool IsInteger => false;

    public override double Get(double value, int index)
    {
        return index == 0 ? value : ThrowIndex(index);
    }

    public override double Compose(ReadOnlySpan<double> components)
    {
        CheckLength(components);
        return components[0];
    }

    public override double MaxDelta(double a, double b)
    {
        return Math.Abs(a - b);
    }
}

internal sealed class IntegerComponentAdapter<TInt> : ComponentAdapter<TInt>
    where TInt : struct, System.Numerics.IBinaryInteger<TInt>
{
    public override int Count => 1;
    public override bool IsInteger => true;

    public override double Get(TInt value, int index)
    {
        return index == 0 ? double.CreateTruncating(value) : ThrowIndex(index);
    }

    public override TInt Compose(ReadOnlySpan<double> components)
    {
        CheckLength(components);
        // CreateSaturating прижимает к диапазону типа, NaN превращается в 0
        return TInt.CreateSaturating(Math.Round(components[0], MidpointRounding.AwayFromZero));
    }

    public override double MaxDelta(TInt a, TInt b)
    {
        return Math.Abs(double.CreateTruncating(a) - double.CreateTruncating(b));
    }
}

internal sealed class Vector2ComponentAdapter : ComponentAdapter<Vector2>
{
    public override int Count => 2;
    public override bool IsInteger => false;

    public override double Get(Vector2 value, int index)
    {
        return index switch
        {
            0 => value.X,
            1 => value.Y,
            _ => ThrowIndex(index)
        };
    }

    public override Vector2 Compose(ReadOnlySpan<double> components)
    {
        CheckLength(components);
        return new Vector2((float)components[0], (float)components[1]);
    }
}

internal sealed class Vector3ComponentAdapter : ComponentAdapter<Vector3>
{
    public override int Count => 3;
    public override bool IsInteger => false;

    public override double Get(Vector3 value, int index)
    {
        return index switch
        {
            0 => value.X,
            1 => value.Y,
            2 => value.Z,
            _ => ThrowIndex(index)
        };
    }

    public override Vector3 Compose(ReadOnlySpan<double> components)
    {
        CheckLength(components);
        return new Vector3((float)components[0], (float)components[1], (float)components[2]);
    }
}

internal sealed class Vector4ComponentAdapter : ComponentAdapter<Vector4>
{
    public override int Count => 4;
    public override bool IsInteger => false;

    public override double Get(Vector4 value, int index)
    {
        return index switch
        {
            0 => value.X,
            1 => value.Y,
            2 => value.Z,
            3 => value.W,
            _ => ThrowIndex(index)
        };
    }

    public override Vector4 Compose(ReadOnlySpan<double> components)
    {
        CheckLength(components);
        return new Vector4((float)components[0], (float)components[1], (float)components[2], (float)components[3]);
    }
}

internal sealed class QuaternionComponentAdapter : ComponentAdapter<Quaternion>
{
    public override int Count => 4;
    public override bool IsInteger => false;

    public override double Get(Quaternion value, int index)
    {
        return index switch
        {
            0 => value.X,
            1 => value.Y,
            2 => value.Z,
            3 => value.W,
            _ => ThrowIndex(index)
        };
    }

    public override Quaternion Compose(ReadOnlySpan<double> components)
    {
        CheckLength(components);
        return new Quaternion((float)components[0], (float)components[1], (float)components[2], (float)components[3]);
    }
}

internal sealed class ColorComponentAdapter : ComponentAdapter<Color>
{
    public override int Count => 4;
    public override bool IsInteger => false;

    public override double Get(Color value, int index)
    {
        return index switch
        {
            0 => value.R,
            1 => value.G,
            2 => value.B,
            3 => value.A,
            _ => ThrowIndex(index)
        };
    }

    public override Color Compose(ReadOnlySpan<double> components)
    {
        CheckLength(components);
        return new Color((float)components[0], (float)components[1], (float)components[2], (float)components[3]);
    }
}
