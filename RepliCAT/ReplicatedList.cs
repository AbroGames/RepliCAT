using System.Collections;
using System.Runtime.InteropServices;

namespace RepliCAT;

/// <summary>
/// Список, который можно реплицировать как член объекта с <see cref="ReplicatedAttribute"/>.
/// Ведет себя как <see cref="List{T}"/>, но внутри хранит элементы по <b>слотам</b>: каждый элемент занимает
/// слот с небольшим целым идентификатором, а порядок элементов — это последовательность слотов.
/// Репликация синхронизирует состояние "слот → элемент" и порядок, поэтому добавление, удаление по индексу
/// и присваивание по индексу передают только затронутые элементы, а вставка и сортировка — еще и порядок.<br/>
/// Освобожденные слоты переиспользуются, поэтому идентификаторы остаются маленькими.<br/>
/// Для элементов-объектов с реплицируемыми членами передаются только изменившиеся члены элементов.<br/>
/// На получателе список нельзя изменять локально: идентификаторы слотов назначает отправитель.
/// </summary>
/// <typeparam name="T">Тип элементов: тип с кодеком значения, класс с реплицируемыми членами или другой реплицируемый список</typeparam>
public sealed class ReplicatedList<T> : IList<T>, IReadOnlyList<T>
{
    private const int DefaultCapacity = 4;

    private T[] _items;
    private bool[] _used;
    private int[] _generations;
    private List<int> _order;
    private readonly Stack<int> _free = new();
    private int _nextSlot;
    private int _version;

    // Служебные буферы получателя (сброс и проверка порядка), создаются лениво.
    private bool[] _marks;
    private List<int> _orderScratch;
    private bool _resetting;

    /// <summary>
    /// Создает пустой список.
    /// </summary>
    public ReplicatedList() : this(0)
    {
    }

    /// <summary>
    /// Создает пустой список с указанной начальной емкостью.
    /// </summary>
    /// <param name="capacity">Начальная емкость</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> отрицательна</exception>
    public ReplicatedList(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);

