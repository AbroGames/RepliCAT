using System.Linq.Expressions;
using System.Reflection;

namespace RepliCAT.Reflection;

/// <summary>
/// Фабрика строго типизированных делегатов доступа к полям и свойствам.
/// Делегаты компилируются из Expression trees, поэтому доступ к значимым типам происходит без упаковки
/// (если <c>TValue</c> совпадает с типом члена). Поддерживаются непубличные члены и непубличные аксессоры свойств.
/// </summary>
/// <remarks>
/// Владелец приводится к <see cref="MemberInfo.DeclaringType"/> члена, поэтому <c>TOwner</c> может быть
/// как самим объявляющим типом, так и его наследником или базовым типом (например, <see cref="object"/>).
/// Значение приводится к типу члена и обратно, если <c>TValue</c> с ним не совпадает
/// (допустимы любые преобразования, поддерживаемые <see cref="Expression.Convert(Expression, Type)"/>).
/// Для статических членов аргумент-владелец игнорируется.
/// </remarks>
public static class TypedAccessors
{
    /// <summary>
    /// Создает делегат, читающий значение поля или свойства.
    /// </summary>
    /// <typeparam name="TOwner">Тип владельца, через который происходит доступ.</typeparam>
    /// <typeparam name="TValue">Тип возвращаемого значения.</typeparam>
    /// <param name="member">Поле или свойство.</param>
    /// <returns>Скомпилированный делегат чтения.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="member"/> равен <c>null</c>.</exception>
    /// <exception cref="ArgumentException">
    /// Член не является полем или свойством, является индексатором, у свойства нет геттера,
    /// либо <typeparamref name="TOwner"/> или <typeparamref name="TValue"/> несовместимы с членом.
    /// </exception>
    public static Func<TOwner, TValue> CreateGetter<TOwner, TValue>(MemberInfo member)
    {
        ValidateMember(member);
        member = Normalize(member);

        if (member is PropertyInfo property && property.GetGetMethod(true) is null)
        {
            throw new ArgumentException($"Property {GetPath(member)} has no getter.", nameof(member));
        }

        try
        {
            var ownerParam = Expression.Parameter(typeof(TOwner), "owner");

            Expression body;
            if (member is FieldInfo { IsLiteral: true } literal)
            {
                // Константы нельзя прочитать через ldsfld, их значение известно на этапе компиляции.
                // GetValue, а не GetRawConstantValue: для enum-констант последний возвращает значение базового типа
                body = Expression.Constant(literal.GetValue(null), literal.FieldType);
            }
            else
            {
                body = Expression.MakeMemberAccess(ConvertOwner<TOwner>(ownerParam, member), member);
            }

            body = ConvertIfNeeded(body, typeof(TValue));

            return Expression.Lambda<Func<TOwner, TValue>>(body, ownerParam).Compile();
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException)
        {
            throw new ArgumentException(
                $"Failed to create getter {typeof(TOwner).Name} -> {typeof(TValue).Name} for {GetPath(member)}: {e.Message}",
                nameof(member), e);
        }
    }

    /// <summary>
    /// Создает делегат, записывающий значение поля или свойства.
    /// </summary>
    /// <typeparam name="TOwner">Тип владельца, через который происходит доступ.</typeparam>
    /// <typeparam name="TValue">Тип записываемого значения.</typeparam>
    /// <param name="member">Поле или свойство.</param>
    /// <returns>
    /// Скомпилированный делегат записи или <c>null</c>, если член недоступен для записи
    /// (readonly-поле, константа, свойство без сеттера). См. <see cref="CanWrite"/>.
    /// Init-only сеттеры считаются доступными для записи.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="member"/> равен <c>null</c>.</exception>
    /// <exception cref="ArgumentException">
    /// Член не является полем или свойством, является индексатором,
    /// является экземплярным членом значимого типа (запись изменила бы копию),
    /// либо <typeparamref name="TOwner"/> или <typeparamref name="TValue"/> несовместимы с членом.
    /// </exception>
    public static Action<TOwner, TValue> CreateSetter<TOwner, TValue>(MemberInfo member)
    {
        ValidateMember(member);
        member = Normalize(member);

        if (!CanWrite(member))
        {
            return null;
        }

        if (!IsStatic(member) && member.DeclaringType.IsValueType)
        {
            throw new ArgumentException(
                $"Cannot create setter for {GetPath(member)}: the declaring type is a value type, the write would modify a copy.",
                nameof(member));
        }

        try
        {
            var ownerParam = Expression.Parameter(typeof(TOwner), "owner");
            var valueParam = Expression.Parameter(typeof(TValue), "value");

            var access = Expression.MakeMemberAccess(ConvertOwner<TOwner>(ownerParam, member), member);
            var assign = Expression.Assign(access, ConvertIfNeeded(valueParam, access.Type));

            return Expression.Lambda<Action<TOwner, TValue>>(assign, ownerParam, valueParam).Compile();
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException)
        {
            throw new ArgumentException(
                $"Failed to create setter {typeof(TOwner).Name} <- {typeof(TValue).Name} for {GetPath(member)}: {e.Message}",
                nameof(member), e);
        }
    }

