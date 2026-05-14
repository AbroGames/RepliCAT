using System.Runtime.CompilerServices;

namespace RepliCAT.Bits;

/// <summary>
/// Побитовый писатель в расширяемый буфер. Биты пишутся начиная с младшего (LSB-first),
/// байты заполняются также начиная с младшего бита.<br/>
/// Любая запись предварительно очищает целевые биты, поэтому после <see cref="Rewind"/>
/// можно писать поверх без дополнительного обнуления.
/// Неиспользуемые старшие биты последнего байта обнуляются в <see cref="ToArray"/> и <see cref="AsSpan"/>.
/// </summary>
public sealed class BitWriter
{
    /// <summary>
    /// Максимальное количество 8-битных групп в varuint.
    /// </summary>
    public const int MaxVarUIntGroups = 10;

    private const int DefaultCapacity = 64;

    private byte[] _buffer;
    private int _bitPosition;

    /// <summary>
    /// Создает писатель с указанной начальной емкостью буфера.
    /// </summary>
    /// <param name="initialCapacityBytes">Начальная емкость буфера в байтах</param>
    public BitWriter(int initialCapacityBytes = DefaultCapacity)
    {
        if (initialCapacityBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(initialCapacityBytes), initialCapacityBytes, "Capacity must be non-negative.");
        }

