using System.Numerics;
using System.Runtime.CompilerServices;
using RepliCAT.Bits;

namespace RepliCAT.Codecs;

/// <summary>
/// Кодек перечислений без упаковки (через <see cref="Unsafe.As{TFrom,TTo}(ref TFrom)"/>).<br/>
/// Перечисления без <see cref="FlagsAttribute"/>, все определенные значения которых неотрицательны,
/// пишутся компактно: <c>max(1, bitLength(maxDefined))</c> бит. Запись значения больше максимального
/// определенного бросает <see cref="ReplicationException"/>, чтение такого значения —
/// <see cref="ReplicationFormatException"/>. Неопределенные значения внутри диапазона допустимы.<br/>
/// Остальные перечисления (флаги, отрицательные значения) пишутся сырыми битами в полную ширину базового типа.
/// </summary>
/// <typeparam name="TEnum">Тип перечисления</typeparam>
public sealed class EnumCodec<TEnum> : IReplicationCodec<TEnum> where TEnum : unmanaged, Enum
{
    private static readonly int Size = Unsafe.SizeOf<TEnum>();

    private readonly bool _compact;
    private readonly ulong _maxDefined;

    /// <summary>
    /// Создает кодек, анализируя определенные значения перечисления.
    /// </summary>
    public EnumCodec()
    {
        Type type = typeof(TEnum);
        bool isFlags = type.IsDefined(typeof(FlagsAttribute), false);
        bool isSigned = IsSignedUnderlying(Enum.GetUnderlyingType(type));

        bool allNonNegative = true;
        ulong maxDefined = 0;
        foreach (TEnum value in Enum.GetValues<TEnum>())
        {
            ulong raw = ToRaw(value);
            if (isSigned && IsNegative(raw))
            {
                allNonNegative = false;
                break;
            }

            maxDefined = Math.Max(maxDefined, raw);
        }

        _compact = !isFlags && allNonNegative;
        _maxDefined = _compact ? maxDefined : 0;
        BitCount = _compact ? Math.Max(1, 64 - BitOperations.LeadingZeroCount(maxDefined)) : Size * 8;
    }

    /// <summary>
    /// Количество бит, которое занимает одно значение на проводе.
    /// </summary>
    public int BitCount { get; }

    /// <summary>
    /// <c>true</c>, если используется компактная запись (по максимальному определенному значению).
    /// </summary>
    public bool IsCompact => _compact;

    /// <inheritdoc/>
    public void Write(BitWriter writer, TEnum value)
    {
        ulong raw = ToRaw(value);
        if (_compact && raw > _maxDefined)
        {
            throw new ReplicationException(
                $"Value {value} of enum {typeof(TEnum).FullName} is out of range: the maximum defined value is {_maxDefined}.");
        }

        writer.WriteBits(raw, BitCount);
    }

    /// <inheritdoc/>
    public TEnum Read(ref BitReader reader)
    {
        ulong raw = reader.ReadBits(BitCount);
        if (_compact && raw > _maxDefined)
        {
            throw new ReplicationFormatException(
                $"Value {raw} of enum {typeof(TEnum).FullName} is out of range: the maximum defined value is {_maxDefined}.");
        }

        return FromRaw(raw);
    }

    /// <inheritdoc/>
    public bool IsChanged(TEnum lastSent, TEnum current)
    {
        return ToRaw(lastSent) != ToRaw(current);
    }

    /// <summary>
    /// Сырые биты значения, дополненные нулями до 64 бит (без расширения знака).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ToRaw(TEnum value)
    {
        switch (Size)
        {
            case 1:
                return Unsafe.As<TEnum, byte>(ref value);
            case 2:
                return Unsafe.As<TEnum, ushort>(ref value);
            case 4:
                return Unsafe.As<TEnum, uint>(ref value);
            default:
                return Unsafe.As<TEnum, ulong>(ref value);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static TEnum FromRaw(ulong raw)
    {
        switch (Size)
        {
            case 1:
            {
                byte b = (byte)raw;
                return Unsafe.As<byte, TEnum>(ref b);
            }
            case 2:
            {
                ushort s = (ushort)raw;
                return Unsafe.As<ushort, TEnum>(ref s);
            }
            case 4:
            {
                uint i = (uint)raw;
                return Unsafe.As<uint, TEnum>(ref i);
            }
            default:
                return Unsafe.As<ulong, TEnum>(ref raw);
        }
    }

    private static bool IsNegative(ulong raw)
    {
        return (raw >> (Size * 8 - 1)) != 0;
    }

    private static bool IsSignedUnderlying(Type underlying)
    {
        return underlying == typeof(sbyte) || underlying == typeof(short)
            || underlying == typeof(int) || underlying == typeof(long);
    }
}
