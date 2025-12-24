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
    private struct TableEntry
    {
        public short mappingSlot = -1;        // 物理纹理层索引
        public ushort activeMip = 0;   // 数据的实际 Mip 等级 (用于 Shader 缩放)

        public TableEntry(int mappingSlot, int activeMip)
        {
            this.mappingSlot = (short)mappingSlot;
            this.activeMip = (ushort)activeMip;
        }
    }

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



    private ArrayPool<PageTableUpdateEntry> _arrayPool;

    private TableEntry[][] _cpuTable; //[mip][entry]

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

    private int _l0Width; 
    private int _l0Height;
    private int _tileSize;

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
        _cpuTable = new TableEntry[mipCount][];
        _l0Width = l0Width;
        _l0Height = l0Height;
        _tileSize = tileSize;
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

    public void MapPage(VirtualPageID id, int physicalSlot)
    {
        // 设置当前页面的状态
        int idx = GetIndex(id);
        _cpuTable[id.mip][idx].mappingSlot = (short)physicalSlot;
        _cpuTable[id.mip][idx].activeMip = (ushort)id.mip;

        AddUpdate(id, physicalSlot, id.mip);

        // 触发 Fallback 更新：向下修正所有依赖此页面的子孙
        MapFallback(id, physicalSlot, id.mip);
    }

    /// <summary>
    /// 对应 LibVT 的页面卸载操作
    /// </summary>
    public void UnmapPage(VirtualPageID id)
    {
        int idx = GetIndex(id);
        if (_cpuTable[id.mip][idx].mappingSlot == -1) return;

        // 标记自己不再驻留
        _cpuTable[id.mip][idx].mappingSlot = -1;

        // 向上寻找最近的祖先作为新的 Fallback 来源
        VirtualPageID fallbackID = id;
        int fallbackSlot = -1;
        int fallbackMip = -1;

        for (int m = id.mip + 1; m <= _persistentMip; m++)
        {
            var ancestor = MapToAncestor(id, m);
            var entry = _cpuTable[m][GetIndex(ancestor)];
            if (entry.mappingSlot != - 1)
            {
                fallbackSlot = entry.mappingSlot;
                fallbackMip = entry.activeMip;
                break;
            }
        }

        _cpuTable[id.mip][idx].mappingSlot = (short)fallbackSlot;
        _cpuTable[id.mip][idx].activeMip = (ushort)fallbackMip;

        AddUpdate(id, fallbackSlot, fallbackMip);

        // 执行回退映射
        UnmapFallback(id, fallbackSlot, fallbackMip);
    }

    /// <summary>
    /// 递归向下修正：当父级变清晰时，所有原本用更模糊祖先的子孙都改用这个父级
    /// </summary>
    private void MapFallback(VirtualPageID parentId, int newSlot, int newMip)
    {
        if (parentId.mip <= 0) return;

        int childMip = parentId.mip - 1;

        for (int y = parentId.y * 2; y < parentId.y * 2 + 2; y++)
        {
            for (int x = parentId.x * 2; x < parentId.x * 2 + 2; x++)
            {
                if (!IsValid(childMip, x, y)) continue;

                int childIdx = y * GetMipTilesX(childMip) + x;
                ref var childEntry = ref _cpuTable[childMip][childIdx];

                // Check activeMip to ensure it's strictly resident
                if (childEntry.mappingSlot != -1 && childEntry.activeMip == childMip) continue;

                childEntry.mappingSlot = (short)newSlot;
                childEntry.activeMip = (ushort)newMip;
                AddUpdate(new VirtualPageID { mip = childMip, x = x, y = y }, newSlot, newMip);

                // 继续向下递归
                MapFallback(new VirtualPageID { mip = childMip, x = x, y = y }, newSlot, newMip);
            }
        }
    }

    /// <summary>
    /// 递归向下回退：当一个页面被卸载，所有依赖它的子孙都要找更上层的老祖宗
    /// </summary>
    private void UnmapFallback(VirtualPageID parentPage, int fallbackSlot, int fallbackMip)
    {
        if (parentPage.mip <= 0) return;

        int childMip = parentPage.mip - 1;
        for (int y = parentPage.y * 2; y < parentPage.y * 2 + 2; y++)
        {
            for (int x = parentPage.x * 2; x < parentPage.x * 2 + 2; x++)
            {
                if (!IsValid(childMip, x, y)) continue;

                int childIdx = y * GetMipTilesX(childMip) + x;
                ref var childEntry = ref _cpuTable[childMip][childIdx];

                // Check activeMip to ensure it's strictly resident
                if (childEntry.mappingSlot != -1 && childEntry.activeMip == childMip) continue;

                childEntry.mappingSlot = (short)fallbackSlot;
                childEntry.activeMip = (ushort)fallbackMip;
                AddUpdate(new VirtualPageID { mip = childMip, x = x, y = y }, fallbackSlot, fallbackMip);

                UnmapFallback(new VirtualPageID { mip = childMip, x = x, y = y }, fallbackSlot, fallbackMip);
            }
        }
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
        for (int mip = 0; mip <= _persistentMip; mip++)
        {
            int tileX = (int)Math.Ceiling(width / (float)tileSize);
            int tileY = (int)Math.Ceiling(height / (float)tileSize);

            _cpuTable[mip] = new TableEntry[tileX * tileY];
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
                    VirtualPageID ancestorId = MapToAncestor(id, _persistentMip);

                    int slot = ancestorId.x + PersistentMipWidth * ancestorId.y;

                    // 立即添加到 GPU 更新队列
                    AddUpdate(id, slot, ancestorId.mip);
                    int index = x + y * tileX;
                    _cpuTable[mip][index] = new TableEntry()
                    {
                        mappingSlot = (short)slot,
                        activeMip = (ushort)ancestorId.mip
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AddUpdate(VirtualPageID id, int slot, int activeMip)
    {
        if (_currentPendingCount >= _maxPageTableUpdateEntry)
        {
            SubmitCurrentBatch();
        }
        _currentPendingEntries[_currentPendingCount++] = new PageTableUpdateEntry
        {
            x = id.x,
            y = id.y,
            mip = id.mip,
            physicalLayer = slot,
            activeMip = activeMip
        };
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetIndex(VirtualPageID id) => id.y * GetMipTilesX(id.mip) + id.x;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetMipTilesX(int mip)
    {
        int scale = 1 << mip;
        int denom = _tileSize * scale;
        int w = (_l0Width + denom - 1) / denom;
        return w;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetMipTilesY(int mip)
    {
        int scale = 1 << mip;
        int denom = _tileSize * scale;
        int h = (_l0Height + denom - 1) / denom;
        return h;
    }

    private bool IsValid(int mip, int x, int y) => x >= 0 && x < GetMipTilesX(mip) && y >= 0 && y < GetMipTilesY(mip);
}