using System.Runtime.InteropServices;
using System.Text;
using RepliCAT.Bits;
using RepliCAT.Model;

namespace RepliCAT.Nodes;

/// <summary>
/// Узел <see cref="ReplicatedList{T}"/>. Полезная нагрузка:
/// <code>
/// [1 бит present]
/// [1 бит reset]
/// если !reset: удаления ([1][varuint slot])* [0]
/// вставки/изменения: ([1][varuint slot][полезная нагрузка элемента])* [0]
/// [1 бит hasOrder] (varuint count, count × varuint slot)
/// </code>
/// Элементы обрабатываются <b>одним</b> дочерним узлом на член (один кодек, одно предупреждение квантователя
/// на член). Если список не изменялся (та же ссылка и версия), а элементы — значения, запись завершается
/// за O(1) без обращения к элементам.<br/>
/// Узел входит на уровень вложенности (<see cref="ReplicationContext.EnterWrite"/>/<see cref="ReplicationContext.EnterRead"/>)
/// так же, как узел объекта.
/// </summary>
/// <typeparam name="TItem">Тип элементов</typeparam>
internal sealed class ListNode<TItem> : ReplicationNode<ReplicatedList<TItem>>
{
    private readonly ReplicationContext _context;
    private readonly ReplicationNode<TItem> _itemNode;
    private readonly bool _itemHasInnerState;
    private readonly string _path;

    /// <summary>
    /// Создает узел списка.
    /// </summary>
    /// <param name="context">Контекст репликатора</param>
    /// <param name="itemNode">Узел элементов (один на член)</param>
    /// <param name="path">Путь к члену для сообщений</param>
    public ListNode(ReplicationContext context, ReplicationNode<TItem> itemNode, string path)
    {
        _context = context;
        _itemNode = itemNode;
        _itemHasInnerState = itemNode.HasInnerState;
        _path = path;
    }

    /// <summary>
    /// Узел элементов.
    /// </summary>
    public ReplicationNode<TItem> ItemNode => _itemNode;

    /// <inheritdoc/>
    public override bool RequiresSetter => false;

    /// <inheritdoc/>
    public override bool HasInnerState => true;

    /// <inheritdoc/>
    public override Shadow CreateShadow()
    {
        return new ListShadow();
    }

    /// <inheritdoc/>
    public override void AppendSchema(StringBuilder sb, Dictionary<Type, int> visited)
    {
        sb.Append("list(");
        _itemNode.AppendSchema(sb, visited);
        sb.Append(')');
    }

    /// <inheritdoc/>
    public override bool WriteDelta(ReplicatedList<TItem> current, Shadow shadow, BitWriter writer, bool forceAll)
    {
        var listShadow = (ListShadow)shadow;

        if (current == null)
        {
            if (!forceAll && listShadow.Written && listShadow.Ref == null)
            {
                return false;
            }

            writer.WriteBool(false);
            listShadow.SetNull();
            return true;
        }

        bool reset = forceAll || !listShadow.Written || !ReferenceEquals(listShadow.Ref, current);
        bool structural = reset || listShadow.Version != current.Version;
        if (!structural && !_itemHasInnerState)
        {
            // Быстрый путь O(1): список не менялся, а значения элементов без изменения списка измениться не могут.
            return false;
        }

        int start = writer.BitPosition;
        bool written;
        _context.EnterWrite(_path);
        try
        {
            written = reset
                ? WriteReset(current, listShadow, writer)
                : WriteChanges(current, listShadow, writer, structural);
        }
        finally
        {
            _context.Exit();
        }

        if (!written)
        {
            writer.Rewind(start);
            return false;
        }

        return true;
    }

    /// <inheritdoc/>
    public override void WriteShadow(Shadow shadow, BitWriter writer)
    {
        var listShadow = (ListShadow)shadow;
        if (!listShadow.Written)
        {
            throw new InvalidOperationException("List shadow has never been written.");
        }

        if (listShadow.Ref == null)
        {
            writer.WriteBool(false);
            return;
        }

        _context.EnterWrite(_path);
        try
        {
            writer.WriteBool(true);
            writer.WriteBool(true);
            Shadow[] slots = listShadow.Slots;
            foreach (int slot in CollectionsMarshal.AsSpan(listShadow.Order))
            {
                writer.WriteBool(true);
                writer.WriteVarUInt((ulong)slot);
                _itemNode.WriteShadow(slots[slot], writer);
            }

            writer.WriteBool(false);
            writer.WriteBool(false);
        }
        finally
        {
            _context.Exit();
        }
    }

