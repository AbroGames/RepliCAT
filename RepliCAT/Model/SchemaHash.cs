using System.Reflection;
using System.Text;
using RepliCAT.Codecs;

namespace RepliCAT.Model;

/// <summary>
/// Хэш схемы: FNV-1a 64 над каноническим описанием модели типа.
/// Описание не зависит от сборок и их версий (имена типов пишутся без assembly-qualified аргументов)
/// и от текущей культуры (числа пишутся в инвариантной культуре).
/// </summary>
internal static class SchemaHash
{
    private const ulong FnvOffsetBasis = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    /// <summary>
    /// Вычисляет FNV-1a 64 над UTF-8 представлением строки.
    /// </summary>
    public static ulong Compute(string canonical)
    {
        ulong hash = FnvOffsetBasis;
        foreach (byte b in Encoding.UTF8.GetBytes(canonical))
        {
            hash ^= b;
            hash *= FnvPrime;
        }

        return hash;
    }

    /// <summary>
    /// Возвращает имя типа с пространством имен, без информации о сборке.
    /// Аргументы обобщенных типов форматируются рекурсивно так же.
    /// </summary>
    public static string FormatTypeName(Type type)
    {
        var sb = new StringBuilder();
        AppendTypeName(sb, type);
        return sb.ToString();
    }

    /// <summary>
    /// Дописывает имя типа (см. <see cref="FormatTypeName"/>).
    /// </summary>
    public static void AppendTypeName(StringBuilder sb, Type type)
    {
        if (type.IsArray)
        {
            AppendTypeName(sb, type.GetElementType());
            sb.Append('[').Append(',', type.GetArrayRank() - 1).Append(']');
            return;
        }

        if (type.IsGenericType && !type.IsGenericTypeDefinition)
        {
            Type definition = type.GetGenericTypeDefinition();
            sb.Append(definition.FullName ?? definition.Name).Append('[');
            Type[] arguments = type.GetGenericArguments();
            for (int i = 0; i < arguments.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                AppendTypeName(sb, arguments[i]);
            }

            sb.Append(']');
            return;
        }

        sb.Append(type.FullName ?? type.Name);
    }

    /// <summary>
    /// Дописывает описание кодека: его тип и параметры, влияющие на формат на проводе
    /// (ширина перечисления, лимит строки, внутренний кодек <see cref="Nullable{T}"/>).
    /// Для пользовательских кодеков учитывается только тип кодека.
    /// </summary>
    public static void AppendCodec(StringBuilder sb, object codec)
    {
        Type type = codec.GetType();
        AppendTypeName(sb, type);

        if (codec is StringCodec stringCodec)
        {
            sb.Append("(max=").Append(stringCodec.MaxByteCount).Append(')');
            return;
        }

        if (!type.IsGenericType)
        {
            return;
        }

        Type definition = type.GetGenericTypeDefinition();
        if (definition == typeof(EnumCodec<>))
        {
            object bitCount = type.GetProperty(nameof(EnumCodec<DayOfWeek>.BitCount), BindingFlags.Public | BindingFlags.Instance)
                .GetValue(codec);
            object compact = type.GetProperty(nameof(EnumCodec<DayOfWeek>.IsCompact), BindingFlags.Public | BindingFlags.Instance)
                .GetValue(codec);
            sb.Append("(bits=").Append((int)bitCount).Append(",compact=").Append((bool)compact ? '1' : '0').Append(')');
        }
        else if (definition == typeof(NullableCodec<>))
        {
            object inner = type.GetProperty(nameof(NullableCodec<int>.Inner), BindingFlags.Public | BindingFlags.Instance)
                .GetValue(codec);
            sb.Append('(');
            AppendCodec(sb, inner);
            sb.Append(')');
        }
    }
}
