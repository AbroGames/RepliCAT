using System.Text;
using RepliCAT.Bits;
using RepliCAT.Codecs;
using RepliCAT.Model;

namespace RepliCAT.Nodes;

/// <summary>
/// Узел <see cref="ReplicatedDictionary{TKey, TValue}"/>. Полезная нагрузка:
/// <code>
/// [1 бит present]
/// [1 бит reset]
/// если !reset: удаления ([1][ключ])* [0]
/// вставки/изменения: ([1][ключ][полезная нагрузка значения])* [0]
/// </code>
/// Ключи пишутся кодеком ключа (без квантования и допуска), значения — <b>одним</b> дочерним узлом на член.
/// Если словарь не изменялся (та же ссылка и версия), а значения — значения с кодеком, запись завершается
/// за O(1) без обращения к записям.<br/>
/// Узел входит на уровень вложенности так же, как узлы объекта и списка.<br/>
/// Политика повторов на чтении совпадает со списком: повтор ключа вне сброса допускается (применяется
/// как дельта к тому же значению), повтор при сбросе — ошибка формата. Ключ <c>null</c> — ошибка формата.<br/>
/// Узел также обслуживает <see cref="ReplicatedSet{T}"/> (через <see cref="SetNode{T}"/>).
/// </summary>
/// <typeparam name="TKey">Тип ключей</typeparam>
/// <typeparam name="TValue">Тип значений</typeparam>
internal sealed class DictionaryNode<TKey, TValue> : ReplicationNode<ReplicatedDictionary<TKey, TValue>>
{
    /// <summary>
    /// <c>true</c>, если кодек ключа может вернуть <c>null</c> (ссылочный тип или <see cref="Nullable{T}"/>).
    /// Проверка через <see cref="EqualityComparer{T}"/> вместо <c>key == null</c>: сравнение с <c>null</c>
    /// в разделяемом обобщенном коде может упаковывать ключ-значение.
    /// </summary>
    private static readonly bool KeyCanBeNull =
        !typeof(TKey).IsValueType || Nullable.GetUnderlyingType(typeof(TKey)) != null;

    private readonly ReplicationContext _context;
    private readonly IReplicationCodec<TKey> _keyCodec;
    private readonly ReplicationNode _keyNode;
    private readonly ReplicationNode<TValue> _valueNode;
    private readonly bool _valueHasInnerState;
    private readonly string _path;
    private readonly string _kind;

    /// <summary>
    /// Создает узел словаря.
    /// </summary>
    /// <param name="context">Контекст репликатора</param>
    /// <param name="keyNode">Узел ключей (используются его кодек и описание для хэша схемы)</param>
    /// <param name="valueNode">Узел значений (один на член)</param>
    /// <param name="path">Путь к члену для сообщений</param>
    /// <param name="kind">Название коллекции для сообщений об ошибках формата</param>
    public DictionaryNode(ReplicationContext context, ValueNode<TKey> keyNode, ReplicationNode<TValue> valueNode, string path,
        string kind = "dictionary")
    {
        _context = context;
        _keyCodec = keyNode.Codec;
        _keyNode = keyNode;
        _valueNode = valueNode;
        _valueHasInnerState = valueNode.HasInnerState;
        _path = path;
        _kind = kind;
    }

    /// <summary>
    /// Узел значений.
    /// </summary>
    public ReplicationNode<TValue> ItemNode => _valueNode;

    /// <inheritdoc/>
    public override bool RequiresSetter => false;

    /// <inheritdoc/>
    public override bool HasInnerState => true;

    /// <inheritdoc/>
    public override Shadow CreateShadow()
    {
        return new DictionaryShadow();
    }

    /// <inheritdoc/>
    public override void AppendSchema(StringBuilder sb, Dictionary<Type, int> visited)
    {
        sb.Append("dict(");
        _keyNode.AppendSchema(sb, visited);
        sb.Append(',');
        _valueNode.AppendSchema(sb, visited);
        sb.Append(')');
    }

