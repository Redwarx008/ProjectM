using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

public class LRUCollection<TKey, TValue> where TKey : notnull
{
    private readonly int _capacity;
    private readonly Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue>>> _map;
    private readonly LinkedList<KeyValuePair<TKey, TValue>> _list;

    public LRUCollection(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        _capacity = capacity;
        _map = new Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue>>>(capacity);
        _list = new LinkedList<KeyValuePair<TKey, TValue>>();
    }

    public int Count => _map.Count;
    public int Capacity => _capacity;

    public bool TryGetValue(TKey key, out TValue value)
    {
        if (_map.TryGetValue(key, out var node))
        {
            // 移动到最前
            _list.Remove(node);
            _list.AddFirst(node);

            value = node.Value.Value;
            return true;
        }

        value = default!;
        return false;
    }

    public void Add(TKey key, TValue value)
    {
        Debug.Assert(!_map.ContainsKey(key));

        var node = new LinkedListNode<KeyValuePair<TKey, TValue>>(new KeyValuePair<TKey, TValue>(key, value));
        _list.AddFirst(node);
        _map[key] = node;
    }

    public bool ContainsKey(TKey key) => _map.ContainsKey(key);

    public bool Remove(TKey key)
    {
        if (_map.TryGetValue(key, out var node))
        {
            _map.Remove(key);
            _list.Remove(node);
            return true;
        }
        return false;
    }

    public void Clear()
    {
        _map.Clear();
        _list.Clear();
    }

    /// <summary>
    /// 获取最久未使用的项（不移除，也不提升热度）
    /// </summary>
    public bool TryPeekOldest(out TKey key, out TValue value)
    {
        if (_list.Last != null)
        {
            key = _list.Last.Value.Key;
            value = _list.Last.Value.Value;
            return true;
        }
        key = default!;
        value = default!;
        return false;
    }

    public bool TryPopOldest(out TKey key, out TValue value)
    {
        if (_list.Last != null)
        {
            var node = _list.Last;
            key = node.Value.Key;
            value = node.Value.Value;

            _list.RemoveLast();
            _map.Remove(key);
            return true;
        }
        key = default!;
        value = default!;
        return false;
    }
}