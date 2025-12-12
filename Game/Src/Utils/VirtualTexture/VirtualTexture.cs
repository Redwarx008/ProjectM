using Core;
using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using Logger = Core.Logger;

namespace ProjectM;

public struct VirtualPageID : System.IEquatable<VirtualPageID>, IComparable<VirtualPageID>
{
    public int mip;
    public int x;
    public int y;

    // 获取父级页面 (Mip + 1, 坐标 / 2)
    public VirtualPageID GetParent()
    {
        // 假设 mip 越大越粗糙，mip 0 最精细
        return new VirtualPageID()
        {
            mip = this.mip + 1,
            x = this.x >> 1,
            y = this.y >> 1
        };
    }

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
    private PhysicalTexture _physicalTexture;
    private StreamingManager _streamer;

    private int _loadedPersistentCount = 0;

    public VirtualTexture(uint maxPageCount, VirtualTextureDesc[] vtDescs)
    {
        Debug.Assert(maxPageCount <= 2048);
        _streamer = new StreamingManager();
        for (int i = 0; i < vtDescs.Length; ++i)
        {
            _streamer.RegisterFile(vtDescs[i].filePath);
        }
        var vTInfo = _streamer.GetVTInfo();
        MaxPageCount = (int)maxPageCount;
        MipCount = vTInfo.mipmaps;
        Width = vTInfo.width;
        Height = vTInfo.height;
        TileSize = vTInfo.tileSize;
        Padding = vTInfo.padding;
        _pageTable = new PageTable(Width, Height, TileSize, (int)maxPageCount, MipCount);
        _physicalTexture = new PhysicalTexture(maxPageCount, _pageTable.DynamicPageOffset, vTInfo, vtDescs);
        LoadResidentPages();
    }

    public void Update()
    {
        ProcessLoadedPages();
        _pageTable.Update();
    }

    public void RequestPage(VirtualPageID pageID)
    {
        PageStatus status = _pageTable.QueryStatus(pageID);
        if (status == PageStatus.NotLoaded)
        {
            _pageTable.MarkLoading(pageID);
            _streamer.RequestPage(pageID);
        }
    }

    private void ProcessLoadedPages()
    {
        int persistentMip = MipCount - 1;
        int processLimit = 10;
        while (processLimit-- > 0 && _streamer.TryGetLoadedPage(out (int textureId, VirtualPageID id, byte[] data) result))
        {
            int targetSlot = -1;

            // 分支 A: 常驻页面
            if (result.id.mip == persistentMip)
            {
                // [优化] 1. 直接算槽位
                targetSlot = _pageTable.GetPhysicalSlot(result.id);

                _physicalTexture.Upload(result.textureId, targetSlot, result.data);

                // 3. 激活
                _pageTable.ActivatePage(result.id, targetSlot);

                // 4. 检查是否全部就绪
                _loadedPersistentCount++;
                if (_loadedPersistentCount == _pageTable.DynamicPageOffset)
                {
                    Logger.Info("[VT] Persistent Pages Ready");
                }
            }
            // 分支 B: 动态页面
            else
            {
                // 1. 尝试分配空闲
                if (!_physicalTexture.TryAllocateDynamic(out targetSlot))
                {
                    // 2. 满了 -> LRU 淘汰
                    if (_pageTable.TryGetLRUPage(out VirtualPageID lruID, out int lruSlot))
                    {
                        _pageTable.DeactivatePage(lruID);
                        _physicalTexture.FreeDynamic(lruSlot); // 逻辑上释放，实际马上复用
                        targetSlot = lruSlot;
                    }
                    else
                    {
                        // 无法淘汰 (理论不应发生，除非 MaxPageCount 太小)
                        continue;
                    }
                }

                // 3. 上传 & 激活
                _physicalTexture.Upload(result.textureId, targetSlot, result.data);
                _pageTable.ActivatePage(result.id, targetSlot);
            }
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
                _pageTable.MarkLoading(id);
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