    /// <summary>
    /// Проверяет, можно ли записать значение в член.
    /// </summary>
    /// <param name="member">Поле или свойство.</param>
    /// <returns>
    /// <c>false</c> для readonly-полей, констант и свойств без сеттера (в том числе непубличного);
    /// <c>true</c> для остальных полей и свойств, включая свойства с init-only сеттером.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="member"/> равен <c>null</c>.</exception>
    /// <exception cref="ArgumentException">Член не является полем или свойством.</exception>
    public static bool CanWrite(MemberInfo member)
    {
        ArgumentNullException.ThrowIfNull(member);
        member = Normalize(member);

        return member switch
        {
            FieldInfo field => !field.IsInitOnly && !field.IsLiteral,
            PropertyInfo property => property.GetSetMethod(true) is not null,
            _ => throw new ArgumentException($"Member {GetPath(member)} is neither a field nor a property.", nameof(member))
        };
    }

    /// <summary>
    /// Приводит свойство к экземпляру <see cref="PropertyInfo"/> объявляющего типа.
    /// Свойство, полученное через наследника (<see cref="MemberInfo.ReflectedType"/> != <see cref="MemberInfo.DeclaringType"/>),
    /// не отдает приватные аксессоры базового типа, поэтому без нормализации приватный геттер или сеттер был бы не виден.
    /// </summary>
    private static MemberInfo Normalize(MemberInfo member)
    {
        if (member is PropertyInfo property && property.DeclaringType is not null && property.ReflectedType != property.DeclaringType)
        {
            const BindingFlags flags = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static
                                       | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (var declared in property.DeclaringType.GetProperties(flags))
            {
                if (declared.HasSameMetadataDefinitionAs(property))
                {
                    return declared;
                }
            }
        }

        return member;
    }

    private static void ValidateMember(MemberInfo member)
    {
        ArgumentNullException.ThrowIfNull(member);

        if (member is not FieldInfo and not PropertyInfo)
        {
            throw new ArgumentException($"Member {GetPath(member)} is neither a field nor a property.", nameof(member));
        }

        if (member is PropertyInfo property && property.GetIndexParameters().Length > 0)
        {
            throw new ArgumentException($"Indexer {GetPath(member)} is not supported.", nameof(member));
        }

        if (member.DeclaringType is null)
        {
            throw new ArgumentException($"Member {member.Name} has no declaring type.", nameof(member));
        }
    }

    private static Expression ConvertOwner<TOwner>(ParameterExpression ownerParam, MemberInfo member)
    {
        if (IsStatic(member))
        {
            return null;
        }

        var declaringType = member.DeclaringType;
        if (!declaringType.IsAssignableFrom(typeof(TOwner)) && !typeof(TOwner).IsAssignableFrom(declaringType))
        {
            throw new ArgumentException(
                $"Owner type {typeof(TOwner).FullName} is not compatible with {declaringType.FullName}.");
        }

        return ConvertIfNeeded(ownerParam, declaringType);
    }

    private static Expression ConvertIfNeeded(Expression expression, Type type)
    {
        return expression.Type == type ? expression : Expression.Convert(expression, type);
    }

    private static bool IsStatic(MemberInfo member)
    {
        return member switch
        {
            FieldInfo field => field.IsStatic,
            PropertyInfo property => (property.GetGetMethod(true) ?? property.GetSetMethod(true)).IsStatic,
            _ => false
        };
    }

    private static string GetPath(MemberInfo member)
    {
        return $"{member.DeclaringType?.FullName}.{member.Name}";
    }
}
