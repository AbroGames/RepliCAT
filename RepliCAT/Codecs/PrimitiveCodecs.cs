using RepliCAT.Bits;

namespace RepliCAT.Codecs;

/// <summary>
/// Кодек булевых значений (1 бит).
/// </summary>
public sealed class BoolCodec : IReplicationCodec<bool>
{
    /// <summary>
    /// Общий экземпляр кодека (кодек не имеет состояния).
    /// </summary>
    public static readonly BoolCodec Instance = new();

    /// <inheritdoc/>
    public void Write(BitWriter writer, bool value)
    {
        writer.WriteBool(value);
    }

    /// <inheritdoc/>
    public bool Read(ref BitReader reader)
    {
        return reader.ReadBool();
    }

    /// <inheritdoc/>
    public bool IsChanged(bool lastSent, bool current)
    {
        return lastSent != current;
    }
}

/// <summary>
/// Кодек <see cref="byte"/> (8 бит) в полную ширину.
/// </summary>
public sealed class ByteCodec : IReplicationCodec<byte>
{
    /// <summary>
    /// Общий экземпляр кодека (кодек не имеет состояния).
    /// </summary>
    public static readonly ByteCodec Instance = new();

    /// <inheritdoc/>
    public void Write(BitWriter writer, byte value)
    {
        writer.WriteBits(value, 8);
    }

    /// <inheritdoc/>
    public byte Read(ref BitReader reader)
    {
        return (byte)reader.ReadBits(8);
    }

    /// <inheritdoc/>
    public bool IsChanged(byte lastSent, byte current)
    {
        return lastSent != current;
    }
}

/// <summary>
/// Кодек <see cref="sbyte"/> (8 бит) в полную ширину.
/// </summary>
public sealed class SByteCodec : IReplicationCodec<sbyte>
{
    /// <summary>
    /// Общий экземпляр кодека (кодек не имеет состояния).
    /// </summary>
    public static readonly SByteCodec Instance = new();

    /// <inheritdoc/>
    public void Write(BitWriter writer, sbyte value)
    {
        writer.WriteBits((byte)value, 8);
    }

    /// <inheritdoc/>
    public sbyte Read(ref BitReader reader)
    {
        return (sbyte)(byte)reader.ReadBits(8);
    }

    /// <inheritdoc/>
    public bool IsChanged(sbyte lastSent, sbyte current)
    {
        return lastSent != current;
    }
}

/// <summary>
/// Кодек <see cref="short"/> (16 бит) в полную ширину.
/// </summary>
public sealed class Int16Codec : IReplicationCodec<short>
{
    /// <summary>
    /// Общий экземпляр кодека (кодек не имеет состояния).
    /// </summary>
    public static readonly Int16Codec Instance = new();

    /// <inheritdoc/>
    public void Write(BitWriter writer, short value)
    {
        writer.WriteBits((ushort)value, 16);
    }

    /// <inheritdoc/>
    public short Read(ref BitReader reader)
    {
        return (short)(ushort)reader.ReadBits(16);
    }

    /// <inheritdoc/>
    public bool IsChanged(short lastSent, short current)
    {
        return lastSent != current;
    }
}

/// <summary>
/// Кодек <see cref="ushort"/> (16 бит) в полную ширину.
/// </summary>
public sealed class UInt16Codec : IReplicationCodec<ushort>
{
    /// <summary>
    /// Общий экземпляр кодека (кодек не имеет состояния).
    /// </summary>
    public static readonly UInt16Codec Instance = new();

    /// <inheritdoc/>
    public void Write(BitWriter writer, ushort value)
    {
        writer.WriteBits(value, 16);
    }

    /// <inheritdoc/>
    public ushort Read(ref BitReader reader)
    {
        return (ushort)reader.ReadBits(16);
    }

    /// <inheritdoc/>
    public bool IsChanged(ushort lastSent, ushort current)
    {
        return lastSent != current;
    }
}

/// <summary>
/// Кодек <see cref="int"/> (32 бита) в полную ширину.
/// </summary>
public sealed class Int32Codec : IReplicationCodec<int>
{
    /// <summary>
    /// Общий экземпляр кодека (кодек не имеет состояния).
    /// </summary>
    public static readonly Int32Codec Instance = new();

    /// <inheritdoc/>
    public void Write(BitWriter writer, int value)
    {
        writer.WriteBits((uint)value, 32);
    }

    /// <inheritdoc/>
    public int Read(ref BitReader reader)
    {
        return (int)(uint)reader.ReadBits(32);
    }

    /// <inheritdoc/>
    public bool IsChanged(int lastSent, int current)
    {
        return lastSent != current;
    }
}

