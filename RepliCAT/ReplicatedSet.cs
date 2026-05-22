using System.Collections;
using RepliCAT.Nodes;

namespace RepliCAT;

/// <summary>
/// Множество, которое можно реплицировать как член объекта с <see cref="ReplicatedAttribute"/>.
/// Ведет себя как <see cref="HashSet{T}"/>, но построено на <see cref="ReplicatedDictionary{TKey, TValue}"/>:
/// элементы — ключи словаря с пустыми значениями. Репликация синхронизирует состав множества,
/// поэтому передаются только добавленные и удаленные элементы, а значения на провод не попадают.<br/>
/// Элементы должны быть типами с кодеком значения (примитивы, строки, перечисления, структуры Godot и т.п.);
/// элементы-объекты не поддерживаются. Элементы сравниваются компаратором по умолчанию,
/// <c>null</c> элементом быть не может.<br/>
/// На получателе множество нельзя изменять локально: его состояние задает отправитель.
/// </summary>
/// <typeparam name="T">Тип элементов: тип с кодеком значения</typeparam>
public sealed class ReplicatedSet<T> : ISet<T>, IReadOnlySet<T>
{
    private readonly ReplicatedDictionary<T, NoValue> _dictionary;

    /// <summary>
    /// Создает пустое множество.
    /// </summary>
    public ReplicatedSet()
    {
        _dictionary = new ReplicatedDictionary<T, NoValue>();
    }

    /// <summary>
    /// Создает пустое множество с указанной начальной емкостью.
    /// </summary>
    /// <param name="capacity">Начальная емкость</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> отрицательна</exception>
    public ReplicatedSet(int capacity)
    {
        _dictionary = new ReplicatedDictionary<T, NoValue>(capacity);
    }

    /// <summary>
    /// Создает множество, содержащее элементы коллекции. Повторяющиеся элементы добавляются один раз.
    /// </summary>
    /// <param name="collection">Исходные элементы</param>
    /// <exception cref="ArgumentNullException"><paramref name="collection"/> равна <c>null</c> или содержит <c>null</c></exception>
    public ReplicatedSet(IEnumerable<T> collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        _dictionary = new ReplicatedDictionary<T, NoValue>(collection.TryGetNonEnumeratedCount(out int count) ? count : 0);
        UnionWith(collection);
    }

    /// <summary>
    /// Получатель: создает множество над словарем, прочитанным узлом словаря.
    /// </summary>
    internal ReplicatedSet(ReplicatedDictionary<T, NoValue> dictionary)
    {
        _dictionary = dictionary;
    }

    /// <summary>
    /// Количество элементов.
    /// </summary>
    public int Count => _dictionary.Count;

    /// <inheritdoc/>
    bool ICollection<T>.IsReadOnly => false;

    /// <summary>
    /// Добавляет элемент.
    /// </summary>
    /// <param name="item">Элемент</param>
    /// <returns><c>true</c>, если элемент добавлен (его еще не было в множестве)</returns>
    /// <exception cref="ArgumentNullException"><paramref name="item"/> равен <c>null</c></exception>
    public bool Add(T item)
    {
        return _dictionary.TryAdd(item, default);
    }

    /// <summary>
    /// Удаляет элемент.
    /// </summary>
    /// <param name="item">Элемент</param>
    /// <returns><c>true</c>, если элемент был удален</returns>
    /// <exception cref="ArgumentNullException"><paramref name="item"/> равен <c>null</c></exception>
    public bool Remove(T item)
    {
        return _dictionary.Remove(item);
    }

    /// <summary>
    /// Удаляет все элементы, удовлетворяющие условию.
    /// </summary>
    /// <param name="match">Условие удаления</param>
    /// <returns>Количество удаленных элементов</returns>
    /// <exception cref="ArgumentNullException"><paramref name="match"/> равно <c>null</c></exception>
    public int RemoveWhere(Predicate<T> match)
    {
        ArgumentNullException.ThrowIfNull(match);

        // Удаление во время перечисления Dictionary допустимо (.NET Core 3.0+).
        int removed = 0;
        foreach (T item in _dictionary.Keys)
        {
            if (match(item) && _dictionary.Remove(item))
            {
                removed++;
            }
        }

        return removed;
    }

    /// <summary>
    /// Удаляет все элементы.
    /// </summary>
    public void Clear()
    {
        _dictionary.Clear();
    }

    /// <summary>
    /// Проверяет, есть ли элемент в множестве.
    /// </summary>
    /// <param name="item">Элемент</param>
    /// <exception cref="ArgumentNullException"><paramref name="item"/> равен <c>null</c></exception>
    public bool Contains(T item)
    {
        return _dictionary.ContainsKey(item);
    }