        _buffer = new byte[Math.Max(initialCapacityBytes, 1)];
    }

    /// <summary>
    /// Текущая позиция записи в битах (количество записанных бит).
    /// </summary>
    public int BitPosition => _bitPosition;

    /// <summary>
    /// Количество байт, занятых записанными битами (с округлением вверх).
    /// </summary>
    public int ByteLength => (_bitPosition + 7) >> 3;

    /// <summary>
    /// Записывает младшие <paramref name="bitCount"/> бит значения, начиная с младшего.
    /// </summary>
    /// <param name="value">Значение</param>
    /// <param name="bitCount">Количество бит, от 0 до 64</param>
    public void WriteBits(ulong value, int bitCount)
    {
        if ((uint)bitCount > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(bitCount), bitCount, "Bit count must be in range 0..64.");
        }

        if (bitCount == 0)
        {
            return;
        }

        EnsureCapacity(_bitPosition + bitCount);

        int position = _bitPosition;
        int remaining = bitCount;
        while (remaining > 0)
        {
            int byteIndex = position >> 3;
            int bitOffset = position & 7;
            int chunk = Math.Min(8 - bitOffset, remaining);
            int mask = ((1 << chunk) - 1) << bitOffset;
            int bits = ((int)value << bitOffset) & mask;
            _buffer[byteIndex] = (byte)((_buffer[byteIndex] & ~mask) | bits);

            value >>= chunk;
            position += chunk;
            remaining -= chunk;
        }

        _bitPosition = position;
    }

    /// <summary>
    /// Записывает булево значение одним битом.
    /// </summary>
    /// <param name="value">Значение</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteBool(bool value)
    {
        WriteBits(value ? 1UL : 0UL, 1);
    }

    /// <summary>
    /// Записывает беззнаковое число в формате varuint: 8-битные группы из 7 бит данных (младшие первыми)
    /// и бита продолжения (бит 7). Не более <see cref="MaxVarUIntGroups"/> групп.
    /// </summary>
    /// <param name="value">Значение</param>
    public void WriteVarUInt(ulong value)
    {
        while (value >= 0x80)
        {
            WriteBits((value & 0x7F) | 0x80, 8);
            value >>= 7;
        }

        WriteBits(value, 8);
    }

    /// <summary>
    /// Записывает знаковое число в формате varint (varuint от zigzag-кодированного значения).
    /// </summary>
    /// <param name="value">Значение</param>
    public void WriteVarInt(long value)
    {
        WriteVarUInt(ZigZagEncode(value));
    }

    /// <summary>
    /// Записывает float как 32 сырых бита.
    /// </summary>
    /// <param name="value">Значение</param>
    public void WriteSingle(float value)
    {
        WriteBits(BitConverter.SingleToUInt32Bits(value), 32);
    }

    /// <summary>
    /// Записывает double как 64 сырых бита.
    /// </summary>
    /// <param name="value">Значение</param>
    public void WriteDouble(double value)
    {
        WriteBits(BitConverter.DoubleToUInt64Bits(value), 64);
    }

    /// <summary>
    /// Записывает байты, по 8 бит каждый. Длина не записывается.
    /// </summary>
    /// <param name="bytes">Байты для записи</param>
    public void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return;
        }

        if ((_bitPosition & 7) == 0)
        {
            EnsureCapacity(_bitPosition + bytes.Length * 8);
            bytes.CopyTo(_buffer.AsSpan(_bitPosition >> 3));
            _bitPosition += bytes.Length * 8;
            return;
        }

        foreach (byte b in bytes)
        {
            WriteBits(b, 8);
        }
    }

    /// <summary>
    /// Возвращает позицию записи назад. Биты после новой позиции считаются незаписанными.
    /// </summary>
    /// <param name="bitPosition">Новая позиция в битах, не больше текущей</param>
    public void Rewind(int bitPosition)
    {
        if (bitPosition < 0 || bitPosition > _bitPosition)
        {
            throw new ArgumentOutOfRangeException(nameof(bitPosition), bitPosition, $"Position must be in range 0..{_bitPosition}.");
        }

        _bitPosition = bitPosition;
    }

    /// <summary>
    /// Перезаписывает один уже записанный бит (например, для патча маски членов).
    /// </summary>
    /// <param name="bitPosition">Позиция бита, меньше текущей позиции записи</param>
    /// <param name="value">Новое значение бита</param>
    public void SetBit(int bitPosition, bool value)
    {
        if (bitPosition < 0 || bitPosition >= _bitPosition)
        {
            throw new ArgumentOutOfRangeException(nameof(bitPosition), bitPosition, $"Position must be in range 0..{_bitPosition - 1}.");
        }

        int byteIndex = bitPosition >> 3;
        int mask = 1 << (bitPosition & 7);
        if (value)
        {
            _buffer[byteIndex] = (byte)(_buffer[byteIndex] | mask);
        }
        else
        {
            _buffer[byteIndex] = (byte)(_buffer[byteIndex] & ~mask);
        }
    }

    /// <summary>
    /// Сбрасывает писатель в начальное состояние, сохраняя выделенный буфер.
    /// </summary>
    public void Reset()
    {
        _bitPosition = 0;
    }

    /// <summary>
    /// Копирует записанные данные в новый массив.
    /// </summary>
    /// <returns>Массив длиной <see cref="ByteLength"/></returns>
    public byte[] ToArray()
    {
        return AsSpan().ToArray();
    }

    /// <summary>
    /// Возвращает записанные данные без копирования. Span действителен до следующей записи в писатель.
    /// </summary>
    /// <returns>Span длиной <see cref="ByteLength"/></returns>
    public ReadOnlySpan<byte> AsSpan()
    {
        ZeroTrailingBits();
        return new ReadOnlySpan<byte>(_buffer, 0, ByteLength);
    }

    /// <summary>
    /// Zigzag-кодирование: отображает знаковые числа на беззнаковые так, что малые по модулю значения дают малые числа.
    /// </summary>
    /// <param name="value">Знаковое значение</param>
    /// <returns>Беззнаковое значение</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ZigZagEncode(long value)
    {
        return (ulong)((value << 1) ^ (value >> 63));
    }

    private void ZeroTrailingBits()
    {
        int bitOffset = _bitPosition & 7;
        if (bitOffset != 0)
        {
            int byteIndex = _bitPosition >> 3;
            _buffer[byteIndex] = (byte)(_buffer[byteIndex] & ((1 << bitOffset) - 1));
        }
    }

    private void EnsureCapacity(int bitCount)
    {
        int requiredBytes = (int)(((long)bitCount + 7) >> 3);
        if (requiredBytes <= _buffer.Length)
        {
            return;
        }

        int newSize = Math.Max(requiredBytes, _buffer.Length * 2);
        Array.Resize(ref _buffer, newSize);
    }
}
