namespace RepliCAT;

/// <summary>
/// Двустороннее отображение между типами и их целочисленными идентификаторами.
/// Используется, например, для передачи информации о полиморфных типах по сети.
/// Отображение должно совпадать на всех сторонах, которые обмениваются идентификаторами.
/// </summary>
public interface ITypeIdMapping
{
    /// <summary>
    /// Возвращает идентификатор типа.
    /// </summary>
    /// <param name="type">Тип, для которого нужен идентификатор.</param>
    /// <returns>Идентификатор типа.</returns>
    /// <exception cref="KeyNotFoundException">Тип не зарегистрирован в отображении.</exception>
    int GetId(Type type);

    /// <summary>
    /// Возвращает тип по его идентификатору.
    /// </summary>
    /// <param name="id">Идентификатор типа.</param>
    /// <returns>Тип с указанным идентификатором.</returns>
    /// <exception cref="KeyNotFoundException">Идентификатор не зарегистрирован в отображении.</exception>
    /// <remarks>
    /// Репликация передает сюда идентификаторы, прочитанные из сети, и считает ошибкой формата
    /// (<c>ReplicationFormatException</c>) любое исключение этого метода и результат <c>null</c>.
    /// </remarks>
    Type GetType(int id);
}