    /// <summary>
    /// Копирует элементы в массив.
    /// </summary>
    /// <param name="array">Массив назначения</param>
    /// <param name="arrayIndex">Индекс в массиве, с которого начинается копирование</param>
    /// <exception cref="ArgumentNullException"><paramref name="array"/> равен <c>null</c></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="arrayIndex"/> вне массива</exception>
    /// <exception cref="ArgumentException">Элементы не помещаются в массив</exception>
    public void CopyTo(T[] array, int arrayIndex)
    {
        _dictionary.Keys.CopyTo(array, arrayIndex);
    }

    // ---------- операции над множествами ----------
    // Изменяющие операции бросают ArgumentNullException, только если null пришлось бы добавить.
    // Проверки считают, что null в other в множестве отсутствует.

    /// <summary>
    /// Добавляет все элементы коллекции.
    /// </summary>
    /// <param name="other">Коллекция</param>
    /// <exception cref="ArgumentNullException"><paramref name="other"/> равна <c>null</c> или содержит <c>null</c></exception>
    public void UnionWith(IEnumerable<T> other)
    {
        ArgumentNullException.ThrowIfNull(other);

        foreach (T item in other)
        {
            Add(item);
        }
    }

    /// <summary>
    /// Оставляет только элементы, которые есть в коллекции.
    /// </summary>
    /// <param name="other">Коллекция</param>
    /// <exception cref="ArgumentNullException"><paramref name="other"/> равна <c>null</c></exception>
    public void IntersectWith(IEnumerable<T> other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (Count == 0 || ReferenceEquals(other, this))
        {
            return;
        }

        IReadOnlySet<T> set = AsReadOnlySet(other);
        if (set.Count == 0)
        {
            Clear();
            return;
        }

        foreach (T item in _dictionary.Keys)
        {
            if (!set.Contains(item))
            {
                _dictionary.Remove(item);
            }
        }
    }

    /// <summary>
    /// Удаляет элементы, которые есть в коллекции.
    /// </summary>
    /// <param name="other">Коллекция</param>
    /// <exception cref="ArgumentNullException"><paramref name="other"/> равна <c>null</c></exception>
    public void ExceptWith(IEnumerable<T> other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (Count == 0)
        {
            return;
        }

        if (ReferenceEquals(other, this))
        {
            Clear();
            return;
        }

        foreach (T item in other)
        {
            if (item is not null)
            {
                _dictionary.Remove(item);
            }
        }
    }

    /// <summary>
    /// Оставляет элементы, которые есть либо в множестве, либо в коллекции, но не там и там одновременно.
    /// </summary>
    /// <param name="other">Коллекция</param>
    /// <exception cref="ArgumentNullException"><paramref name="other"/> равна <c>null</c> или содержит <c>null</c></exception>
    public void SymmetricExceptWith(IEnumerable<T> other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (Count == 0)
        {
            UnionWith(other);
            return;
        }

        if (ReferenceEquals(other, this))
        {
            Clear();
            return;
        }

        // Повторы в other не должны переключать элемент дважды.
        foreach (T item in AsReadOnlySet(other))
        {
            if (!Remove(item))
            {
                Add(item);
            }
        }
    }

    /// <summary>
    /// Проверяет, что все элементы множества есть в коллекции.
    /// </summary>
    /// <param name="other">Коллекция</param>
    /// <exception cref="ArgumentNullException"><paramref name="other"/> равна <c>null</c></exception>
    public bool IsSubsetOf(IEnumerable<T> other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (Count == 0)
        {
            return true;
        }

        IReadOnlySet<T> set = AsReadOnlySet(other);
        return Count <= set.Count && AllItemsIn(set);
    }

    /// <summary>
    /// Проверяет, что множество — собственное подмножество коллекции: все его элементы есть в коллекции,
    /// а в коллекции есть и другие элементы.
    /// </summary>
    /// <param name="other">Коллекция</param>
    /// <exception cref="ArgumentNullException"><paramref name="other"/> равна <c>null</c></exception>
    public bool IsProperSubsetOf(IEnumerable<T> other)
    {
        ArgumentNullException.ThrowIfNull(other);

        IReadOnlySet<T> set = AsReadOnlySet(other);
        return Count < set.Count && AllItemsIn(set);
    }

    /// <summary>
    /// Проверяет, что все элементы коллекции есть в множестве.
    /// </summary>
    /// <param name="other">Коллекция</param>
    /// <exception cref="ArgumentNullException"><paramref name="other"/> равна <c>null</c></exception>
    public bool IsSupersetOf(IEnumerable<T> other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return ContainsAll(other);
    }

    /// <summary>
    /// Проверяет, что множество — собственное надмножество коллекции: все элементы коллекции есть в множестве,
    /// а в множестве есть и другие элементы.
    /// </summary>
    /// <param name="other">Коллекция</param>
    /// <exception cref="ArgumentNullException"><paramref name="other"/> равна <c>null</c></exception>
    public bool IsProperSupersetOf(IEnumerable<T> other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (Count == 0)
        {
            return false;
        }

        IReadOnlySet<T> set = AsReadOnlySet(other);
        return set.Count < Count && ContainsAll(set);
    }