    /// <inheritdoc/>
    public override ReplicatedList<TItem> Read(ReplicatedList<TItem> existing, ref BitReader reader)
    {
        if (!reader.ReadBool())
        {
            return null;
        }

        ReplicatedList<TItem> list = existing ?? new ReplicatedList<TItem>();

        _context.EnterRead(_path);
        try
        {
            if (reader.ReadBool())
            {
                // Сброс: экземпляр сохраняется, записанные слоты переиспользуют прежние элементы,
                // остальные удаляются, порядок — последовательность записей.
                list.BeginReset();
                try
                {
                    ReadUpserts(list, ref reader);
                }
                finally
                {
                    list.EndReset();
                }
            }
            else
            {
                try
                {
                    while (reader.ReadBool())
                    {
                        // Отсутствующий слот игнорируется (идемпотентность после снимка).
                        list.MarkSlotRemoved(_context.ReadSlotId(ref reader));
                    }
                }
                finally
                {
                    list.CompactOrder();
                }

                ReadUpserts(list, ref reader);
            }

            if (reader.ReadBool())
            {
                ReadOrder(list, ref reader);
            }
        }
        finally
        {
            _context.Exit();
        }

        return list;
    }

    private bool WriteReset(ReplicatedList<TItem> current, ListShadow shadow, BitWriter writer)
    {
        ReadOnlySpan<int> order = current.Order;
        _context.CheckCollectionCount(order.Length, _path);

        writer.WriteBool(true);
        writer.WriteBool(true);

        // Тени слотов, которых больше нет (или которые заняты заново), выбрасываются. Тени остальных слотов
        // переиспользуются: запись с forceAll полностью перезаписывает их состояние.
        Shadow[] slots = shadow.Slots;
        foreach (int slot in CollectionsMarshal.AsSpan(shadow.Order))
        {
            if (!shadow.IsSameSlot(current, slot))
            {
                slots[slot] = null;
            }
        }

        foreach (int slot in order)
        {
            Shadow itemShadow = shadow.GetSlot(slot);
            if (itemShadow == null)
            {
                _context.CheckCollectionCount(slot + 1, _path);
                itemShadow = _itemNode.CreateShadow();
                shadow.SetSlot(slot, itemShadow, current.GetGeneration(slot));
            }

            writer.WriteBool(true);
            writer.WriteVarUInt((ulong)slot);
            _itemNode.WriteDelta(current.GetSlot(slot), itemShadow, writer, true);
        }

        writer.WriteBool(false);
        writer.WriteBool(false);
        shadow.Commit(current);
        return true;
    }

    private bool WriteChanges(ReplicatedList<TItem> current, ListShadow shadow, BitWriter writer, bool structural)
    {
        ReadOnlySpan<int> order = current.Order;
        bool any = false;
        bool hasOrder = false;

        writer.WriteBool(true);
        writer.WriteBool(false);

        if (structural)
        {
            _context.CheckCollectionCount(order.Length, _path);

            // Порядок проверяется до изменения тени.
            hasOrder = !IsExpectedOrder(current, shadow);

            // Удаления: слоты тени, которые освобождены или заняты заново (другое поколение).
            // Заново занятый слот затем записывается как новый и встает в конец порядка получателя.
            Shadow[] slots = shadow.Slots;
            foreach (int slot in CollectionsMarshal.AsSpan(shadow.Order))
            {
                if (!shadow.IsSameSlot(current, slot))
                {
                    writer.WriteBool(true);
                    writer.WriteVarUInt((ulong)slot);
                    slots[slot] = null;
                    any = true;
                }
            }
        }

        writer.WriteBool(false);

        foreach (int slot in order)
        {
            int entryStart = writer.BitPosition;
            writer.WriteBool(true);
            writer.WriteVarUInt((ulong)slot);

            Shadow itemShadow = shadow.GetSlot(slot);
            if (itemShadow == null)
            {
                // Новый слот: элемент целиком.
                _context.CheckCollectionCount(slot + 1, _path);
                itemShadow = _itemNode.CreateShadow();
                shadow.SetSlot(slot, itemShadow, current.GetGeneration(slot));
                _itemNode.WriteDelta(current.GetSlot(slot), itemShadow, writer, true);
                any = true;
            }
            else if (_itemNode.WriteDelta(current.GetSlot(slot), itemShadow, writer, false))
            {
                // Существующий слот: только изменения элемента. Сюда без структурных изменений
                // попадают только элементы с внутренним состоянием (быстрый путь отсекает значения).
                any = true;
            }
            else
            {
                writer.Rewind(entryStart);
            }
        }

        writer.WriteBool(false);
        writer.WriteBool(hasOrder);
        if (hasOrder)
        {
            writer.WriteVarUInt((ulong)order.Length);
            foreach (int slot in order)
            {
                writer.WriteVarUInt((ulong)slot);
            }

            any = true;
        }

        if (structural)
        {
            shadow.Commit(current);
        }

        return any;
    }

