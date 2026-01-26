using Godot;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace ProjectM;
internal class PageCache : IDisposable
{
    // 活跃页面缓存：Key=虚拟页ID, Value=物理图集槽位索引
    // Capacity 应设置为物理图集的最大页数 (MaxPageCount)
    private LRUCollection<VirtualPageID, int> _lruPages;

    // 加载中集合：只记录 ID，防止重复 I/O
    private HashSet<VirtualPageID> _loadingPages = [];

    private List<int> _freeSlots;

    private TextureArray[] _textureAarrays;

    public event Action<VirtualPageID, int> AddPage = null!;

    public event Action<VirtualPageID> RemovePage = null!;

    private readonly StreamingManager _streamer;

    public PageCache(StreamingManager loader, int pageCapacity, int pageSize, ReadOnlySpan<RenderingDevice.DataFormat> formats)
    {
        _streamer = loader;
        _lruPages = new LRUCollection<VirtualPageID, int>(pageCapacity);
        _freeSlots = new List<int>(pageCapacity);
        for (int i = 0; i < pageCapacity; ++i)
        {
            _freeSlots.Add(pageCapacity - 1 - i);
        }
        _textureAarrays = new TextureArray[formats.Length];
        for(int i = 0; i < formats.Length; ++i)
        {
            _textureAarrays[i] = new TextureArray(pageCapacity, pageSize, formats[i]);
        }
        loader.LoadComplete += OnLoadComplete;
    }

    public bool Touch(VirtualPageID page)
    {
        if (!_loadingPages.Contains(page))
        {
            return _lruPages.TryGetValue(page, out _);
        }
        return false;
    }

    public void Request(VirtualPageID page)
    {
        if(!_loadingPages.Contains(page))
        {
            _streamer.RequestPage(page);
            _loadingPages.Add(page);
        }
    }

    public void Remove(VirtualPageID page, out int evictedSlot)
    {
        if(_lruPages.Remove(page, out evictedSlot))
        {
            _freeSlots.Add(evictedSlot);
        }
        else
        {
            evictedSlot = -1;
        }
    }

    public void UpdateTextureArray()
    {
        foreach(var textureArray in _textureAarrays)
        {
            textureArray.Update();
        }
    }

    public GDTexture2DArray GetTexture(int textureIndex)
    {
        return _textureAarrays[textureIndex].Texture;
    }

    private void OnLoadComplete(Span<(VirtualPageID id, IMemoryOwner<byte>? data)> loadedData)
    {
        Debug.Assert(loadedData.Length == _textureAarrays.Length);

        _loadingPages.Remove(loadedData[0].id);
        int slot;
        if (_freeSlots.Count > 0)
        {
            slot = _freeSlots[_freeSlots.Count - 1];
            _freeSlots.RemoveAt(_freeSlots.Count - 1);
        }
        else
        {
            _lruPages.PopLast(out VirtualPageID evictedId, out slot);
            Debug.Assert(slot != 0);
            RemovePage.Invoke(loadedData[0].id);
        }

        for (int i = 0; i < loadedData.Length; ++i)
        {
            Debug.Assert(loadedData[i].data != null);
            _textureAarrays[i].AddUpdate(slot, loadedData[i].data!);
        }

        _lruPages.Add(loadedData[0].id, slot);
        AddPage.Invoke(loadedData[0].id, slot);
    }

    public void Dispose()
    {
        foreach(var textureArray in _textureAarrays)
        {
            textureArray.Dispose();
        }
    }
}