    /// <summary>
    /// Проверяет, есть ли у множества и коллекции общие элементы.
    /// </summary>
    /// <param name="other">Коллекция</param>
    /// <exception cref="ArgumentNullException"><paramref name="other"/> равна <c>null</c></exception>
    public bool Overlaps(IEnumerable<T> other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (Count == 0)
        {
            return false;
        }

        foreach (T item in other)
        {
            if (item is not null && _dictionary.ContainsKey(item))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Проверяет, что множество и коллекция содержат одни и те же элементы (без учета повторов в коллекции).
    /// </summary>
    /// <param name="other">Коллекция</param>
    /// <exception cref="ArgumentNullException"><paramref name="other"/> равна <c>null</c></exception>
    public bool SetEquals(IEnumerable<T> other)
    {
        ArgumentNullException.ThrowIfNull(other);

        IReadOnlySet<T> set = AsReadOnlySet(other);
        return set.Count == Count && ContainsAll(set);
    }

    /// <summary>
    /// Возвращает перечислитель элементов (структуру). Изменение множества во время перечисления приводит
    /// к <see cref="InvalidOperationException"/> так же, как у <see cref="Dictionary{TKey, TValue}"/>.
    /// </summary>
    public Enumerator GetEnumerator()
    {
        return new Enumerator(_dictionary.Keys.GetEnumerator());
    }

    /// <inheritdoc/>
    IEnumerator<T> IEnumerable<T>.GetEnumerator()
    {
        return GetEnumerator();
    }

    /// <inheritdoc/>
    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    /// <inheritdoc/>
    void ICollection<T>.Add(T item)
    {
        Add(item);
    }

    /// <summary>
    /// Коллекция как множество с компаратором по умолчанию: сама коллекция, если она уже такое множество,
    /// иначе копия без повторов.
    /// </summary>
    private static IReadOnlySet<T> AsReadOnlySet(IEnumerable<T> other)
    {
        if (other is ReplicatedSet<T> replicated)
        {
            return replicated;
        }

        if (other is HashSet<T> hashSet && hashSet.Comparer.Equals(EqualityComparer<T>.Default))
        {
            return hashSet;
        }

        return new HashSet<T>(other);
    }

    /// <summary>
    /// Проверяет, что все элементы множества есть в <paramref name="set"/>.
    /// </summary>
    private bool AllItemsIn(IReadOnlySet<T> set)
    {
        foreach (T item in _dictionary.Keys)
        {
            if (!set.Contains(item))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Проверяет, что все элементы <paramref name="other"/> есть в множестве (<c>null</c> — нет).
    /// </summary>
    private bool ContainsAll(IEnumerable<T> other)
    {
        foreach (T item in other)
        {
            if (item is null || !_dictionary.ContainsKey(item))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Перечислитель <see cref="ReplicatedSet{T}"/>.
    /// </summary>
    public struct Enumerator : IEnumerator<T>
    {
        private Dictionary<T, NoValue>.KeyCollection.Enumerator _inner;

        internal Enumerator(Dictionary<T, NoValue>.KeyCollection.Enumerator inner)
        {
            _inner = inner;
        }

        /// <summary>
        /// Текущий элемент.
        /// </summary>
        public T Current => _inner.Current;

        /// <inheritdoc/>
        object IEnumerator.Current => _inner.Current;

        /// <summary>
        /// Переходит к следующему элементу.
        /// </summary>
        /// <returns><c>false</c>, если элементы закончились</returns>
        /// <exception cref="InvalidOperationException">Множество изменено во время перечисления</exception>
        public bool MoveNext()
        {
            return _inner.MoveNext();
        }

        /// <summary>
        /// Возвращает перечислитель в начало.
        /// </summary>
        /// <exception cref="InvalidOperationException">Множество изменено во время перечисления</exception>
        public void Reset()
        {
            ResetInPlace(ref _inner);
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            _inner.Dispose();
        }

        /// <summary>
        /// Вызывает явную реализацию <see cref="IEnumerator.Reset"/> у самого поля: приведение к интерфейсу
        /// упаковало бы копию структуры, и сброс бы до поля не дошел.
        /// </summary>
        private static void ResetInPlace<TEnumerator>(ref TEnumerator enumerator) where TEnumerator : IEnumerator
        {
            enumerator.Reset();
        }
    }

    // ---------- внутренний API для SetNode ----------

    /// <summary>
    /// Словарь, на котором построено множество: элементы — его ключи. Множество владеет словарем
    /// и не отдает его наружу, поэтому ссылка на словарь однозначно задает множество.
    /// </summary>
    internal ReplicatedDictionary<T, NoValue> Dictionary => _dictionary;
}
