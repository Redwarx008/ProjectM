using Core;
using Godot;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.ConstrainedExecution;
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
    public override string ToString()
    {
        return $"mip: {mip}, x: {x}, y: {y}";
    }
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
    public int ProcessLimit { get; set; } = 15;
    public bool Inited { get; private set; }

    public static readonly int MaxPageTableMipInGpu = 10;

    private PageTable _pageTable;
    private PageCache _pageCache;
    private StreamingManager _streamer;

    private int _loadedPersistentCount = 0;

    private readonly Dictionary<VirtualPageID, int> _requestCountDic = [];

    private readonly List<(VirtualPageID page, int count)> _pendingRequests = [];

    public VirtualTexture(uint pageCapacity, ReadOnlySpan<VirtualTextureDesc> vtDesc)
    {
        Debug.Assert(pageCapacity <= 2048);
        _streamer = new StreamingManager(vtDesc);

        var vTInfo = _streamer.GetVTHeader();
        MaxPageCount = (int)pageCapacity;
        MipCount = vTInfo.mipmaps;
        Width = vTInfo.width;
        Height = vTInfo.height;
        TileSize = vTInfo.tileSize;
        Padding = vTInfo.padding;
        Span<RenderingDevice.DataFormat> formats = stackalloc RenderingDevice.DataFormat[vtDesc.Length];
        for (int i = 0; i < vtDesc.Length; ++i)
        {
            formats[i] = vtDesc[i].format;
        }
        _pageCache = new PageCache(_streamer, (int)pageCapacity, TileSize + 2 * Padding, MipCount, formats);
        _pageTable = new PageTable(_pageCache, Width, Height, TileSize, MipCount);
    }

    public void Update()
    {
        FlushPageRequests();
        _streamer.Update();
        _pageTable.UpdateUnmap();
        _pageCache.UpdateTextureArray();
        _pageTable.UpdateMap();
    }

    public void RequestPage(VirtualPageID id)
    {
        
        VirtualPageID cur = id;
        while(cur.mip < MipCount)
        {
            if(!_pageCache.Touch(cur))
            {
                if(_requestCountDic.TryGetValue(cur, out int value))
                {
                    _requestCountDic[cur] = ++value;
                }
                else
                {
                    _requestCountDic[cur] = 1;
                }
            }

            cur = PageTable.MapToAncestor(cur, cur.mip + 1);
        }
    }

    private void FlushPageRequests()
    {
        if (_requestCountDic.Count == 0)
            return;

        foreach(var requestAndCount in _requestCountDic)
        {
            _pendingRequests.Add((requestAndCount.Key, requestAndCount.Value));
        }

        _pendingRequests.Sort((a, b) =>
        {
            if(a.page.mip != b.page.mip)
            {
                return b.page.mip.CompareTo(a.page.mip);
            }

            return b.count.CompareTo(a.count);
        });

        int limit = Math.Min(_pendingRequests.Count, ProcessLimit);
        for(int i = 0; i < limit; ++i)
        {
            _pageCache.Request(_pendingRequests[i].page);
        }

        _pendingRequests.Clear();
        _requestCountDic.Clear();
    }


    public GDTexture2DArray GetTexture(int i)
    {
        return _pageCache.GetTexture(i);
    }

    public GDTexture2D? GetIndirectTexture()
    {
        return _pageTable.IndirectTexture;
    }

    public Vector2[] GetIndirectTextureSize()
    {
        return _pageTable.GetRealMipBounds();
    }

    public void Dispose()
    {
        _pageCache.Dispose();
        _pageTable.Dispose();
    }
}