/// <summary>
/// Кодек <see cref="uint"/> (32 бита) в полную ширину.
/// </summary>
public sealed class UInt32Codec : IReplicationCodec<uint>
{
    /// <summary>
    /// Общий экземпляр кодека (кодек не имеет состояния).
    /// </summary>
    public static readonly UInt32Codec Instance = new();

    /// <inheritdoc/>
    public void Write(BitWriter writer, uint value)
    {
        writer.WriteBits(value, 32);
    }

    /// <inheritdoc/>
    public uint Read(ref BitReader reader)
    {
        return (uint)reader.ReadBits(32);
    }

    /// <inheritdoc/>
    public bool IsChanged(uint lastSent, uint current)
    {
        return lastSent != current;
    }
}

/// <summary>
/// Кодек <see cref="long"/> (64 бита) в полную ширину.
/// </summary>
public sealed class Int64Codec : IReplicationCodec<long>
{
    /// <summary>
    /// Общий экземпляр кодека (кодек не имеет состояния).
    /// </summary>
    public static readonly Int64Codec Instance = new();

    /// <inheritdoc/>
    public void Write(BitWriter writer, long value)
    {
        writer.WriteBits((ulong)value, 64);
    }

    /// <inheritdoc/>
    public long Read(ref BitReader reader)
    {
        return (long)reader.ReadBits(64);
    }

    /// <inheritdoc/>
    public bool IsChanged(long lastSent, long current)
    {
        return lastSent != current;
    }
}

/// <summary>
/// Кодек <see cref="ulong"/> (64 бита) в полную ширину.
/// </summary>
public sealed class UInt64Codec : IReplicationCodec<ulong>
{
    /// <summary>
    /// Общий экземпляр кодека (кодек не имеет состояния).
    /// </summary>
    public static readonly UInt64Codec Instance = new();

    /// <inheritdoc/>
    public void Write(BitWriter writer, ulong value)
    {
        writer.WriteBits(value, 64);
    }

    /// <inheritdoc/>
    public ulong Read(ref BitReader reader)
    {
        return reader.ReadBits(64);
    }

    /// <inheritdoc/>
    public bool IsChanged(ulong lastSent, ulong current)
    {
        return lastSent != current;
    }
}

/// <summary>
/// Кодек <see cref="char"/> (16 бит, UTF-16 code unit) в полную ширину.
/// </summary>
public sealed class CharCodec : IReplicationCodec<char>
{
    /// <summary>
    /// Общий экземпляр кодека (кодек не имеет состояния).
    /// </summary>
    public static readonly CharCodec Instance = new();

    /// <inheritdoc/>
    public void Write(BitWriter writer, char value)
    {
        writer.WriteBits(value, 16);
    }

    /// <inheritdoc/>
    public char Read(ref BitReader reader)
    {
        return (char)reader.ReadBits(16);
    }

    /// <inheritdoc/>
    public bool IsChanged(char lastSent, char current)
    {
        return lastSent != current;
    }
}

/// <summary>
/// Кодек <see cref="float"/> как 32 сырых бита.<br/>
/// Изменение определяется по семантике <see cref="float.Equals(float)"/>: NaN равен NaN, -0 равен 0.
/// </summary>
public sealed class SingleCodec : IReplicationCodec<float>
{
    /// <summary>
    /// Общий экземпляр кодека (кодек не имеет состояния).
    /// </summary>
    public static readonly SingleCodec Instance = new();

    /// <inheritdoc/>
    public void Write(BitWriter writer, float value)
    {
        writer.WriteSingle(value);
    }

    /// <inheritdoc/>
    public float Read(ref BitReader reader)
    {
        return reader.ReadSingle();
    }

    /// <inheritdoc/>
    public bool IsChanged(float lastSent, float current)
    {
        return !lastSent.Equals(current);
    }
}

/// <summary>
/// Кодек <see cref="double"/> как 64 сырых бита.<br/>
/// Изменение определяется по семантике <see cref="double.Equals(double)"/>: NaN равен NaN, -0 равен 0.
/// </summary>
public sealed class DoubleCodec : IReplicationCodec<double>
{
    /// <summary>
    /// Общий экземпляр кодека (кодек не имеет состояния).
    /// </summary>
    public static readonly DoubleCodec Instance = new();

    /// <inheritdoc/>
    public void Write(BitWriter writer, double value)
    {
        writer.WriteDouble(value);
    }

    /// <inheritdoc/>
    public double Read(ref BitReader reader)
    {
        return reader.ReadDouble();
    }

    /// <inheritdoc/>
    public bool IsChanged(double lastSent, double current)
    {
        return !lastSent.Equals(current);
    }
}
