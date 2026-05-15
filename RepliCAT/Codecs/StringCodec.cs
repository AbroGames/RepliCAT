using System.Buffers;
using System.Text;
using RepliCAT.Bits;

namespace RepliCAT.Codecs;

/// <summary>
/// Кодек строк. Формат: <c>varuint(byteCount + 1)</c> (0 означает <c>null</c>), затем байты UTF-8 по 8 бит.<br/>
/// Сравнение ординальное. Длина строки в байтах ограничена <see cref="MaxByteCount"/>:
/// запись более длинной строки бросает <see cref="ReplicationException"/>,
/// чтение более длинной строки бросает <see cref="ReplicationFormatException"/>.
/// </summary>
public sealed class StringCodec : IReplicationCodec<string>
{
    /// <summary>
    /// Максимальная длина строки в байтах UTF-8 по умолчанию (64 KiB).
    /// </summary>
    public const int DefaultMaxByteCount = 64 * 1024;

    /// <summary>
    /// Общий экземпляр кодека с лимитом по умолчанию.
    /// </summary>
    public static readonly StringCodec Default = new();

    private const int StackBufferSize = 256;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    /// <summary>
    /// Создает кодек с указанным лимитом длины строки.
    /// </summary>
    /// <param name="maxByteCount">Максимальная длина строки в байтах UTF-8</param>
    public StringCodec(int maxByteCount = DefaultMaxByteCount)
    {
        if (maxByteCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxByteCount), maxByteCount, "Max byte count must be non-negative.");
        }

        MaxByteCount = maxByteCount;
    }

    /// <summary>
    /// Максимальная длина строки в байтах UTF-8.
    /// </summary>
    public int MaxByteCount { get; }

    /// <inheritdoc/>
    public void Write(BitWriter writer, string value)
    {
        if (value == null)
        {
            writer.WriteVarUInt(0);
            return;
        }

        int byteCount = Utf8.GetByteCount(value);
        if (byteCount > MaxByteCount)
        {
            throw new ReplicationException(
                $"String is too long for replication: {byteCount} bytes in UTF-8, the limit is {MaxByteCount} bytes.");
        }

        writer.WriteVarUInt((ulong)byteCount + 1);
        if (byteCount == 0)
        {
            return;
        }

        byte[] rented = null;
        Span<byte> buffer = byteCount <= StackBufferSize
            ? stackalloc byte[StackBufferSize]
            : (rented = ArrayPool<byte>.Shared.Rent(byteCount));
        try
        {
            int written = Utf8.GetBytes(value, buffer);
            writer.WriteBytes(buffer[..written]);
        }
        finally
        {
            if (rented != null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    /// <inheritdoc/>
    public string Read(ref BitReader reader)
    {
        ulong header = reader.ReadVarUInt();
        if (header == 0)
        {
            return null;
        }

        ulong length = header - 1;
        if (length > (ulong)MaxByteCount)
        {
            throw new ReplicationFormatException(
                $"String is too long: {length} bytes, the limit is {MaxByteCount} bytes.");
        }

        int byteCount = (int)length;
        if (byteCount == 0)
        {
            return string.Empty;
        }

        if ((long)byteCount * 8 > reader.RemainingBits)
        {
            throw new ReplicationFormatException(
                $"Unexpected end of data: string of {byteCount} bytes, but only {reader.RemainingBits} bits remain.");
        }

        byte[] rented = null;
        Span<byte> buffer = byteCount <= StackBufferSize
            ? stackalloc byte[StackBufferSize]
            : (rented = ArrayPool<byte>.Shared.Rent(byteCount));
        try
        {
            buffer = buffer[..byteCount];
            reader.ReadBytes(buffer);
            return Utf8.GetString(buffer);
        }
        finally
        {
            if (rented != null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    /// <inheritdoc/>
    public bool IsChanged(string lastSent, string current)
    {
        return !string.Equals(lastSent, current, StringComparison.Ordinal);
    }
}
