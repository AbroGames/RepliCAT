using System.Collections;

namespace RepliCAT;

/// <summary>
/// Словарь, который можно реплицировать как член объекта с <see cref="ReplicatedAttribute"/>.
/// Обертка над <see cref="Dictionary{TKey, TValue}"/> со счетчиком изменений: репликация синхронизирует
/// состояние "ключ → значение", поэтому передаются только добавленные, удаленные и измененные записи.<br/>
/// Ключи должны быть типами с кодеком значения (примитивы, строки, перечисления, структуры Godot и т.п.);
/// ключи-объекты не поддерживаются. Ключи сравниваются компаратором по умолчанию.<br/>
/// Для значений-объектов с реплицируемыми членами передаются только изменившиеся члены значений.<br/>
/// На получателе словарь нельзя изменять локально: его состояние задает отправитель.
/// </summary>
/// <typeparam name="TKey">Тип ключей: тип с кодеком значения</typeparam>
/// <typeparam name="TValue">Тип значений: тип с кодеком значения, класс с реплицируемыми членами или реплицируемая коллекция</typeparam>
public sealed class ReplicatedDictionary<TKey, TValue> : IDictionary<TKey, TValue>, IReadOnlyDictionary<TKey, TValue>
{
    private readonly Dictionary<TKey, TValue> _dictionary;
    private int _version;

    // Служебное состояние получателя при сбросе, создается лениво.
    private HashSet<TKey> _resetPending;
    private bool _resetting;

    /// <summary>
    /// Создает пустой словарь.
    /// </summary>
    public ReplicatedDictionary()
    {
        _dictionary = new Dictionary<TKey, TValue>();
    }

    /// <summary>
    /// Создает пустой словарь с указанной начальной емкостью.
    /// </summary>
    /// <param name="capacity">Начальная емкость</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> отрицательна</exception>
    public ReplicatedDictionary(int capacity)
    {
        _dictionary = new Dictionary<TKey, TValue>(capacity);
    }

    /// <summary>
    /// Создает словарь, содержащий записи коллекции.
    /// </summary>
    /// <param name="collection">Исходные записи</param>
    /// <exception cref="ArgumentNullException"><paramref name="collection"/> равна <c>null</c> или содержит ключ <c>null</c></exception>
    /// <exception cref="ArgumentException">Коллекция содержит повторяющиеся ключи</exception>
    public ReplicatedDictionary(IEnumerable<KeyValuePair<TKey, TValue>> collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        _dictionary = new Dictionary<TKey, TValue>(collection);
    }

    /// <summary>
    /// Количество записей.
    /// </summary>
    public int Count => _dictionary.Count;

    /// <summary>
    /// Ключи словаря (представление, изменяется вместе со словарем).
    /// </summary>
    public Dictionary<TKey, TValue>.KeyCollection Keys => _dictionary.Keys;

    /// <summary>
    /// Значения словаря (представление, изменяется вместе со словарем).
    /// </summary>
    public Dictionary<TKey, TValue>.ValueCollection Values => _dictionary.Values;

    /// <inheritdoc/>
    ICollection<TKey> IDictionary<TKey, TValue>.Keys => _dictionary.Keys;

    /// <inheritdoc/>
    ICollection<TValue> IDictionary<TKey, TValue>.Values => _dictionary.Values;

    /// <inheritdoc/>
    IEnumerable<TKey> IReadOnlyDictionary<TKey, TValue>.Keys => _dictionary.Keys;

    /// <inheritdoc/>
    IEnumerable<TValue> IReadOnlyDictionary<TKey, TValue>.Values => _dictionary.Values;

    /// <inheritdoc/>
    bool ICollection<KeyValuePair<TKey, TValue>>.IsReadOnly => false;

    /// <summary>
    /// Значение по ключу. Присваивание добавляет запись или заменяет значение существующей.
    /// </summary>
    /// <param name="key">Ключ</param>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> равен <c>null</c></exception>
    /// <exception cref="KeyNotFoundException">При чтении: ключа нет в словаре</exception>
    public TValue this[TKey key]
    {
        get => _dictionary[key];
        set
        {
            _dictionary[key] = value;
            _version++;
        }
    }

    /// <summary>
    /// Добавляет запись.
    /// </summary>
    /// <param name="key">Ключ</param>
    /// <param name="value">Значение</param>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> равен <c>null</c></exception>
    /// <exception cref="ArgumentException">Запись с таким ключом уже есть</exception>
    public void Add(TKey key, TValue value)
    {
        _dictionary.Add(key, value);
        _version++;
    }

    /// <summary>
    /// Добавляет запись, если ключа еще нет в словаре.
    /// </summary>
    /// <param name="key">Ключ</param>
    /// <param name="value">Значение</param>
    /// <returns><c>true</c>, если запись добавлена</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> равен <c>null</c></exception>
    public bool TryAdd(TKey key, TValue value)
    {
        if (!_dictionary.TryAdd(key, value))
        {
            return false;
        }

        _version++;
        return true;
    }

    /// <summary>
    /// Удаляет запись по ключу.
    /// </summary>
    /// <param name="key">Ключ</param>
    /// <returns><c>true</c>, если запись была удалена</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> равен <c>null</c></exception>
    public bool Remove(TKey key)
    {
        if (!_dictionary.Remove(key))
        {
            return false;
        }

        _version++;
        return true;
    }