    /// <inheritdoc/>
    public override bool WriteDelta(ReplicatedDictionary<TKey, TValue> current, Shadow shadow, BitWriter writer, bool forceAll)
    {
        var dictionaryShadow = (DictionaryShadow)shadow;

        if (current == null)
        {
            if (!forceAll && dictionaryShadow.Written && dictionaryShadow.Ref == null)
            {
                return false;
            }

            writer.WriteBool(false);
            dictionaryShadow.SetNull();
            return true;
        }

        bool reset = forceAll || !dictionaryShadow.Written || !ReferenceEquals(dictionaryShadow.Ref, current);
        bool structural = reset || dictionaryShadow.Version != current.Version;
        if (!structural && !_valueHasInnerState)
        {
            // Быстрый путь O(1): словарь не менялся, а значения без изменения словаря измениться не могут.
            return false;
        }

        int start = writer.BitPosition;
        bool written;
        _context.EnterWrite(_path);
        try
        {
            written = reset
                ? WriteReset(current, dictionaryShadow, writer)
                : WriteChanges(current, dictionaryShadow, writer, structural);
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
        var dictionaryShadow = (DictionaryShadow)shadow;
        if (!dictionaryShadow.Written)
        {
            throw new InvalidOperationException("Dictionary shadow has never been written.");
        }

        if (dictionaryShadow.Ref == null)
        {
            writer.WriteBool(false);
            return;
        }

        _context.EnterWrite(_path);
        try
        {
            writer.WriteBool(true);
            writer.WriteBool(true);
            foreach (KeyValuePair<TKey, Shadow> entry in dictionaryShadow.Entries)
            {
                writer.WriteBool(true);
                _keyCodec.Write(writer, entry.Key);
                _valueNode.WriteShadow(entry.Value, writer);
            }

            writer.WriteBool(false);
        }
        finally
        {
            _context.Exit();
        }
    }

    /// <inheritdoc/>
    public override ReplicatedDictionary<TKey, TValue> Read(ReplicatedDictionary<TKey, TValue> existing, ref BitReader reader)
    {
        if (!reader.ReadBool())
        {
            return null;
        }

        ReplicatedDictionary<TKey, TValue> dictionary = existing ?? new ReplicatedDictionary<TKey, TValue>();

        _context.EnterRead(_path);
        try
        {
            if (reader.ReadBool())
            {
                // Сброс: экземпляр сохраняется, записанные ключи переиспользуют прежние значения,
                // остальные удаляются.
                dictionary.BeginReset();
                try
                {
                    ReadUpserts(dictionary, ref reader, true);
                }
                finally
                {
                    dictionary.EndReset();
                }
            }
            else
            {
                while (reader.ReadBool())
                {
                    // Отсутствующий ключ игнорируется (идемпотентность после снимка).
                    dictionary.RemoveEntry(ReadKey(ref reader));
                }

                ReadUpserts(dictionary, ref reader, false);
            }
        }
        finally
        {
            _context.Exit();
        }

        return dictionary;
    }

    private bool WriteReset(ReplicatedDictionary<TKey, TValue> current, DictionaryShadow shadow, BitWriter writer)
    {
        Dictionary<TKey, TValue> entries = current.Inner;
        _context.CheckCollectionCount(entries.Count, _path);

        writer.WriteBool(true);
        writer.WriteBool(true);

        // Тени ключей, которых больше нет, выбрасываются. Тени остальных ключей переиспользуются:
        // запись с forceAll полностью перезаписывает их состояние. Удаление во время перечисления
        // Dictionary допустимо (.NET Core 3.0+).
        Dictionary<TKey, Shadow> shadows = shadow.Entries;
        foreach (KeyValuePair<TKey, Shadow> entry in shadows)
        {
            if (!entries.ContainsKey(entry.Key))
            {
                shadows.Remove(entry.Key);
            }
        }

        foreach (KeyValuePair<TKey, TValue> entry in entries)
        {
            if (!shadows.TryGetValue(entry.Key, out Shadow valueShadow))
            {
                valueShadow = _valueNode.CreateShadow();
                shadows.Add(entry.Key, valueShadow);
            }

            writer.WriteBool(true);
            _keyCodec.Write(writer, entry.Key);
            _valueNode.WriteDelta(entry.Value, valueShadow, writer, true);
        }

        writer.WriteBool(false);
        shadow.Commit(current);
        return true;
    }

    private bool WriteChanges(ReplicatedDictionary<TKey, TValue> current, DictionaryShadow shadow, BitWriter writer,
        bool structural)
    {
        Dictionary<TKey, TValue> entries = current.Inner;
        Dictionary<TKey, Shadow> shadows = shadow.Entries;
        bool any = false;

        writer.WriteBool(true);
        writer.WriteBool(false);

        if (structural)
        {
            _context.CheckCollectionCount(entries.Count, _path);

            // Удаления: ключи тени, которых больше нет в словаре.
            foreach (KeyValuePair<TKey, Shadow> entry in shadows)
            {
                if (!entries.ContainsKey(entry.Key))
                {
                    writer.WriteBool(true);
                    _keyCodec.Write(writer, entry.Key);
                    shadows.Remove(entry.Key);
                    any = true;
                }
            }
        }

        writer.WriteBool(false);

        foreach (KeyValuePair<TKey, TValue> entry in entries)
        {
            int entryStart = writer.BitPosition;
            writer.WriteBool(true);
            _keyCodec.Write(writer, entry.Key);

            if (!shadows.TryGetValue(entry.Key, out Shadow valueShadow))
            {
                // Новый ключ: значение целиком.
                valueShadow = _valueNode.CreateShadow();
                shadows.Add(entry.Key, valueShadow);
                _valueNode.WriteDelta(entry.Value, valueShadow, writer, true);
                any = true;
            }
            else if (_valueNode.WriteDelta(entry.Value, valueShadow, writer, false))
            {
                // Существующий ключ: только изменения значения. Без структурных изменений сюда попадают
                // только значения с внутренним состоянием (быстрый путь отсекает значения с кодеком).
                any = true;
            }
            else
            {
                writer.Rewind(entryStart);
            }
        }

        writer.WriteBool(false);

        if (structural)
        {
            shadow.Commit(current);
        }

        return any;
    }

    /// <summary>
    /// Читает ключ кодеком ключа. Ключ <c>null</c> (например, у строкового кодека) — ошибка формата,
    /// потому что словарь (и множество) не может его содержать.
    /// </summary>
    private TKey ReadKey(ref BitReader reader)
    {
        TKey key = _keyCodec.Read(ref reader);
        if (KeyCanBeNull && EqualityComparer<TKey>.Default.Equals(key, default))
        {
            throw new ReplicationFormatException($"a null {_kind} key was received.");
        }

        return key;
    }

    private void ReadUpserts(ReplicatedDictionary<TKey, TValue> dictionary, ref BitReader reader, bool reset)
    {
        int max = _context.Limits.MaxCollectionCount;
        int upserts = 0;
        while (reader.ReadBool())
        {
            TKey key = ReadKey(ref reader);
            if (reset)
            {
                // При сбросе каждый ключ записывается один раз, поэтому число записей ограничено лимитом.
                if (dictionary.IsDuplicateInReset(key))
                {
                    throw new ReplicationFormatException($"duplicate {_kind} key {key} in a reset.");
                }

                if (upserts >= max)
                {
                    throw new ReplicationFormatException(
                        $"{_kind} reset exceeds ReplicationLimits.MaxCollectionCount ({max}).");
                }

                upserts++;
            }
            else if (dictionary.Count >= max && !dictionary.ContainsKey(key))
            {
                throw new ReplicationFormatException(
                    $"{_kind} exceeds ReplicationLimits.MaxCollectionCount ({max}).");
            }

            dictionary.TryGetValue(key, out TValue existing);
            TValue value = _valueNode.Read(existing, ref reader);
            dictionary.SetEntry(key, value);
        }
    }

    /// <summary>
    /// Тень словаря: последняя отправленная ссылка, ее версия и тени значений по ключам.
    /// </summary>
    private sealed class DictionaryShadow : Shadow
    {
        public ReplicatedDictionary<TKey, TValue> Ref;
        public bool Written;
        public int Version;
        public readonly Dictionary<TKey, Shadow> Entries = new();

        public void SetNull()
        {
            Entries.Clear();
            Ref = null;
            Version = 0;
            Written = true;
        }

        public void Commit(ReplicatedDictionary<TKey, TValue> dictionary)
        {
            Version = dictionary.Version;
            Ref = dictionary;
            Written = true;
        }
    }
}
