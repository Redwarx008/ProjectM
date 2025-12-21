using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace ProjectM;
internal class PageCache
{
    // 活跃页面缓存：Key=虚拟页ID, Value=物理图集槽位索引
    // Capacity 应设置为物理图集的最大页数 (MaxPageCount)
    private LRUCollection<VirtualPageID, int> _dynamicResidentPages;

    // 加载中集合：只记录 ID，防止重复 I/O
    private HashSet<VirtualPageID> _loadingPages = [];

    private int _persistentMip;

    private int _persistentMipWidth;

    public PageCache(PageTable pageTable, int mipCount, int dynamicResidentCapacity)
    {
        _persistentMipWidth = pageTable.PersistentMipWidth;
        _persistentMip = mipCount - 1;
        _dynamicResidentPages = new LRUCollection<VirtualPageID, int>(dynamicResidentCapacity);
    }

    public bool Touch(VirtualPageID page)
    {
        if (!_loadingPages.Contains(page))
        {
            return _dynamicResidentPages.TryGetValue(page, out _);
        }
        return false;
    }

    public bool TryMarkLoading(VirtualPageID id)
    {
        // 如果已在内存或正在加载，则不处理
        if (_dynamicResidentPages.ContainsKey(id) || _loadingPages.Contains(id))
            return false;

        _loadingPages.Add(id);
        return true;
    }

    public void MarkLoadComplete(VirtualPageID id)
    {
        _loadingPages.Remove(id);
    }

    public bool TryGetPhysicalSlot(VirtualPageID id, out int slot)
    {
        if (id.mip == _persistentMip)
        {
            slot = id.x + _persistentMipWidth * id.y;
            return true;
        }
        else if (_dynamicResidentPages.TryGetValue(id, out int residentSlot))
        {
            slot = residentSlot;
            return true;
        }
        slot = -1;
        return false;
    }

    public void Add(VirtualPageID id, int slot)
    {
        _dynamicResidentPages.Add(id, slot);
    }

    public bool EvictOne(out VirtualPageID evictedId, out int evictedSlot)
    {
        return _dynamicResidentPages.TryPopOldest(out evictedId, out evictedSlot);
    }
}