        _items = capacity == 0 ? [] : new T[capacity];
        _used = capacity == 0 ? [] : new bool[capacity];
        _generations = capacity == 0 ? [] : new int[capacity];
        _order = new List<int>(capacity);
    }

    /// <summary>
    /// Создает список, содержащий элементы коллекции.
    /// </summary>
    /// <param name="collection">Исходные элементы</param>
    /// <exception cref="ArgumentNullException"><paramref name="collection"/> равна <c>null</c></exception>
    public ReplicatedList(IEnumerable<T> collection) : this(0)
    {
        AddRange(collection);
    }

    /// <summary>
    /// Количество элементов.
    /// </summary>
    public int Count => _order.Count;

    /// <inheritdoc/>
    bool ICollection<T>.IsReadOnly => false;

    /// <summary>
    /// Элемент по индексу.
    /// </summary>
    /// <param name="index">Индекс элемента</param>
    /// <exception cref="ArgumentOutOfRangeException">Индекс вне диапазона</exception>
    public T this[int index]
    {
        get
        {
            CheckIndex(index);
            return _items[_order[index]];
        }
        set
        {
            CheckIndex(index);
            _items[_order[index]] = value;
            _version++;
        }
    }

    /// <summary>
    /// Добавляет элемент в конец списка.
    /// </summary>
    /// <param name="item">Элемент</param>
    public void Add(T item)
    {
        int slot = AllocateSlot();
        _items[slot] = item;
        _order.Add(slot);
        _version++;
    }

    /// <summary>
    /// Добавляет элементы коллекции в конец списка.
    /// </summary>
    /// <param name="collection">Элементы</param>
    /// <exception cref="ArgumentNullException"><paramref name="collection"/> равна <c>null</c></exception>
    public void AddRange(IEnumerable<T> collection)
    {
        ArgumentNullException.ThrowIfNull(collection);

        if (ReferenceEquals(collection, this))
        {
            // Как у List<T>: добавление самого себя удваивает содержимое.
            int count = Count;
            for (int i = 0; i < count; i++)
            {
                Add(_items[_order[i]]);
            }

            return;
        }

        foreach (T item in collection)
        {
            Add(item);
        }
    }

    /// <summary>
    /// Вставляет элемент по индексу.
    /// </summary>
    /// <param name="index">Индекс, от 0 до <see cref="Count"/> включительно</param>
    /// <param name="item">Элемент</param>
    /// <exception cref="ArgumentOutOfRangeException">Индекс вне диапазона</exception>
    public void Insert(int index, T item)
    {
        if ((uint)index > (uint)_order.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "Index must be within the bounds of the list.");
        }

        int slot = AllocateSlot();
        _items[slot] = item;
        _order.Insert(index, slot);
        _version++;
    }

    /// <summary>
    /// Удаляет элемент по индексу.
    /// </summary>
    /// <param name="index">Индекс элемента</param>
    /// <exception cref="ArgumentOutOfRangeException">Индекс вне диапазона</exception>
    public void RemoveAt(int index)
    {
        CheckIndex(index);

        int slot = _order[index];
        _order.RemoveAt(index);
        ReleaseSlot(slot);
        _free.Push(slot);
        _version++;
    }

    /// <summary>
    /// Удаляет первое вхождение элемента.
    /// </summary>
    /// <param name="item">Элемент</param>
    /// <returns><c>true</c>, если элемент найден и удален</returns>
    public bool Remove(T item)
    {
        int index = IndexOf(item);
        if (index < 0)
        {
            return false;
        }

        RemoveAt(index);
        return true;
    }

    /// <summary>
    /// Удаляет все элементы.
    /// </summary>
    public void Clear()
    {
        foreach (int slot in _order)
        {
            ReleaseSlot(slot);
        }

        _order.Clear();
        _free.Clear();
        _nextSlot = 0;
        _version++;
    }

    /// <summary>
    /// Возвращает индекс первого вхождения элемента (сравнение через <see cref="EqualityComparer{T}.Default"/>).
    /// </summary>
    /// <param name="item">Элемент</param>
    /// <returns>Индекс или -1</returns>
    public int IndexOf(T item)
    {
        EqualityComparer<T> comparer = EqualityComparer<T>.Default;
        ReadOnlySpan<int> order = CollectionsMarshal.AsSpan(_order);
        for (int i = 0; i < order.Length; i++)
        {
            if (comparer.Equals(_items[order[i]], item))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Проверяет, содержит ли список элемент.
    /// </summary>
    /// <param name="item">Элемент</param>
    public bool Contains(T item)
    {
        return IndexOf(item) >= 0;
    }

    /// <summary>
    /// Копирует элементы в массив.
    /// </summary>
    /// <param name="array">Массив назначения</param>
    /// <param name="arrayIndex">Индекс в массиве, с которого начинается копирование</param>
    /// <exception cref="ArgumentNullException"><paramref name="array"/> равен <c>null</c></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="arrayIndex"/> отрицателен</exception>
    /// <exception cref="ArgumentException">Элементы не помещаются в массив</exception>
    public void CopyTo(T[] array, int arrayIndex)
    {
        ArgumentNullException.ThrowIfNull(array);
        ArgumentOutOfRangeException.ThrowIfNegative(arrayIndex);
        if (array.Length - arrayIndex < _order.Count)
        {
            throw new ArgumentException("Destination array is not long enough.", nameof(array));
        }

        ReadOnlySpan<int> order = CollectionsMarshal.AsSpan(_order);
        for (int i = 0; i < order.Length; i++)
        {
            array[arrayIndex + i] = _items[order[i]];
        }
    }

    /// <summary>
    /// Сортирует элементы (нестабильная сортировка, как <see cref="List{T}.Sort(Comparison{T})"/>).
    /// Элементы остаются в своих слотах, при репликации передается только новый порядок.
    /// </summary>
    /// <param name="comparison">Функция сравнения</param>
    /// <exception cref="ArgumentNullException"><paramref name="comparison"/> равна <c>null</c></exception>
    public void Sort(Comparison<T> comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);

        Sort(Comparer<T>.Create(comparison));
    }

    /// <summary>
    /// Сортирует элементы (нестабильная сортировка, как <see cref="List{T}.Sort(IComparer{T})"/>).
    /// Элементы остаются в своих слотах, при репликации передается только новый порядок.
    /// </summary>
    /// <param name="comparer">Компаратор или <c>null</c> для <see cref="Comparer{T}.Default"/></param>
    public void Sort(IComparer<T> comparer = null)
    {
        int count = _order.Count;
        if (count > 1)
        {
            var keys = new T[count];
            int[] slots = _order.ToArray();
            for (int i = 0; i < count; i++)
            {
                keys[i] = _items[slots[i]];
            }

            Array.Sort(keys, slots, comparer);
            _order.Clear();
            _order.AddRange(slots);
        }

        _version++;
    }

    /// <summary>
    /// Возвращает перечислитель (структуру). Изменение списка во время перечисления приводит
    /// к <see cref="InvalidOperationException"/>.
    /// </summary>
    public Enumerator GetEnumerator()
    {
        return new Enumerator(this);
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

    /// <summary>
    /// Перечислитель <see cref="ReplicatedList{T}"/> с проверкой версии.
    /// </summary>
    public struct Enumerator : IEnumerator<T>
    {
        private readonly ReplicatedList<T> _list;
        private readonly int _version;
        private int _index;
        private T _current;

        internal Enumerator(ReplicatedList<T> list)
        {
            _list = list;
            _version = list._version;
            _index = 0;
            _current = default;
        }

        /// <summary>
        /// Текущий элемент.
        /// </summary>
        public readonly T Current => _current;

        /// <inheritdoc/>
        readonly object IEnumerator.Current => _current;

        /// <summary>
        /// Переходит к следующему элементу.
        /// </summary>
        /// <returns><c>false</c>, если элементы закончились</returns>
        /// <exception cref="InvalidOperationException">Список изменен во время перечисления</exception>
        public bool MoveNext()
        {
            if (_version != _list._version)
            {
                throw new InvalidOperationException("Collection was modified; enumeration operation may not execute.");
            }

            if (_index < _list._order.Count)
            {
                _current = _list._items[_list._order[_index]];
                _index++;
                return true;
            }

            _index = _list._order.Count + 1;
            _current = default;
            return false;
        }

        /// <summary>
        /// Возвращает перечислитель в начало.
        /// </summary>
        /// <exception cref="InvalidOperationException">Список изменен во время перечисления</exception>
        public void Reset()
        {
            if (_version != _list._version)
            {
                throw new InvalidOperationException("Collection was modified; enumeration operation may not execute.");
            }

            _index = 0;
            _current = default;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
        }
    }

    // ---------- внутренний API для ListNode ----------

    /// <summary>
    /// Версия списка: увеличивается при каждом изменении.
    /// </summary>
    internal int Version => _version;

    /// <summary>
    /// Порядок элементов: последовательность занятых слотов. Изменять нельзя.
    /// </summary>
    internal ReadOnlySpan<int> Order => CollectionsMarshal.AsSpan(_order);

    /// <summary>
    /// Проверяет, занят ли слот.
    /// </summary>
    internal bool IsSlotUsed(int slot)
    {
        return (uint)slot < (uint)_used.Length && _used[slot];
    }

    /// <summary>
    /// Поколение слота: увеличивается каждый раз, когда слот занимается заново. Позволяет узлу отличить
    /// переиспользованный за кадр слот (удаление + новый элемент) от того же элемента. На провод не попадает.
    /// </summary>
    internal int GetGeneration(int slot)
    {
        return _generations[slot];
    }

    /// <summary>
    /// Возвращает элемент занятого слота.
    /// </summary>
    internal T GetSlot(int slot)
    {
        return _items[slot];
    }

    /// <summary>
    /// Возвращает элемент слота, если слот занят.
    /// </summary>
    internal bool TryGetSlot(int slot, out T item)
    {
        if (IsSlotUsed(slot))
        {
            item = _items[slot];
            return true;
        }

        item = default;
        return false;
    }

    /// <summary>
    /// Получатель: <c>true</c>, если идет сброс и слот уже был записан во время этого сброса.
    /// </summary>
    internal bool IsDuplicateInReset(int slot)
    {
        return _resetting && IsSlotUsed(slot) && (slot >= _marks.Length || !_marks[slot]);
    }

    /// <summary>
    /// Получатель: записывает элемент в слот. Новый слот добавляется в конец порядка.
    /// Во время сброса (<see cref="BeginReset"/>) каждый слот можно записать только один раз.
    /// </summary>
    /// <returns><c>false</c>, если во время сброса слот уже был записан (ошибка формата)</returns>
    internal bool SetSlot(int slot, T item)
    {
        if (IsSlotUsed(slot))
        {
            if (_resetting)
            {
                // Слот, не помеченный при BeginReset, уже был записан во время этого сброса.
                if (slot >= _marks.Length || !_marks[slot])
                {
                    return false;
                }

                // Слот из прежнего содержимого переиспользуется: он встает в новый порядок.
                _marks[slot] = false;
                _order.Add(slot);
            }

            _items[slot] = item;
            _version++;
            return true;
        }

        EnsureSlotCapacity(slot + 1);
        _used[slot] = true;
        _generations[slot]++;
        _items[slot] = item;
        _order.Add(slot);
        _version++;
        return true;
    }

    /// <summary>
    /// Получатель: освобождает слот, не трогая порядок. После серии удалений нужно вызвать <see cref="CompactOrder"/>.
    /// Отсутствующий слот игнорируется.
    /// </summary>
    internal void MarkSlotRemoved(int slot)
    {
        if (!IsSlotUsed(slot))
        {
            return;
        }

        // В стек свободных слотов не кладем: получатель слоты не выделяет,
        // и стек рос бы бесконечно.
        ReleaseSlot(slot);
        _version++;
    }

    /// <summary>
    /// Получатель: убирает из порядка освобожденные слоты.
    /// </summary>
    internal void CompactOrder()
    {
        Span<int> order = CollectionsMarshal.AsSpan(_order);
        int kept = 0;
        for (int i = 0; i < order.Length; i++)
        {
            int slot = order[i];
            if (_used[slot])
            {
                order[kept++] = slot;
            }
        }

        if (kept < order.Length)
        {
            _order.RemoveRange(kept, order.Length - kept);
        }
    }

    /// <summary>
    /// Получатель: начинает сброс. Порядок очищается, прежние элементы остаются в своих слотах до
    /// <see cref="EndReset"/>: записанные за время сброса слоты переиспользуют их, остальные удаляются.
    /// </summary>
    internal void BeginReset()
    {
        EnsureMarks();
        foreach (int slot in _order)
        {
            _marks[slot] = true;
        }

        _order.Clear();
        _resetting = true;
        _version++;
    }

    /// <summary>
    /// Получатель: завершает сброс и удаляет слоты, которые не были записаны.
    /// </summary>
    internal void EndReset()
    {
        _resetting = false;
        bool[] marks = _marks;
        for (int slot = 0; slot < marks.Length; slot++)
        {
            if (marks[slot])
            {
                marks[slot] = false;
                ReleaseSlot(slot);
            }
        }

        _version++;
    }

    /// <summary>
    /// Получатель: возвращает очищенный буфер для нового порядка. После заполнения нужно вызвать <see cref="TryCommitOrder"/>.
    /// </summary>
    internal List<int> BeginSetOrder()
    {
        _orderScratch ??= new List<int>();
        _orderScratch.Clear();
        return _orderScratch;
    }

    /// <summary>
    /// Получатель: применяет порядок из буфера <see cref="BeginSetOrder"/>, если это перестановка текущих слотов.
    /// </summary>
    /// <returns><c>false</c>, если порядок не является перестановкой (список не изменяется)</returns>
    internal bool TryCommitOrder()
    {
        List<int> scratch = _orderScratch;
        if (scratch.Count != _order.Count)
        {
            return false;
        }

        EnsureMarks();
        bool valid = true;
        int marked = 0;
        for (; marked < scratch.Count; marked++)
        {
            int slot = scratch[marked];
            if (!IsSlotUsed(slot) || _marks[slot])
            {
                valid = false;
                break;
            }

            _marks[slot] = true;
        }

        for (int i = 0; i < marked; i++)
        {
            _marks[scratch[i]] = false;
        }

        if (!valid)
        {
            return false;
        }

        (_order, _orderScratch) = (scratch, _order);
        _version++;
        return true;
    }

    // ---------- слоты ----------

    private int AllocateSlot()
    {
        // Слоты выделяются лениво: получатель может занять произвольные слоты, поэтому занятые пропускаются.
        while (_free.Count > 0)
        {
            int slot = _free.Pop();
            if (!_used[slot])
            {
                _used[slot] = true;
                _generations[slot]++;
                return slot;
            }
        }

        while (_nextSlot < _used.Length && _used[_nextSlot])
        {
            _nextSlot++;
        }

        int next = _nextSlot++;
        EnsureSlotCapacity(next + 1);
        _used[next] = true;
        _generations[next]++;
        return next;
    }

    private void ReleaseSlot(int slot)
    {
        _used[slot] = false;
        _items[slot] = default;
    }

    private void EnsureSlotCapacity(int required)
    {
        if (required <= _items.Length)
        {
            return;
        }

        int capacity = Math.Max(required, Math.Max(DefaultCapacity, _items.Length * 2));
        Array.Resize(ref _items, capacity);
        Array.Resize(ref _used, capacity);
        Array.Resize(ref _generations, capacity);
    }

    private void EnsureMarks()
    {
        if (_marks == null || _marks.Length < _used.Length)
        {
            Array.Resize(ref _marks, _used.Length);
        }
    }

    private void CheckIndex(int index)
    {
        if ((uint)index >= (uint)_order.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "Index was out of range.");
        }
    }
}
