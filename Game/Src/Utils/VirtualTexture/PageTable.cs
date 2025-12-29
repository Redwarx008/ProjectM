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



public enum PageStatus
{
    NotLoaded, // 不在 VRAM 中，也未请求
    Loading,   // 已发送加载请求，正在后台加载
    InVRAM     // 已在 VRAM 中
}

internal unsafe class PageTable : IDisposable
{
    private unsafe struct PageUpdateInfo : IComparable<PageUpdateInfo>, IEquatable<PageUpdateInfo>
    {
        public int x;
        public int y;
        public int mip;
        public PageEntry* entry;
        public int CompareTo(PageUpdateInfo other) => other.mip.CompareTo(mip);
        public bool Equals(PageUpdateInfo other)
        {
            return x == other.x &&
                   y == other.y &&
                   mip == other.mip &&
                   entry == other.entry;
        }
        public override bool Equals(object? obj)
        {
            return obj is PageUpdateInfo other && Equals(other);
        }
        public static bool operator ==(PageUpdateInfo left, PageUpdateInfo right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(PageUpdateInfo left, PageUpdateInfo right)
        {
            return !left.Equals(right);
        }
        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 23 + x.GetHashCode();
                hash = hash * 23 + y.GetHashCode();
                hash = hash * 23 + mip.GetHashCode();
                hash = hash * 23 + ((IntPtr)entry).GetHashCode();
                return hash;
            }
        }
    }

    private class PageUpdateInfoComparer : IComparer<PageUpdateInfo>
    {
        public int Compare(PageUpdateInfo a, PageUpdateInfo b)
        {
            int c;

            // 1. mip（高 mip 先）
            c = b.mip.CompareTo(a.mip);
            if (c != 0) return c;

            // 2. y
            c = a.y.CompareTo(b.y);
            if (c != 0) return c;

            // 3. x
            c = a.x.CompareTo(b.x);
            if (c != 0) return c;

            // 4. entry 指针（保证唯一性）
            return ((nint)a.entry).CompareTo((nint)b.entry);
        }
    }

    private struct PageEntry
    {
        public short mappingSlot = -1;        // 物理纹理层索引
        public ushort activeMip = 0;   // 数据的实际 Mip 等级 (用于 Shader 缩放)

        public PageEntry(int mappingSlot, int activeMip)
        {
            this.mappingSlot = (short)mappingSlot;
            this.activeMip = (ushort)activeMip;
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct ShaderPushConstants
    {
        public Vector2I origin;
        public Vector2I size;
        public int mip;
        private int _padding;
        public Vector2I value;
    }

    public GDTexture2D[] IndirectTextures { get; private set; }



    // 二维数组：第一维是 Mip 层级，第二维是莫顿排序的 Entry 数组
    private PageEntry** _mipBasePages;

    private (int w, int h)[] _mipRealBounds; // 记录每一层真实的图块数量

    private SortedSet<PageUpdateInfo>[] _pendingMapPagesPerMip;
    private SortedSet<PageUpdateInfo>[] _pendingReMapPagesPerMip;

    private Callable _cachedDispatchCallback;

    private int _persistentMip;

    private Rid _pipeline;

    private Rid _descriptorSet;

    private Rid _shader;


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
        _mipRealBounds = new (int w, int h)[mipCount];
        CalculateLayout(l0Width, l0Height, tileSize);
        _pendingMapPagesPerMip = new SortedSet<PageUpdateInfo>[mipCount];
        _pendingReMapPagesPerMip = new SortedSet<PageUpdateInfo>[mipCount];
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
        InitializeCpuTable();
    }

    public void MapPage(VirtualPageID id, int physicalSlot)
    {
        Debug.Assert(IsValid(id.mip, id.x, id.y));
        // 设置当前页面的状态
        uint idx = MathExtension.EncodeMorton(id.x, id.y);

        PageEntry* pageEntry = _mipBasePages[id.mip] + idx;
        pageEntry->mappingSlot = (short)physicalSlot;
        pageEntry->activeMip = (ushort)id.mip;

        AddUpdateMap(id.x, id.y, id.mip, id.mip, pageEntry);

        int mipOffset = 0;
        int range = 1;
        for (int mip = id.mip - 1; mip >= 0; --mip)
        {
            mipOffset += 1;
            range *= 4;
            int subPageIdxBegin = (int)idx << (mipOffset * 2);
            for (int subPageIdx = subPageIdxBegin; subPageIdx < subPageIdxBegin + range; ++subPageIdx)
            {
                (int x, int y) coord = MathExtension.DecodeMorton((uint)subPageIdx);
                if (!IsValid(mip, coord.x, coord.y))
                {
                    continue;
                }

                PageEntry* subPageEntry = _mipBasePages[mip] + subPageIdx;
                if (subPageEntry->mappingSlot != -1 && subPageEntry->activeMip == mip) //当子页面已加载资源时需要将其区域再覆盖一次
                {
                    for (int subPageMip = mip; subPageMip >= 0; --subPageMip)
                    {
                        AddUpdateMap(coord.x, coord.y, mip, subPageMip, subPageEntry);
                    }
                }
            }

            AddUpdateMap(id.x, id.y, id.mip, mip, pageEntry);
        }
    }

    public void RemapPage(VirtualPageID id)
    {
        Debug.Assert(IsValid(id.mip, id.x, id.y));
        // 向上寻找最近有效的祖先作为新的 Fallback 来源
        VirtualPageID fallbackID = id;
        int fallbackSlot = -1;
        int fallbackMip = -1;

        for (int m = id.mip + 1; m <= _persistentMip; m++)
        {
            VirtualPageID ancestor = MapToAncestor(id, m);
            uint idxInMip = MathExtension.EncodeMorton(ancestor.x, ancestor.y);
            PageEntry* entry = _mipBasePages[m] + idxInMip;
            if (entry->activeMip == m && entry->mappingSlot != -1)
            {
                fallbackSlot = entry->mappingSlot;
                fallbackMip = entry->activeMip;
                break;
            }
        }

        uint idx = MathExtension.EncodeMorton(id.x, id.y);

        PageEntry* pageEntry = _mipBasePages[id.mip] + idx;
        pageEntry->mappingSlot = (short)fallbackSlot;
        pageEntry->activeMip = (ushort)fallbackMip;

        AddUpdateReMap(id.x, id.y, id.mip, id.mip, pageEntry);

        int mipOffset = 0;
        int range = 1;
        for (int mip = id.mip - 1; mip >= 0; --mip)
        {
            mipOffset += 1;
            range *= 4;
            int subPageIdxBegin = (int)idx << (mipOffset * 2);
            for (int subPageIdx = subPageIdxBegin; subPageIdx < subPageIdxBegin + range; ++subPageIdx)
            {
                (int x, int y) coord = MathExtension.DecodeMorton((uint)subPageIdx);
                if (!IsValid(mip, coord.x, coord.y))
                {
                    continue;
                }

                PageEntry* subPageEntry = _mipBasePages[mip] + subPageIdx;
                if (subPageEntry->mappingSlot != -1 && subPageEntry->activeMip == mip) //当子页面已加载资源时需要将其区域再覆盖一次
                {
                    for (int subPageMip = mip; subPageMip >= 0; --subPageMip)
                    {
                        AddUpdateReMap(coord.x, coord.y, mip, subPageMip, subPageEntry);
                    }
                }
            }

            AddUpdateReMap(id.x, id.y, id.mip, mip, pageEntry);
        }
    }



    public void UpdateMap()
    {
        UpdatePage(_pendingMapPagesPerMip);
    }

    public void UpdateReMap()
    {
        UpdatePage(_pendingReMapPagesPerMip);
    }

    private void UpdatePage(SortedSet<PageUpdateInfo>[] pageUpdateInfosPerMip)
    {
        for (int coveredMip = 0; coveredMip < pageUpdateInfosPerMip.Length; coveredMip++)
        {
            SortedSet<PageUpdateInfo> pageUpdateInfos = pageUpdateInfosPerMip[coveredMip];
            foreach (PageUpdateInfo info in pageUpdateInfos)
            {
                int scale = 1 << (info.mip - coveredMip);

                var origin = new Vector2I(
                    info.x * scale,
                    info.y * scale
                );

                var bounds = _mipRealBounds[coveredMip];

                var size = new Vector2I(
                    Math.Min(scale, bounds.w - origin.X),
                    Math.Min(scale, bounds.h - origin.Y)
                );

                if (size.X <= 0 || size.Y <= 0)
                    continue;

                var entry = info.entry;

                var value = new Vector2I(
                    entry->mappingSlot,
                    entry->activeMip
                );

                RenderingServer.CallOnRenderThread(Callable.From(() =>
                {
                    PageTableUpdate(coveredMip, origin, size, value);
                }));
            }
            pageUpdateInfos.Clear();
        }
    }

    private void PageTableUpdate(int mip, Vector2I origin, Vector2I size, Vector2I value)
    {
        var pushConstants = new ShaderPushConstants()
        {
            origin = origin,
            size = size,
            mip = mip,
            value = value,
        };
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan<ShaderPushConstants>(ref pushConstants, 1));
        var rd = RenderingServer.GetRenderingDevice();
        var list = rd.ComputeListBegin();
        rd.ComputeListBindComputePipeline(list, _pipeline);
        rd.ComputeListBindUniformSet(list, _descriptorSet, 0);
        rd.ComputeListSetPushConstant(list, bytes, (uint)bytes.Length);
        var bounds = _mipRealBounds[mip];
        rd.ComputeListDispatch(list, (uint)Math.Ceiling(bounds.w / 1f), (uint)Math.Ceiling(bounds.h / 1f), 1);
        rd.ComputeListEnd();
    }

    private void BuildPipeline()
    {
        var rd = RenderingServer.GetRenderingDevice();

        _shader = ShaderHelper.CreateComputeShader("res://Shaders/Compute/PageTableUpdate.glsl");

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

        _descriptorSet = rd.UniformSetCreate([indirectTextureBinding], _shader, 0);

        _pipeline = rd.ComputePipelineCreate(_shader);
        Debug.Assert(_pipeline != Constants.NullRid);
    }

    private void InitializeCpuTable()
    {
        int mipCount = _persistentMip + 1;
        _mipBasePages = (PageEntry**)NativeMemory.Alloc((nuint)(mipCount * sizeof(PageEntry*)));
        for (int mip = 0; mip < mipCount; mip++)
        {
            int tileX = _mipRealBounds[mip].w;
            int tileY = _mipRealBounds[mip].h;
            int maxDim = Math.Max(tileX, tileY);
            int pow2 = MathExtension.GetNextPowerOfTwo(maxDim);
            nuint totalEntries = (nuint)(pow2 * pow2);
            nuint sizeInBytes = totalEntries * (nuint)sizeof(PageEntry);
            PageEntry* ptr = (PageEntry*)NativeMemory.Alloc(sizeInBytes);
            _mipBasePages[mip] = ptr;
            var span = new Span<PageEntry>(ptr, (int)totalEntries);
            span.Fill(new PageEntry(-1, _persistentMip));

            var comparer = new PageUpdateInfoComparer();
            _pendingMapPagesPerMip[mip] = new SortedSet<PageUpdateInfo>(comparer);
            _pendingReMapPagesPerMip[mip] = new SortedSet<PageUpdateInfo>(comparer);
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
            _mipRealBounds[mip] = (tileX, tileY);
            pageCount += tileX * tileY;
            width = (int)Math.Ceiling(width * 0.5f);
            height = (int)Math.Ceiling(height * 0.5f);
        }
    }

    public void Dispose()
    {
        if (_mipBasePages != null)
        {
            int mipCount = _persistentMip + 1;
            for (int i = 0; i < mipCount; i++)
            {
                if (_mipBasePages[i] != null)
                {
                    NativeMemory.Free(_mipBasePages[i]);
                }
            }
            NativeMemory.Free(_mipBasePages);
            _mipBasePages = null;
        }

        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            var rd = RenderingServer.GetRenderingDevice();
            rd.FreeRid(_shader);
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
    private unsafe void AddUpdateMap(int x, int y, int mip, int coveredMip, PageEntry* entry)
    {
        _pendingMapPagesPerMip[coveredMip].Add(new PageUpdateInfo()
        {
            x = x,
            y = y,
            mip = mip,
            entry = entry
        });
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AddUpdateReMap(int x, int y, int mip, int coveredMip, PageEntry* entry)
    {
        _pendingReMapPagesPerMip[coveredMip].Add(new PageUpdateInfo()
        {
            x = x,
            y = y,
            mip = mip,
            entry = entry
        });
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsValid(int mip, int x, int y)
    {
        Debug.Assert(mip >= 0 && mip <= _persistentMip);
        var bound = _mipRealBounds[mip];
        return x >= 0 && x < bound.w && y >= 0 && y < bound.h;
    }

}