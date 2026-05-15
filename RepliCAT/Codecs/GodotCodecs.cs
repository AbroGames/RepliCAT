using System.Runtime.CompilerServices;
using Godot;
using RepliCAT.Bits;

namespace RepliCAT.Codecs;

/// <summary>
/// Кодек <see cref="Vector2"/>: покомпонентно, каждый компонент как 32 сырых бита float.<br/>
/// Изменение определяется покомпонентно по семантике <see cref="float.Equals(float)"/>: NaN равен NaN, -0 равен 0.
/// </summary>
public sealed class Vector2Codec : IReplicationCodec<Vector2>
{
    /// <summary>
    /// Общий экземпляр кодека (кодек не имеет состояния).
    /// </summary>
    public static readonly Vector2Codec Instance = new();

    /// <inheritdoc/>
    public void Write(BitWriter writer, Vector2 value)
    {
        writer.WriteSingle(value.X);
        writer.WriteSingle(value.Y);
    }

    /// <inheritdoc/>
    public Vector2 Read(ref BitReader reader)
    {
        return new Vector2(reader.ReadSingle(), reader.ReadSingle());
    }

    /// <inheritdoc/>
    public bool IsChanged(Vector2 lastSent, Vector2 current)
    {
        return GodotCodecMath.Differs(lastSent.X, current.X)
            || GodotCodecMath.Differs(lastSent.Y, current.Y);
    }
}

/// <summary>
/// Кодек <see cref="Vector2I"/>: покомпонентно, каждый компонент как 32-битное целое.
/// </summary>
public sealed class Vector2ICodec : IReplicationCodec<Vector2I>
{
    /// <summary>
    /// Общий экземпляр кодека (кодек не имеет состояния).
    /// </summary>
    public static readonly Vector2ICodec Instance = new();

    /// <inheritdoc/>
    public void Write(BitWriter writer, Vector2I value)
    {
        writer.WriteBits((uint)value.X, 32);
        writer.WriteBits((uint)value.Y, 32);
    }

    /// <inheritdoc/>
    public Vector2I Read(ref BitReader reader)
    {
        return new Vector2I((int)(uint)reader.ReadBits(32), (int)(uint)reader.ReadBits(32));
    }

    /// <inheritdoc/>
    public bool IsChanged(Vector2I lastSent, Vector2I current)
    {
        return lastSent.X != current.X
            || lastSent.Y != current.Y;
    }
}

/// <summary>
/// Кодек <see cref="Vector3"/>: покомпонентно, каждый компонент как 32 сырых бита float.<br/>
/// Изменение определяется покомпонентно по семантике <see cref="float.Equals(float)"/>: NaN равен NaN, -0 равен 0.
/// </summary>
public sealed class Vector3Codec : IReplicationCodec<Vector3>
{
    /// <summary>
    /// Общий экземпляр кодека (кодек не имеет состояния).
    /// </summary>
    public static readonly Vector3Codec Instance = new();

    /// <inheritdoc/>
    public void Write(BitWriter writer, Vector3 value)
    {
        writer.WriteSingle(value.X);
        writer.WriteSingle(value.Y);
        writer.WriteSingle(value.Z);
    }

    /// <inheritdoc/>
    public Vector3 Read(ref BitReader reader)
    {
        return new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }

    /// <inheritdoc/>
    public bool IsChanged(Vector3 lastSent, Vector3 current)
    {
        return GodotCodecMath.Differs(lastSent.X, current.X)
            || GodotCodecMath.Differs(lastSent.Y, current.Y)
            || GodotCodecMath.Differs(lastSent.Z, current.Z);
    }
}

/// <summary>
/// Кодек <see cref="Vector3I"/>: покомпонентно, каждый компонент как 32-битное целое.
/// </summary>
public sealed class Vector3ICodec : IReplicationCodec<Vector3I>
{
    /// <summary>
    /// Общий экземпляр кодека (кодек не имеет состояния).
    /// </summary>
    public static readonly Vector3ICodec Instance = new();

    /// <inheritdoc/>
    public void Write(BitWriter writer, Vector3I value)
    {
        writer.WriteBits((uint)value.X, 32);
        writer.WriteBits((uint)value.Y, 32);
        writer.WriteBits((uint)value.Z, 32);
    }

