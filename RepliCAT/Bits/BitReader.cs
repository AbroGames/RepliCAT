using System.Runtime.CompilerServices;

namespace RepliCAT.Bits;

/// <summary>
/// Побитовый читатель поверх <see cref="ReadOnlySpan{T}"/>, зеркальный к <see cref="BitWriter"/>.<br/>
/// Любой выход за пределы данных, а также varuint длиннее <see cref="BitWriter.MaxVarUIntGroups"/> групп
/// (или не помещающийся в 64 бита) приводят к <see cref="ReplicationFormatException"/>.
/// </summary>
public ref struct BitReader
{
    private readonly ReadOnlySpan<byte> _data;
    private readonly int _bitLength;
    private int _bitPosition;

    /// <summary>
    /// Создает читатель поверх указанных данных.
    /// </summary>
    /// <param name="data">Данные для чтения</param>
    public BitReader(ReadOnlySpan<byte> data)
    {
        if (data.Length > int.MaxValue / 8)
        {
            throw new ReplicationFormatException($"Data is too large: {data.Length} bytes.");
        }

        _data = data;
        _bitLength = data.Length * 8;
        _bitPosition = 0;
    }

    /// <summary>
    /// Текущая позиция чтения в битах.
    /// </summary>
    public readonly int BitPosition => _bitPosition;

    /// <summary>
    /// Количество непрочитанных бит.
    /// </summary>
    public readonly int RemainingBits => _bitLength - _bitPosition;

    /// <summary>
    /// Читает <paramref name="bitCount"/> бит, начиная с младшего.
    /// </summary>
    /// <param name="bitCount">Количество бит, от 0 до 64</param>
    /// <returns>Прочитанное значение в младших битах результата</returns>
    public ulong ReadBits(int bitCount)
    {
        if ((uint)bitCount > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(bitCount), bitCount, "Bit count must be in range 0..64.");
        }

        EnsureAvailable(bitCount);

        ulong result = 0;
        int position = _bitPosition;
        int shift = 0;
        while (shift < bitCount)
        {
            int bitOffset = position & 7;
            int chunk = Math.Min(8 - bitOffset, bitCount - shift);
            ulong bits = (ulong)((_data[position >> 3] >> bitOffset) & ((1 << chunk) - 1));
            result |= bits << shift;

            shift += chunk;
            position += chunk;
        }

        _bitPosition = position;
        return result;
    }

    /// <summary>
    /// Читает булево значение из одного бита.
    /// </summary>
    /// <returns>Прочитанное значение</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ReadBool()
    {
        return ReadBits(1) != 0;
    }

    /// <summary>
    /// Читает беззнаковое число в формате varuint (см. <see cref="BitWriter.WriteVarUInt"/>).
    /// </summary>
    /// <returns>Прочитанное значение</returns>
    public ulong ReadVarUInt()
    {
        ulong result = 0;
        for (int group = 0; group < BitWriter.MaxVarUIntGroups; group++)
        {
            ulong b = ReadBits(8);
            ulong data = b & 0x7F;
            int shift = group * 7;

            // Последняя группа может нести только один (старший, 64-й) бит значения
            if (group == BitWriter.MaxVarUIntGroups - 1 && data > 1)
            {
                throw new ReplicationFormatException("Malformed varuint: value does not fit in 64 bits.");
            }

            result |= data << shift;
            if ((b & 0x80) == 0)
            {
                return result;
            }
        }

        throw new ReplicationFormatException($"Malformed varuint: more than {BitWriter.MaxVarUIntGroups} groups.");
    }

    /// <summary>
    /// Читает знаковое число в формате varint (varuint от zigzag-кодированного значения).
    /// </summary>
    /// <returns>Прочитанное значение</returns>
    public long ReadVarInt()
    {
        return ZigZagDecode(ReadVarUInt());
    }

    /// <summary>
    /// Читает float из 32 сырых бит.
    /// </summary>
    /// <returns>Прочитанное значение</returns>
    public float ReadSingle()
    {
        return BitConverter.UInt32BitsToSingle((uint)ReadBits(32));
    }

    /// <summary>
    /// Читает double из 64 сырых бит.
    /// </summary>
    /// <returns>Прочитанное значение</returns>
    public double ReadDouble()
    {
        return BitConverter.UInt64BitsToDouble(ReadBits(64));
    }

    /// <summary>
    /// Читает байты, по 8 бит каждый, заполняя <paramref name="destination"/> целиком.
    /// </summary>
    /// <param name="destination">Буфер для прочитанных байт</param>
    public void ReadBytes(Span<byte> destination)
    {
        if (destination.IsEmpty)
        {
            return;
        }

        EnsureAvailable((long)destination.Length * 8);

        if ((_bitPosition & 7) == 0)
        {
            _data.Slice(_bitPosition >> 3, destination.Length).CopyTo(destination);
            _bitPosition += destination.Length * 8;
            return;
        }

        for (int i = 0; i < destination.Length; i++)
        {
            destination[i] = (byte)ReadBits(8);
        }
    }

    /// <summary>
    /// Декодирование zigzag, обратное к <see cref="BitWriter.ZigZagEncode"/>.
    /// </summary>
    /// <param name="value">Беззнаковое значение</param>
    /// <returns>Знаковое значение</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ZigZagDecode(ulong value)
    {
        return (long)(value >> 1) ^ -(long)(value & 1);
    }

    private readonly void EnsureAvailable(long bitCount)
    {
        if (bitCount > _bitLength - _bitPosition)
        {
            throw new ReplicationFormatException(
                $"Unexpected end of data: requested {bitCount} bits at position {_bitPosition}, but only {_bitLength - _bitPosition} remain.");
        }
    }
}
