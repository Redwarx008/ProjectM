using System;
using System.Collections.Generic;
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

    public void AddOrUpdate(TKey key, TValue value)
    {
        if (_map.TryGetValue(key, out var node))
        {
            var newPair = new KeyValuePair<TKey, TValue>(key, value);

            node.Value = newPair;

            _list.Remove(node);
            _list.AddFirst(node);
        }
        else
        {
            // 淘汰最旧
            if (_map.Count >= _capacity)
            {
                var last = _list.Last!;
                _map.Remove(last.Value.Key);
                _list.RemoveLast();
            }

            var newNode = new LinkedListNode<KeyValuePair<TKey, TValue>>(
                new KeyValuePair<TKey, TValue>(key, value));

            _list.AddFirst(newNode);
            _map[key] = newNode;
        }
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
}