    /// <summary>
    /// Удаляет запись по ключу и возвращает ее значение.
    /// </summary>
    /// <param name="key">Ключ</param>
    /// <param name="value">Значение удаленной записи или <c>default</c></param>
    /// <returns><c>true</c>, если запись была удалена</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> равен <c>null</c></exception>
    public bool Remove(TKey key, out TValue value)
    {
        if (!_dictionary.Remove(key, out value))
        {
            return false;
        }

        _version++;
        return true;
    }

    /// <summary>
    /// Удаляет все записи.
    /// </summary>
    public void Clear()
    {
        if (_dictionary.Count == 0)
        {
            return;
        }

        _dictionary.Clear();
        _version++;
    }

    /// <summary>
    /// Проверяет, есть ли ключ в словаре.
    /// </summary>
    /// <param name="key">Ключ</param>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> равен <c>null</c></exception>
    public bool ContainsKey(TKey key)
    {
        return _dictionary.ContainsKey(key);
    }

    /// <summary>
    /// Проверяет, есть ли значение в словаре (линейный поиск).
    /// </summary>
    /// <param name="value">Значение</param>
    public bool ContainsValue(TValue value)
    {
        return _dictionary.ContainsValue(value);
    }

    /// <summary>
    /// Возвращает значение по ключу, если ключ есть в словаре.
    /// </summary>
    /// <param name="key">Ключ</param>
    /// <param name="value">Значение или <c>default</c></param>
    /// <returns><c>true</c>, если ключ найден</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> равен <c>null</c></exception>
    public bool TryGetValue(TKey key, out TValue value)
    {
        return _dictionary.TryGetValue(key, out value);
    }

    /// <summary>
    /// Возвращает перечислитель записей (структуру). Изменение словаря во время перечисления приводит
    /// к <see cref="InvalidOperationException"/> так же, как у <see cref="Dictionary{TKey, TValue}"/>.
    /// </summary>
    public Dictionary<TKey, TValue>.Enumerator GetEnumerator()
    {
        return _dictionary.GetEnumerator();
    }

    /// <inheritdoc/>
    IEnumerator<KeyValuePair<TKey, TValue>> IEnumerable<KeyValuePair<TKey, TValue>>.GetEnumerator()
    {
        return _dictionary.GetEnumerator();
    }

    /// <inheritdoc/>
    IEnumerator IEnumerable.GetEnumerator()
    {
        return _dictionary.GetEnumerator();
    }

    /// <inheritdoc/>
    void ICollection<KeyValuePair<TKey, TValue>>.Add(KeyValuePair<TKey, TValue> item)
    {
        Add(item.Key, item.Value);
    }

    /// <inheritdoc/>
    bool ICollection<KeyValuePair<TKey, TValue>>.Contains(KeyValuePair<TKey, TValue> item)
    {
        return ((ICollection<KeyValuePair<TKey, TValue>>)_dictionary).Contains(item);
    }

    /// <inheritdoc/>
    bool ICollection<KeyValuePair<TKey, TValue>>.Remove(KeyValuePair<TKey, TValue> item)
    {
        if (!((ICollection<KeyValuePair<TKey, TValue>>)_dictionary).Remove(item))
        {
            return false;
        }

        _version++;
        return true;
    }

    /// <inheritdoc/>
    void ICollection<KeyValuePair<TKey, TValue>>.CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex)
    {
        ((ICollection<KeyValuePair<TKey, TValue>>)_dictionary).CopyTo(array, arrayIndex);
    }

    // ---------- внутренний API для DictionaryNode ----------

    /// <summary>
    /// Версия словаря: увеличивается при каждом изменении.
    /// </summary>
    internal int Version => _version;

    /// <summary>
    /// Внутренний словарь. Изменять можно только через методы получателя ниже.
    /// </summary>
    internal Dictionary<TKey, TValue> Inner => _dictionary;

    /// <summary>
    /// Получатель: <c>true</c>, если идет сброс и ключ уже был записан во время этого сброса.
    /// </summary>
    internal bool IsDuplicateInReset(TKey key)
    {
        return _resetting && _dictionary.ContainsKey(key) && !_resetPending.Contains(key);
    }

    /// <summary>
    /// Получатель: записывает значение по ключу. Во время сброса (<see cref="BeginReset"/>) ключ
    /// из прежнего содержимого сохраняется (<see cref="EndReset"/> его не удалит).
    /// Проверка на повтор ключа при сбросе — <see cref="IsDuplicateInReset"/>, до чтения значения.
    /// </summary>
    internal void SetEntry(TKey key, TValue value)
    {
        if (_resetting)
        {
            _resetPending.Remove(key);
        }

        _dictionary[key] = value;
        _version++;
    }

    /// <summary>
    /// Получатель: удаляет запись. Отсутствующий ключ игнорируется.
    /// </summary>
    internal void RemoveEntry(TKey key)
    {
        if (_dictionary.Remove(key))
        {
            _version++;
        }
    }

    /// <summary>
    /// Получатель: начинает сброс. Прежние записи остаются до <see cref="EndReset"/>: записанные за время
    /// сброса ключи переиспользуют их значения, остальные удаляются.
    /// </summary>
    internal void BeginReset()
    {
        _resetPending ??= new HashSet<TKey>();
        _resetPending.Clear();
        foreach (TKey key in _dictionary.Keys)
        {
            _resetPending.Add(key);
        }

        _resetting = true;
        _version++;
    }

    /// <summary>
    /// Получатель: завершает сброс и удаляет ключи, которые не были записаны.
    /// </summary>
    internal void EndReset()
    {
        _resetting = false;
        foreach (TKey key in _resetPending)
        {
            _dictionary.Remove(key);
        }

        _resetPending.Clear();
        _version++;
    }
}
