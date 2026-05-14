namespace RepliCAT;

/// <summary>
/// Ограничения системы репликации. Защищают от бесконечной рекурсии (циклы в графе объектов)
/// и от огромных аллокаций при чтении поврежденных или вредоносных данных.
/// </summary>
public sealed class ReplicationLimits
{
    /// <summary>
    /// Значение <see cref="MaxDepth"/> по умолчанию.
    /// </summary>
    public const int DefaultMaxDepth = 64;

    /// <summary>
    /// Значение <see cref="MaxCollectionCount"/> по умолчанию.
    /// </summary>
    public const int DefaultMaxCollectionCount = 65536;

    /// <summary>
    /// Максимальная глубина вложенности объектов и коллекций при записи и чтении.
    /// </summary>
    public int MaxDepth { get; set; } = DefaultMaxDepth;

    /// <summary>
    /// Максимальное количество элементов в реплицируемой коллекции (а также максимальный id слота списка).
    /// </summary>
    public int MaxCollectionCount { get; set; } = DefaultMaxCollectionCount;
}
