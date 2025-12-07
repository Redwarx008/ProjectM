using Godot;
using System;
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
    public static readonly int MaxPageTableUpdateEntry = 500;

    public static readonly int MaxPageTableMipInGpu = 8;

    private GDTexture2D[] _indirectTextures;

    // 活跃页面缓存：Key=虚拟页ID, Value=物理图集槽位索引
    // Capacity 应设置为物理图集的最大页数 (MaxPageCount)
    private LRUCollection<VirtualPageID, int> _dynamicPages;

    // 加载中集合：只记录 ID，防止重复 I/O
    private HashSet<VirtualPageID> _loadingPages = [];

    private PageTableUpdateEntry[] _pendingEntries => _backPendingEntries;

    private int _pendingEntryCount = 0;

    private PageTableUpdateEntry[] _frontPendingEntries = new PageTableUpdateEntry[MaxPageTableUpdateEntry];
    private PageTableUpdateEntry[] _backPendingEntries = new PageTableUpdateEntry[MaxPageTableUpdateEntry];

    private readonly object _bufferLock = new();

    //渲染线程将在 callback 中读取这两个引用（volatile 保证引用的原子读取/写入可见性）
    private volatile PageTableUpdateEntry[]? _dispatchPendingEntries;

    private volatile int _dispatchPendingEntryCount;

    private Callable _cachedDispatchCallback;

    private int _persistentMip;

    private Rid _pipeline;

    private Rid _descriptorSet;

    private GDBuffer _pageTableUpdateEntriesBuffer = null!;

    private bool _pipelineInited;  //todo: 可能并不需要?

    public int DynamicPageOffset { get; set; } = int.MaxValue;

    public int PersistentMipWidth { get; private set; }

    public int PersistentMipHeight { get; private set; }
    public PageTable(int l0Width, int l0Height, int tileSize, int maxPhysicalPages, int mipCount)
    {
        _dynamicPages = new LRUCollection<VirtualPageID, int>(maxPhysicalPages);
        _indirectTextures = new GDTexture2D[mipCount];
        _persistentMip = mipCount - 1;
        _cachedDispatchCallback = Callable.From(() =>
        {
            var list = _dispatchPendingEntries;
            var counter = _dispatchPendingEntryCount;
            if (list != null)
            {
                Dispatch(list, counter);
            }
        });
        CalculatePersistentMipLayout(l0Width, l0Height, tileSize);
        InitializeFallbackToPersistentMip(l0Width, l0Height, tileSize);
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
                    Format = Godot.RenderingDevice.DataFormat.R16G16Sfloat,
                    Width = (uint)pageCountX,
                    Height = (uint)pageCountY,
                    Mipmaps = 1,
                    UsageBits = Godot.RenderingDevice.TextureUsageBits.StorageBit | Godot.RenderingDevice.TextureUsageBits.SamplingBit
        | Godot.RenderingDevice.TextureUsageBits.CanCopyToBit | Godot.RenderingDevice.TextureUsageBits.CanCopyFromBit
                };
                _indirectTextures[mip] = GDTexture2D.Create(desc);
            }
            Debug.Assert(PersistentMipWidth == (int)_indirectTextures[_indirectTextures.Length - 1].Width);
            Debug.Assert(PersistentMipHeight == (int)_indirectTextures[_indirectTextures.Length - 1].Height);
            BuildPipeline();
            _pipelineInited = true;
        }));
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
        // 只有动态页面需要维护集合
        if (id.mip < _persistentMip)
        {
            _loadingPages.Remove(id);
            _dynamicPages.AddOrUpdate(id, slot);
        }

        _pendingEntries[_pendingEntryCount++] = new PageTableUpdateEntry
        {
            x = id.x,
            y = id.y,
            mip = id.mip,
            physicalLayer = slot,
            activeMip = id.mip,
        };
    }

    public void DeactivatePage(VirtualPageID id)
    {
        if (id.mip == _persistentMip) return; 

        // 1. 从 LRU 中移除
        if (_dynamicPages.Remove(id))
        {
            var (ancestorId, ancestorSlot) = FindNearestResidentAncestor(id);

            // 3. 更新 GPU 页表指向祖先
            _pendingEntries[_pendingEntryCount++] = new PageTableUpdateEntry
            {
                x = id.x,
                y = id.y,
                mip = id.mip,
                physicalLayer = ancestorSlot,                    // 指向 ancestor 的槽位
                activeMip = ancestorId.mip,                      // 并告诉 Shader 实际 LOD 是 ancestor 的
            };
        }
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
        if(!_pipelineInited || _pendingEntryCount == 0) return;
        SubmitUpdateForRender();
        _pendingEntryCount = 0;
    }

    private void SubmitUpdateForRender()
    {
        // 锁住交换 —— 确保可见性与原子性
        lock (_bufferLock)
        {
            (_frontPendingEntries, _backPendingEntries) = (_backPendingEntries, _frontPendingEntries);
            _dispatchPendingEntries = _frontPendingEntries;
            _dispatchPendingEntryCount = _pendingEntryCount;
        }

        RenderingServer.CallOnRenderThread(_cachedDispatchCallback);
    }

    private void Dispatch(PageTableUpdateEntry[] pendingEntries, int pendingEntryCount)
    {
        var rd = RenderingServer.GetRenderingDevice();
        unsafe
        {
            ReadOnlySpan<byte> counterBytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref pendingEntryCount, 1));
            rd.BufferUpdate(_pageTableUpdateEntriesBuffer, 0, sizeof(int), counterBytes);

            ReadOnlySpan<byte> dataBytes = MemoryMarshal.AsBytes(pendingEntries.AsSpan());
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
            pageTableUpdateEntriesBufferSize = (uint)(4 + sizeof(PageTableUpdateEntry) * MaxPageTableUpdateEntry);
        }
        _pageTableUpdateEntriesBuffer = GDBuffer.CreateStorage(pageTableUpdateEntriesBufferSize);
        Rid shader = ShaderHelper.CreateComputeShader("res://Shaders/Compute/PageTableCompute/glsl");

        var indirectTextureBinding = new RDUniform()
        {
            UniformType = RenderingDevice.UniformType.Image,
            Binding = 0
        };
        for(int mip = 0; mip < _indirectTextures.Length; ++mip)
        {
            indirectTextureBinding.AddId(_indirectTextures[mip]);
        }
        // Fill the remaining slots.
        for(int mip = _indirectTextures.Length; mip < MaxPageTableMipInGpu; ++mip)
        {
            indirectTextureBinding.AddId(_indirectTextures[_indirectTextures.Length - 1]);
        }

        var pageTableUpdateEntriesBufferBinding = new RDUniform()
        {
            UniformType = RenderingDevice.UniformType.StorageBuffer,
            Binding = 1
        };
        pageTableUpdateEntriesBufferBinding.AddId(_pageTableUpdateEntriesBuffer);

        _descriptorSet = rd.UniformSetCreate([indirectTextureBinding, pageTableUpdateEntriesBufferBinding], shader, 0);

        _pipeline = rd.ComputePipelineCreate(shader);

        rd.FreeRid(shader);
    }

    /// <summary>
    /// 寻找最近的已常驻祖先页面
    /// </summary>
    /// <returns>返回 (祖先ID, 祖先的物理槽位)</returns>
    private (VirtualPageID id, int slot) FindNearestResidentAncestor(VirtualPageID startId)
    {
        VirtualPageID current = startId.GetParent();

        while (true)
        {
            int slot = GetPhysicalSlot(current);
            if (slot != -1)
            {
                return (current, slot);
            }

            // 理论上不会死循环，因为最终会命中 _persistentStartMip 层的根节点
            current = current.GetParent();
        }
    }
    private void InitializeFallbackToPersistentMip(int l0Width, int l0Height, int tileSize)
    {
        // 遍历所有非常驻层级
        int width = l0Width;
        int height = l0Height;
        for (int mip = 0; mip < _persistentMip; mip++)
        {
            width = (int)Math.Ceiling(width / (float)tileSize);
            height = (int)Math.Ceiling(height / (float)tileSize);

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    var id = new VirtualPageID()
                    {
                        x = x,
                        y = y,
                        mip = mip,
                    };

                    // 查找它的 Root 祖先 (这里简化为直接指向唯一的 Root)
                    // 如果有多个 Root Page，需要计算坐标对应关系
                    var (ancestorId, ancestorSlot) = FindNearestResidentAncestor(id);

                    // 立即添加到 GPU 更新队列
                    _pendingEntries[_pendingEntryCount++] = new PageTableUpdateEntry
                    {
                        x = id.x,
                        y = id.y,
                        mip = mip,
                        physicalLayer = ancestorSlot,
                        activeMip = ancestorId.mip,
                    };
                }
            }
            width = (int)Math.Ceiling(width * 0.5f);
            height = (int)Math.Ceiling(height * 0.5f);
        }
    }

    private void CalculatePersistentMipLayout(int l0Width, int l0Height, int tileSize)
    {
        int scale = 1 << _persistentMip;
        int w = (int)Math.Ceiling(l0Width / (float)(tileSize * scale));
        int h = (int)Math.Ceiling(l0Height / (float)(tileSize * scale));
        w = Math.Max(1, w); h = Math.Max(1, h);
        PersistentMipWidth = w; PersistentMipHeight = h;
        DynamicPageOffset = PersistentMipWidth * PersistentMipHeight;
    }

    public void Dispose()
    {
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            var rd = RenderingServer.GetRenderingDevice();
            rd.FreeRid(_pipeline);
            _pageTableUpdateEntriesBuffer.Dispose();
        }));
    }
}