    /// <inheritdoc/>
    public Vector3I Read(ref BitReader reader)
    {
        return new Vector3I((int)(uint)reader.ReadBits(32), (int)(uint)reader.ReadBits(32), (int)(uint)reader.ReadBits(32));
    }

    /// <inheritdoc/>
    public bool IsChanged(Vector3I lastSent, Vector3I current)
    {
        return lastSent.X != current.X
            || lastSent.Y != current.Y
            || lastSent.Z != current.Z;
    }
}

/// <summary>
/// Кодек <see cref="Vector4"/>: покомпонентно, каждый компонент как 32 сырых бита float.<br/>
/// Изменение определяется покомпонентно по семантике <see cref="float.Equals(float)"/>: NaN равен NaN, -0 равен 0.
/// </summary>
public sealed class Vector4Codec : IReplicationCodec<Vector4>
{
    /// <summary>
    /// Общий экземпляр кодека (кодек не имеет состояния).
    /// </summary>
    public static readonly Vector4Codec Instance = new();

    /// <inheritdoc/>
    public void Write(BitWriter writer, Vector4 value)
    {
        writer.WriteSingle(value.X);
        writer.WriteSingle(value.Y);
        writer.WriteSingle(value.Z);
        writer.WriteSingle(value.W);
    }

    /// <inheritdoc/>
    public Vector4 Read(ref BitReader reader)
    {
        return new Vector4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }

    /// <inheritdoc/>
    public bool IsChanged(Vector4 lastSent, Vector4 current)
    {
        return GodotCodecMath.Differs(lastSent.X, current.X)
            || GodotCodecMath.Differs(lastSent.Y, current.Y)
            || GodotCodecMath.Differs(lastSent.Z, current.Z)
            || GodotCodecMath.Differs(lastSent.W, current.W);
    }
}

/// <summary>
/// Кодек <see cref="Vector4I"/>: покомпонентно, каждый компонент как 32-битное целое.
/// </summary>
public sealed class Vector4ICodec : IReplicationCodec<Vector4I>
{
    /// <summary>
    /// Общий экземпляр кодека (кодек не имеет состояния).
    /// </summary>
    public static readonly Vector4ICodec Instance = new();

    /// <inheritdoc/>
    public void Write(BitWriter writer, Vector4I value)
    {
        writer.WriteBits((uint)value.X, 32);
        writer.WriteBits((uint)value.Y, 32);
        writer.WriteBits((uint)value.Z, 32);
        writer.WriteBits((uint)value.W, 32);
    }

    /// <inheritdoc/>
    public Vector4I Read(ref BitReader reader)
    {
        return new Vector4I((int)(uint)reader.ReadBits(32), (int)(uint)reader.ReadBits(32), (int)(uint)reader.ReadBits(32), (int)(uint)reader.ReadBits(32));
    }

    /// <inheritdoc/>
    public bool IsChanged(Vector4I lastSent, Vector4I current)
    {
        return lastSent.X != current.X
            || lastSent.Y != current.Y
            || lastSent.Z != current.Z
            || lastSent.W != current.W;
    }
}

/// <summary>
/// Кодек <see cref="Color"/>: покомпонентно, каждый компонент как 32 сырых бита float.<br/>
/// Изменение определяется покомпонентно по семантике <see cref="float.Equals(float)"/>: NaN равен NaN, -0 равен 0.
/// </summary>
public sealed class ColorCodec : IReplicationCodec<Color>
{
    /// <summary>
    /// Общий экземпляр кодека (кодек не имеет состояния).
    /// </summary>
    public static readonly ColorCodec Instance = new();

    /// <inheritdoc/>
    public void Write(BitWriter writer, Color value)
    {
        writer.WriteSingle(value.R);
        writer.WriteSingle(value.G);
        writer.WriteSingle(value.B);
        writer.WriteSingle(value.A);
    }

