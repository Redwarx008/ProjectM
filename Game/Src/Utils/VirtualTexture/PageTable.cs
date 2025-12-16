using Godot;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using static ProjectM.VirtualTexture;

namespace ProjectM;

[StructLayout(LayoutKind.Sequential)]
public struct PageTableUpdateEntry
{
    public int x;
    public int y;
    public int mip;
    public int physicalLayer;
    public int activeMip;
}

public enum PageStatus
{
    NotLoaded, // 不在 VRAM 中，也未请求
    Loading,   // 已发送加载请求，正在后台加载
    InVRAM     // 已在 VRAM 中
}

internal class PageTable : IDisposable
{
    private sealed class BatchQueue
    {
        private readonly Queue<Batch> _queue = new();

        public record struct Batch(PageTableUpdateEntry[] Entries, int Count);

        public void Enqueue(Batch batch)
        {
            lock (_queue)
                _queue.Enqueue(batch);
        }

        public bool TryDequeue(out Batch batch)
        {
            lock (_queue)
                return _queue.TryDequeue(out batch);
        }
    }

    public GDTexture2D[] IndirectTextures { get; private set; }

    // 活跃页面缓存：Key=虚拟页ID, Value=物理图集槽位索引
    // Capacity 应设置为物理图集的最大页数 (MaxPageCount)
    private LRUCollection<VirtualPageID, int> _dynamicPages;

    // 加载中集合：只记录 ID，防止重复 I/O
    private HashSet<VirtualPageID> _loadingPages = [];

    private ArrayPool<PageTableUpdateEntry> _arrayPool;

    private PageTableUpdateEntry[] _currentPendingEntries;

    private int _currentPendingCount = 0;

    private BatchQueue _pendingBatches = new();

    private Callable _cachedDispatchCallback;

    private int _persistentMip;

    private int _maxPageTableUpdateEntry = 2000;

    private Rid _pipeline;

    private Rid _descriptorSet;

    private Rid _shader;

    private GDBuffer _pageTableUpdateEntriesBuffer = null!;

    private bool _pipelineInited;  //todo: 可能并不需要?

    public int DynamicPageOffset { get; set; } = int.MaxValue;

    public int PersistentMipWidth { get; private set; }