    /// <summary>
    /// Проверяет, совпадет ли порядок получателя с текущим без явной передачи порядка.
    /// Ожидаемый порядок: порядок тени без удаленных (и заново занятых) слотов, затем новые слоты
    /// в текущем порядке (так их расставит получатель).
    /// </summary>
    private static bool IsExpectedOrder(ReplicatedList<TItem> current, ListShadow shadow)
    {
        ReadOnlySpan<int> order = current.Order;
        int index = 0;
        foreach (int slot in CollectionsMarshal.AsSpan(shadow.Order))
        {
            if (!shadow.IsSameSlot(current, slot))
            {
                continue;
            }

            if (index >= order.Length || order[index] != slot)
            {
                return false;
            }

            index++;
        }

        for (; index < order.Length; index++)
        {
            if (shadow.IsSameSlot(current, order[index]))
            {
                return false;
            }
        }

        return true;
    }

    private void ReadUpserts(ReplicatedList<TItem> list, ref BitReader reader)
    {
        while (reader.ReadBool())
        {
            int slot = _context.ReadSlotId(ref reader);
            if (list.IsDuplicateInReset(slot))
            {
                throw new ReplicationFormatException($"duplicate list slot id {slot} in a reset.");
            }

            list.TryGetSlot(slot, out TItem existing);
            TItem item = _itemNode.Read(existing, ref reader);
            list.SetSlot(slot, item);
        }
    }

    private void ReadOrder(ReplicatedList<TItem> list, ref BitReader reader)
    {
        int count = _context.ReadCollectionCount(ref reader);
        if (count != list.Count)
        {
            throw new ReplicationFormatException(
                $"list order has {count} slots, but the list has {list.Count} items.");
        }

        List<int> order = list.BeginSetOrder();
        for (int i = 0; i < count; i++)
        {
            order.Add(_context.ReadSlotId(ref reader));
        }

        if (!list.TryCommitOrder())
        {
            throw new ReplicationFormatException("list order is not a permutation of the current slots.");
        }
    }

    /// <summary>
    /// Тень списка: последняя отправленная ссылка, ее версия, тени элементов по слотам
    /// (<c>null</c> — слота нет) и порядок слотов. Слот есть в <see cref="Order"/> тогда и только тогда,
    /// когда его тень не <c>null</c>.
    /// </summary>
    private sealed class ListShadow : Shadow
    {
        public ReplicatedList<TItem> Ref;
        public bool Written;
        public int Version;
        public Shadow[] Slots = [];
        public int[] Generations = [];
        public readonly List<int> Order = new();

        public Shadow GetSlot(int slot)
        {
            return (uint)slot < (uint)Slots.Length ? Slots[slot] : null;
        }

        /// <summary>
        /// <c>true</c>, если слот есть в тени и в списке занят тем же элементом (то же поколение слота).
        /// </summary>
        public bool IsSameSlot(ReplicatedList<TItem> list, int slot)
        {
            return GetSlot(slot) != null && list.IsSlotUsed(slot) && list.GetGeneration(slot) == Generations[slot];
        }

        public void SetSlot(int slot, Shadow itemShadow, int generation)
        {
            if (slot >= Slots.Length)
            {
                int size = Math.Max(slot + 1, Math.Max(4, Slots.Length * 2));
                Array.Resize(ref Slots, size);
                Array.Resize(ref Generations, size);
            }

            Slots[slot] = itemShadow;
            Generations[slot] = generation;
        }

        public void SetNull()
        {
            foreach (int slot in CollectionsMarshal.AsSpan(Order))
            {
                Slots[slot] = null;
            }

            Order.Clear();
            Ref = null;
            Version = 0;
            Written = true;
        }

        public void Commit(ReplicatedList<TItem> list)
        {
            Order.Clear();
            Order.AddRange(list.Order);
            Version = list.Version;
            Ref = list;
            Written = true;
        }
    }
}