    /// <inheritdoc/>
    public Color Read(ref BitReader reader)
    {
        return new Color(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }

    /// <inheritdoc/>
    public bool IsChanged(Color lastSent, Color current)
    {
        return GodotCodecMath.Differs(lastSent.R, current.R)
            || GodotCodecMath.Differs(lastSent.G, current.G)
            || GodotCodecMath.Differs(lastSent.B, current.B)
            || GodotCodecMath.Differs(lastSent.A, current.A);
    }
}

/// <summary>
/// Кодек <see cref="Quaternion"/>: покомпонентно, каждый компонент как 32 сырых бита float.<br/>
/// Изменение определяется покомпонентно по семантике <see cref="float.Equals(float)"/>: NaN равен NaN, -0 равен 0.
/// </summary>
public sealed class QuaternionCodec : IReplicationCodec<Quaternion>
{
    /// <summary>
    /// Общий экземпляр кодека (кодек не имеет состояния).
    /// </summary>
    public static readonly QuaternionCodec Instance = new();

    /// <inheritdoc/>
    public void Write(BitWriter writer, Quaternion value)
    {
        writer.WriteSingle(value.X);
        writer.WriteSingle(value.Y);
        writer.WriteSingle(value.Z);
        writer.WriteSingle(value.W);
    }

    /// <inheritdoc/>
    public Quaternion Read(ref BitReader reader)
    {
        return new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }

    /// <inheritdoc/>
    public bool IsChanged(Quaternion lastSent, Quaternion current)
    {
        return GodotCodecMath.Differs(lastSent.X, current.X)
            || GodotCodecMath.Differs(lastSent.Y, current.Y)
            || GodotCodecMath.Differs(lastSent.Z, current.Z)
            || GodotCodecMath.Differs(lastSent.W, current.W);
    }
}

/// <summary>
/// Кодек <see cref="Rect2"/>: позиция и размер, покомпонентно, каждый компонент как 32 сырых бита float.<br/>
/// Изменение определяется покомпонентно по семантике <see cref="float.Equals(float)"/>: NaN равен NaN, -0 равен 0.
/// </summary>
public sealed class Rect2Codec : IReplicationCodec<Rect2>
{
    /// <summary>
    /// Общий экземпляр кодека (кодек не имеет состояния).
    /// </summary>
    public static readonly Rect2Codec Instance = new();

    /// <inheritdoc/>
    public void Write(BitWriter writer, Rect2 value)
    {
        Vector2Codec.Instance.Write(writer, value.Position);
        Vector2Codec.Instance.Write(writer, value.Size);
    }

    /// <inheritdoc/>
    public Rect2 Read(ref BitReader reader)
    {
        Vector2 position = Vector2Codec.Instance.Read(ref reader);
        Vector2 size = Vector2Codec.Instance.Read(ref reader);
        return new Rect2(position, size);
    }

    /// <inheritdoc/>
    public bool IsChanged(Rect2 lastSent, Rect2 current)
    {
        return Vector2Codec.Instance.IsChanged(lastSent.Position, current.Position)
            || Vector2Codec.Instance.IsChanged(lastSent.Size, current.Size);
    }
}

/// <summary>
/// Кодек <see cref="Rect2I"/>: позиция и размер, покомпонентно, каждый компонент как 32-битное целое.
/// </summary>
public sealed class Rect2ICodec : IReplicationCodec<Rect2I>
{
    /// <summary>
    /// Общий экземпляр кодека (кодек не имеет состояния).
    /// </summary>
    public static readonly Rect2ICodec Instance = new();

    /// <inheritdoc/>
    public void Write(BitWriter writer, Rect2I value)
    {
        Vector2ICodec.Instance.Write(writer, value.Position);
        Vector2ICodec.Instance.Write(writer, value.Size);
    }

    /// <inheritdoc/>
    public Rect2I Read(ref BitReader reader)
    {
        Vector2I position = Vector2ICodec.Instance.Read(ref reader);
        Vector2I size = Vector2ICodec.Instance.Read(ref reader);
        return new Rect2I(position, size);
    }

    /// <inheritdoc/>
    public bool IsChanged(Rect2I lastSent, Rect2I current)
    {
        return lastSent.Position != current.Position || lastSent.Size != current.Size;
    }
}

internal static class GodotCodecMath
{
    /// <summary>
    /// Сравнение по семантике <see cref="float.Equals(float)"/>: NaN равен NaN, -0 равен 0.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Differs(float a, float b)
    {
        return !a.Equals(b);
    }
}
