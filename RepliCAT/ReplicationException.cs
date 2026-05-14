namespace RepliCAT;

/// <summary>
/// Ошибка конфигурации или использования системы репликации: неподдерживаемый тип члена,
/// некорректные параметры атрибутов, неверные аргументы API и т.п.
/// Сообщение, как правило, содержит путь к члену (<c>DeclaringType.FullName.MemberName</c>).
/// </summary>
public class ReplicationException : Exception
{
    /// <summary>
    /// Создает исключение с указанным сообщением.
    /// </summary>
    /// <param name="message">Описание ошибки</param>
    public ReplicationException(string message) : base(message)
    {
    }

    /// <summary>
    /// Создает исключение с указанным сообщением и внутренним исключением.
    /// </summary>
    /// <param name="message">Описание ошибки</param>
    /// <param name="innerException">Исключение, ставшее причиной ошибки</param>
    public ReplicationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// Ошибка формата входных данных: данные репликации повреждены, обрезаны или не соответствуют схеме.
/// Это единственный тип исключения, который может возникнуть при применении некорректных данных.
/// </summary>
public class ReplicationFormatException : ReplicationException
{
    /// <summary>
    /// Создает исключение с указанным сообщением.
    /// </summary>
    /// <param name="message">Описание ошибки</param>
    public ReplicationFormatException(string message) : base(message)
    {
    }

    /// <summary>
    /// Создает исключение с указанным сообщением и внутренним исключением.
    /// </summary>
    /// <param name="message">Описание ошибки</param>
    /// <param name="innerException">Исключение, ставшее причиной ошибки</param>
    public ReplicationFormatException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