    public int PersistentMipHeight { get; private set; }
    public PageTable(int l0Width, int l0Height, int tileSize, int maxPhysicalPages, int mipCount)
    {
        IndirectTextures = new GDTexture2D[mipCount];
        _persistentMip = mipCount - 1;
        _cachedDispatchCallback = Callable.From(DispatchBatches);
        CalculateLayout(l0Width, l0Height, tileSize);
        _arrayPool = ArrayPool<PageTableUpdateEntry>.Create(_maxPageTableUpdateEntry, 2);
        _currentPendingEntries = _arrayPool.Rent(_maxPageTableUpdateEntry);
        _dynamicPages = new LRUCollection<VirtualPageID, int>(maxPhysicalPages - DynamicPageOffset);
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            int width = l0Width;
            int height = l0Height;
            for (int mip = 0; mip < mipCount; ++mip)
            {
                int pageCountX = (int)Math.Ceiling(width / (float)tileSize);
                int pageCountY = (int)Math.Ceiling(height / (float)tileSize);
                var desc = new GDTextureDesc()
                {
                    Format = Godot.RenderingDevice.DataFormat.R16G16Uint,
                    Width = (uint)pageCountX,
                    Height = (uint)pageCountY,
                    Mipmaps = 1,
                    UsageBits = Godot.RenderingDevice.TextureUsageBits.StorageBit | Godot.RenderingDevice.TextureUsageBits.SamplingBit
        | Godot.RenderingDevice.TextureUsageBits.CanCopyToBit | Godot.RenderingDevice.TextureUsageBits.CanCopyFromBit
                };
                IndirectTextures[mip] = GDTexture2D.Create(desc);
                width = (int)Math.Ceiling(width * 0.5f);
                height = (int)Math.Ceiling(height * 0.5f);
            }
            Debug.Assert(PersistentMipWidth == (int)IndirectTextures[IndirectTextures.Length - 1].Width);
            Debug.Assert(PersistentMipHeight == (int)IndirectTextures[IndirectTextures.Length - 1].Height);
            BuildPipeline();
            _pipelineInited = true;
        }));
        InitializeFallbackToPersistentMip(l0Width, l0Height, tileSize);
        SubmitCurrentBatch();
    }

    public PageStatus QueryStatus(VirtualPageID pageID)
    {
        if(pageID.mip == _persistentMip)
        {
            return PageStatus.InVRAM;
        }
        if (_dynamicPages.TryGetValue(pageID, out int _))
        {
            return PageStatus.InVRAM;
        }
        if (_loadingPages.Contains(pageID))
        {
            return PageStatus.Loading;
        }
        return PageStatus.NotLoaded;
    }

    public void MarkLoading(VirtualPageID id)
    {
        if (id.mip < _persistentMip &&!_dynamicPages.ContainsKey(id))
        {
            _loadingPages.Add(id);
        }
    }

    /// <summary>
    /// 查找最适合淘汰的页面 (最久未使用)
    /// 返回是否找到，以及 ID 和占用的槽位
    /// </summary>
    public bool TryGetLRUPage(out VirtualPageID id, out int slot)
    {
        return _dynamicPages.TryPeekOldest(out id, out slot);
    }

    public void ActivatePage(VirtualPageID id, int slot)
    {
        _loadingPages.Remove(id);
       
        _currentPendingEntries[_currentPendingCount++] = new PageTableUpdateEntry
        {
            x = id.x,
            y = id.y,
            mip = id.mip,
            physicalLayer = slot,
            activeMip = id.mip,
        };
        // 只有动态页面需要维护集合
        if (id.mip < _persistentMip)
        {
            Debug.Assert(!_dynamicPages.ContainsKey(id));
            _dynamicPages.Add(id, slot);
        }
    }

    public bool TryEvictOne(out VirtualPageID id, out int slot)
    {
        if (_dynamicPages.TryPopOldest(out id, out slot))
        {
            DeactivatePage(id);
            return true;
        }
        id = default;
        slot = -1;
        return false;
    }

    private void DeactivatePage(VirtualPageID id)
    {
        if (id.mip == _persistentMip) return;

        VirtualPageID ancestorId = MapToAncestor(id, _persistentMip);

        // 3. 更新 GPU 页表指向祖先
        _currentPendingEntries[_currentPendingCount++] = new PageTableUpdateEntry
        {
            x = id.x,
            y = id.y,
            mip = id.mip,
            physicalLayer = GetPhysicalSlot(ancestorId),                    // 指向 ancestor 的槽位
            activeMip = ancestorId.mip,                      // 并告诉 Shader 实际 LOD 是 ancestor 的
        };
    }

    public int GetPhysicalSlot(VirtualPageID id)
    {
        if (id.mip == _persistentMip)
        {
            return id.x + PersistentMipWidth * id.y;
        }

        if (_dynamicPages.TryGetValue(id, out int slot))
        {
            return slot;
        }
        return -1;
    }

    public void Update()
    {
        if(!_pipelineInited || _currentPendingCount == 0) return;
        SubmitCurrentBatch();
    }

    private void SubmitCurrentBatch()
    {
        _pendingBatches.Enqueue(
            new BatchQueue.Batch(_currentPendingEntries, _currentPendingCount));
        _currentPendingEntries = _arrayPool.Rent(_maxPageTableUpdateEntry);
        _currentPendingCount = 0;

        RenderingServer.CallOnRenderThread(_cachedDispatchCallback);
    }

    private void DispatchBatches()
    {
        while (_pendingBatches.TryDequeue(out var batch))
        {
            var entries = batch.Entries;
            var count = batch.Count;

            // GPU 提交
            DispatchOneBatch(entries, count);

            // 用完归还池
            _arrayPool.Return(entries, clearArray: false);
        }
    }
    private void DispatchOneBatch(PageTableUpdateEntry[] pendingEntries, int pendingEntryCount)
    {
        var rd = RenderingServer.GetRenderingDevice();
        unsafe
        {
            ReadOnlySpan<byte> counterBytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref pendingEntryCount, 1));
            rd.BufferUpdate(_pageTableUpdateEntriesBuffer, 0, sizeof(int), counterBytes);

            ReadOnlySpan<byte> dataBytes = MemoryMarshal.AsBytes(pendingEntries.AsSpan(0, pendingEntryCount));
            rd.BufferUpdate(_pageTableUpdateEntriesBuffer, sizeof(int), (uint)(sizeof(PageTableUpdateEntry) * pendingEntryCount), dataBytes);
        }

        var list = rd.ComputeListBegin();
        rd.ComputeListBindComputePipeline(list, _pipeline);
        rd.ComputeListBindUniformSet(list, _descriptorSet, 0);
        rd.ComputeListDispatch(list, (uint)Math.Ceiling(pendingEntryCount / 64f), 1, 1);
        rd.ComputeListEnd();
    }

    private void BuildPipeline()
    {
        var rd = RenderingServer.GetRenderingDevice();
        uint pageTableUpdateEntriesBufferSize;
        unsafe
        {
            pageTableUpdateEntriesBufferSize = (uint)(4 + sizeof(PageTableUpdateEntry) * _maxPageTableUpdateEntry);
        }
        _pageTableUpdateEntriesBuffer = GDBuffer.CreateStorage(pageTableUpdateEntriesBufferSize);
        _shader = ShaderHelper.CreateComputeShader("res://Shaders/Compute/PageTableCompute.glsl");

        var indirectTextureBinding = new RDUniform()
        {
            UniformType = RenderingDevice.UniformType.Image,
            Binding = 0
        };
        for(int mip = 0; mip < IndirectTextures.Length; ++mip)
        {
            indirectTextureBinding.AddId(IndirectTextures[mip]);
        }
        // Fill the remaining slots.
        for(int mip = IndirectTextures.Length; mip < MaxPageTableMipInGpu; ++mip)
        {
            indirectTextureBinding.AddId(IndirectTextures[IndirectTextures.Length - 1]);
        }

        var pageTableUpdateEntriesBufferBinding = new RDUniform()
        {
            UniformType = RenderingDevice.UniformType.StorageBuffer,
            Binding = 1
        };
        pageTableUpdateEntriesBufferBinding.AddId(_pageTableUpdateEntriesBuffer);

        _descriptorSet = rd.UniformSetCreate([indirectTextureBinding, pageTableUpdateEntriesBufferBinding], _shader, 0);

        _pipeline = rd.ComputePipelineCreate(_shader);
        Debug.Assert(_pipeline != Constants.NullRid);
    }

    private void InitializeFallbackToPersistentMip(int l0Width, int l0Height, int tileSize)
    {
        // 遍历所有非常驻层级
        int width = l0Width;
        int height = l0Height;
        for (int mip = 0; mip < _persistentMip; mip++)
        {
            int tileX = (int)Math.Ceiling(width / (float)tileSize);
            int tileY = (int)Math.Ceiling(height / (float)tileSize);

            for (int y = 0; y < tileY; y++)
            {
                for (int x = 0; x < tileX; x++)
                {
                    var id = new VirtualPageID()
                    {
                        x = x,
                        y = y,
                        mip = mip,
                    };

                    // 查找它的 Root 祖先
                    // 如果有多个 Root Page，需要计算坐标对应关系
                    VirtualPageID ancestorId = MapToAncestor(id, _persistentMip);

                    // 立即添加到 GPU 更新队列
                    _currentPendingEntries[_currentPendingCount++] = new PageTableUpdateEntry
                    {
                        x = id.x,
                        y = id.y,
                        mip = mip,
                        physicalLayer = GetPhysicalSlot(ancestorId),
                        activeMip = ancestorId.mip,
                    };
                }
            }
            width = (int)Math.Ceiling(width * 0.5f);
            height = (int)Math.Ceiling(height * 0.5f);
        }
    }

    private void CalculateLayout(int l0Width, int l0Height, int tileSize)
    {
        int scale = 1 << _persistentMip;
        int w = (int)Math.Ceiling(l0Width / (float)(tileSize * scale));
        int h = (int)Math.Ceiling(l0Height / (float)(tileSize * scale));
        w = Math.Max(1, w); h = Math.Max(1, h);
        PersistentMipWidth = w; PersistentMipHeight = h;
        DynamicPageOffset = PersistentMipWidth * PersistentMipHeight;
        int pageCount = 0;
        int width = l0Width;
        int height = l0Height;
        for (int mip = 0; mip <= _persistentMip; ++mip)
        {
            int tileX = (int)Math.Ceiling(width / (float)tileSize);
            int tileY = (int)Math.Ceiling(height / (float)tileSize);
            pageCount += tileX * tileY;
            width = (int)Math.Ceiling(width * 0.5f);
            height = (int)Math.Ceiling(height * 0.5f);
        }
        _maxPageTableUpdateEntry = pageCount;
    }

    public void Dispose()
    {
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            var rd = RenderingServer.GetRenderingDevice();
            rd.FreeRid(_shader);
            _pageTableUpdateEntriesBuffer.Dispose();
            foreach(var texture in IndirectTextures)
            {
                texture.Dispose();
            }
        }));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static VirtualPageID MapToAncestor(VirtualPageID id, int ancestorMip)
    {
        int delta = ancestorMip - id.mip;
        float scale = 1 << delta;

        return new VirtualPageID
        {
            mip = ancestorMip,
            x = (int)MathF.Floor((id.x + 0.5f) / scale),
            y = (int)MathF.Floor((id.y + 0.5f) / scale)
        };
    }
}