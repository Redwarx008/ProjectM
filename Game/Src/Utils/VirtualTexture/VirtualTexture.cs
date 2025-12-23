using Core;
using Godot;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.ConstrainedExecution;
using static Godot.HttpRequest;
using Logger = Core.Logger;

namespace ProjectM;

public struct VirtualPageID : System.IEquatable<VirtualPageID>, IComparable<VirtualPageID>
{
    public int mip;
    public int x;
    public int y;
    public int CompareTo(VirtualPageID other) => other.mip.CompareTo(mip);
    public bool Equals(VirtualPageID other) => mip == other.mip && x == other.x && y == other.y;
    public override bool Equals(object? obj) => obj is VirtualPageID v && Equals(v);
    public override int GetHashCode() => HashCode.Combine(mip, x, y);
    public static bool operator ==(VirtualPageID a, VirtualPageID b) => a.Equals(b);
    public static bool operator !=(VirtualPageID a, VirtualPageID b) => !a.Equals(b);
}

public struct VirtualTextureDesc
{
    public RenderingDevice.DataFormat format;

    public string filePath;
}

public class VirtualTexture : IDisposable
{
    public enum PageSize
    {
        x128 = 128,
        x256 = 256,
        x512 = 512,
        x1024 = 1024,
    }
    public int MipCount { get; init; } 
    public int TileSize { get; init; }
    public int MaxPageCount { get; init; }
    public int Padding { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public bool Inited { get; private set; }

    public static readonly int MaxPageTableMipInGpu = 8;

    private PageTable _pageTable;
    private PageCache _pageCache;
    private PhysicalTexture _physicalTexture;
    private StreamingManager _streamer;

    private int _loadedPersistentCount = 0;

    private bool _persistentReady = false;

    private readonly List<VirtualPageID> _pendingRequests = [];

    public VirtualTexture(uint maxPageCount, VirtualTextureDesc[] vtDescs)
    {
        Debug.Assert(maxPageCount <= 2048);
        _streamer = new StreamingManager();
        for (int i = 0; i < vtDescs.Length; ++i)
        {
            _streamer.RegisterFile(vtDescs[i].filePath, (int)maxPageCount);
        }
        var vTInfo = _streamer.GetVTInfo();
        MaxPageCount = (int)maxPageCount;
        MipCount = vTInfo.mipmaps;
        Width = vTInfo.width;
        Height = vTInfo.height;
        TileSize = vTInfo.tileSize;
        Padding = vTInfo.padding;
        _pageTable = new PageTable(Width, Height, TileSize, (int)maxPageCount, MipCount);
        _pageCache = new PageCache(_pageTable, MipCount, (int)(maxPageCount - _pageTable.DynamicPageOffset));
        _physicalTexture = new PhysicalTexture(maxPageCount, _pageTable.DynamicPageOffset, vTInfo, vtDescs);
        LoadResidentPages();
    }

    public void Update()
    {
        FlushPageRequests();
        ProcessLoadedPages();
        _pageTable.Update();
        _physicalTexture.Update();
    }

    public void RequestPage(VirtualPageID id)
    {
        VirtualPageID cur = id;
        while(cur.mip < MipCount - 1)
        {
            if (_pageCache.TryMarkLoading(cur))
            {
                _pendingRequests.Add(cur);
            }
            else
            {
                _pageCache.Touch(cur);
            }
            cur = PageTable.MapToAncestor(cur, cur.mip + 1);
        }
    }

    private void FlushPageRequests()
    {
        if (_pendingRequests.Count == 0)
            return;

        // mip 越大越先请求（祖先优先）
        _pendingRequests.Sort((a, b) => b.mip.CompareTo(a.mip));

        foreach (var id in _pendingRequests)
        {
            _streamer.RequestPage(id);
        }

        _pendingRequests.Clear();
    }

    private void ProcessLoadedPages()
    {
        int persistentMip = MipCount - 1;
        int processLimit = 10;
        while (processLimit-- > 0 && _streamer.TryGetLoadedPage(out (int textureId, VirtualPageID id, IMemoryOwner<byte> data) result))
        {
            int targetSlot;
            if (result.id.mip == persistentMip)
            {
                _pageCache.TryGetPhysicalSlot(result.id, out targetSlot);

                _loadedPersistentCount++;
                if (_loadedPersistentCount == _pageTable.DynamicPageOffset)
                {
                    _persistentReady = true;
                    Logger.Info("[VT] Persistent Pages Ready");
                }
            }
            else
            {
                if (!_persistentReady)
                    continue;
                if (!_pageCache.TryGetPhysicalSlot(result.id, out targetSlot))
                {
                    if (!_physicalTexture.TryAllocateDynamic(out targetSlot))
                    {
                        // 2. 满了 -> LRU 淘汰
                        if (!_pageCache.EvictOne(out var evictedId, out int evictedSlot))
                            continue;
                        _pageTable.UnmapPage(evictedId);
                        //_physicalTexture.FreeDynamic(evictedSlot); 
                        targetSlot = evictedSlot;
                    }
                }

                if(targetSlot != -1)
                {
                    _pageCache.Add(result.id, targetSlot);
                }
            }
            _pageCache.MarkLoadComplete(result.id);
            _physicalTexture.AddUpdate(result.textureId, targetSlot, result.data);
            _pageTable.MapPage(result.id, targetSlot);
        }
    }

    private void LoadResidentPages()
    {
        int persistentMip = MipCount - 1;
        int pageCountX = _pageTable.PersistentMipWidth;
        int pageCountY = _pageTable.PersistentMipHeight;
        for (int y = 0; y < pageCountY; ++y)
        {
            for (int x = 0; x < pageCountX; ++x)
            {
                var id = new VirtualPageID()
                {
                    x = x,
                    y = y,
                    mip = persistentMip,
                };
                _pageCache.TryMarkLoading(id);
                _streamer.RequestPage(id);
            }
        }
    }

    public GDTexture2DArray GetPhysicalTexture(int i)
    {
        return _physicalTexture[i];
    }

    public GDTexture2D GetPageTableInMipLevel(int mip)
    {
        return _pageTable.IndirectTextures[mip];
    }

    public void Dispose()
    {
        _pageTable.Dispose();
        _physicalTexture.Dispose();
    }
}