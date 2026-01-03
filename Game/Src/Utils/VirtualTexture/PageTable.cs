using Core;
using Godot;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using static ProjectM.VirtualTexture;
using Logger = Core.Logger;

namespace ProjectM;

internal unsafe class PageTable : IDisposable
{
    private unsafe struct PageUpdateInfo : IEquatable<PageUpdateInfo>
    {
        public int x;
        public int y;
        public int mip;
        public short mappingSlot;
        public ushort activeMip;
        public bool Equals(PageUpdateInfo other)
        {
            return x == other.x && y == other.y && mip == other.mip &&
                   mappingSlot == other.mappingSlot && activeMip == other.activeMip;
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
                hash = hash * 23 + mappingSlot.GetHashCode();
                hash = hash * 23 + activeMip.GetHashCode();
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

            // 4. mappingSlot
            c = a.mappingSlot.CompareTo(b.mappingSlot);
            if (c != 0) return c;

            // 5. activeMip
            return a.activeMip.CompareTo(b.activeMip);
        }
    }

    private class SortedSetArrayPool
    {
        private readonly ConcurrentStack<SortedSet<PageUpdateInfo>[]> _pool = new();
        private readonly int _mipCount;
        private readonly IComparer<PageUpdateInfo> _comparer;

        public SortedSetArrayPool(int mipCount, IComparer<PageUpdateInfo> comparer)
        {
            _mipCount = mipCount;
            _comparer = comparer;
        }

        public SortedSet<PageUpdateInfo>[] Rent()
        {
            if (_pool.TryPop(out var sets)) return sets;

            var newSets = new SortedSet<PageUpdateInfo>[_mipCount];
            for (int i = 0; i < _mipCount; i++)
            {
                newSets[i] = new SortedSet<PageUpdateInfo>(_comparer);
            }
            return newSets;
        }

        public void Return(SortedSet<PageUpdateInfo>[] sets, bool clear = false)
        {
            if(clear)
            {
                for (int i = 0; i < sets.Length; i++)
                {
                    sets[i].Clear();
                }
            }
            _pool.Push(sets);
        }
    }

    private struct PageEntry
    {
        public short mappingSlot = -1;        // 物理纹理层索引
        public ushort activeMip = 0;   // 数据的实际 Mip 等级 (用于 Shader 缩放)

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

    private (int w, int h)[] _mipRealBounds;

    private SortedSetArrayPool _setArrayPool;

    private SortedSet<PageUpdateInfo>[] _activeMapSets;
    private SortedSet<PageUpdateInfo>[] _activeRemapSets;

    private Callable _cachedDispatchCallback;

    private int _persistentMip;

    private Rid _pipeline;

    private Rid _descriptorSet;

    private Rid _shader;

    private bool _pipelineInited;  //todo: 可能并不需要?

    public int DynamicPageOffset { get; set; } = int.MaxValue;

    public int PersistentMipWidth { get; private set; }

    public int PersistentMipHeight { get; private set; }
    public PageTable(int l0Width, int l0Height, int tileSize, int maxPhysicalPages, int mipCount)
    {
        IndirectTextures = new GDTexture2D[mipCount];
        _setArrayPool = new SortedSetArrayPool(mipCount, new PageUpdateInfoComparer());
        _activeMapSets = _setArrayPool.Rent();
        _activeRemapSets = _setArrayPool.Rent();
        _persistentMip = mipCount - 1;
        _mipRealBounds = new (int w, int h)[mipCount];
        CalculateLayout(l0Width, l0Height, tileSize);
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
        uint rootMorton = MathExtension.EncodeMorton(id.x, id.y);

        PageEntry* pageEntry = _mipBasePages[id.mip] + rootMorton;
        pageEntry->mappingSlot = (short)physicalSlot;
        pageEntry->activeMip = (ushort)id.mip;

        AddUpdateMap(id.x, id.y, id.mip, id.mip, *pageEntry);

        //int mipOffset = 0;
        //int range = 1;
        //for (int mip = id.mip - 1; mip >= 0; --mip)
        //{
        //    mipOffset += 1;
        //    range *= 4;
        //    int subPageIdxBegin = (int)idx << (mipOffset * 2);
        //    for (int subPageIdx = subPageIdxBegin; subPageIdx < subPageIdxBegin + range; ++subPageIdx)
        //    {
        //        (int x, int y) coord = MathExtension.DecodeMorton((uint)subPageIdx);
        //        if (!IsValid(mip, coord.x, coord.y))
        //        {
        //            continue;
        //        }

        //        PageEntry* subPageEntry = _mipBasePages[mip] + subPageIdx;
        //        if (subPageEntry->mappingSlot != -1 && subPageEntry->activeMip == mip) //当子页面已加载资源时需要将其区域再覆盖一次
        //        {
        //            for (int subPageMip = mip; subPageMip >= 0; --subPageMip)
        //            {
        //                AddUpdateMap(coord.x, coord.y, mip, subPageMip, *subPageEntry);
        //            }
        //        }
        //    }

        //    AddUpdateMap(id.x, id.y, id.mip, mip, *pageEntry);
        //}

        for (int mip = id.mip - 1; mip >= 0; --mip)
        {
            AddUpdateMap(id.x, id.y, id.mip, mip, *pageEntry);
        }

        Span<MortonDFSNode> stack = stackalloc MortonDFSNode[64];
        int top = 0;
        stack[top++] = new MortonDFSNode
        {
            mip = id.mip - 1,
            morton = rootMorton << 2
        };
        while (top > 0)
        {
            MortonDFSNode node = stack[--top];

            if (node.mip < 0)
                continue;

            PageEntry* entry =
                _mipBasePages[node.mip] + node.morton;

            if (entry->mappingSlot == -1 ||
                    entry->activeMip != node.mip)
            {
                continue;
            }
            (int x, int y) = MathExtension.DecodeMorton((uint)node.morton);

            if(!IsValid(node.mip, x, y))
            {
                continue;
            }

            if(entry->activeMip == node.mip && entry->mappingSlot != -1)
            {
                for (int subPageMip = node.mip; subPageMip >= 0; --subPageMip)
                {
                    AddUpdateMap(x, y, node.mip, subPageMip, *entry);
                }
            }

            for (uint childBits = 0; childBits < 4; ++childBits)
            {
                stack[top++] = new MortonDFSNode
                {
                    mip = node.mip - 1,
                    morton = (node.morton << 2) | childBits
                };
            }
        }
        //Logger.Debug($"[VT]: map page : {id} {physicalSlot}");
    }

    private struct MortonDFSNode
    {
        public int mip;
        public uint morton;
    }
    public void RemapPage(VirtualPageID id, Action<VirtualPageID> removePageMethod)
    {
        Debug.Assert(IsValid(id.mip, id.x, id.y));
        int fallbackSlot = -1;
        int fallbackMip = -1;

        VirtualPageID ancestor = MapToAncestor(id, _persistentMip);
        uint persistentMorton = MathExtension.EncodeMorton(ancestor.x, ancestor.y);
        PageEntry* fallbackEntry = _mipBasePages[_persistentMip] + persistentMorton;
        Debug.Assert(fallbackEntry->mappingSlot != -1 && fallbackEntry->activeMip == _persistentMip);
        fallbackSlot = fallbackEntry->mappingSlot;
        fallbackMip = fallbackEntry->activeMip;

        uint rootMorton = MathExtension.EncodeMorton(id.x, id.y);

        PageEntry* pageEntry = _mipBasePages[id.mip] + rootMorton;
        pageEntry->mappingSlot = (short)fallbackSlot;
        pageEntry->activeMip = (ushort)fallbackMip;

        AddUpdateReMap(id.x, id.y, id.mip, id.mip, *pageEntry);

        //int mipOffset = 0;
        //int range = 1;
        //for (int mip = id.mip - 1; mip >= 0; --mip)
        //{
        //    mipOffset += 1;
        //    range *= 4;
        //    int subPageIdxBegin = (int)rootMorton << (mipOffset * 2);
        //    for (int subPageIdx = subPageIdxBegin; subPageIdx < subPageIdxBegin + range; ++subPageIdx)
        //    {
        //        (int x, int y) coord = MathExtension.DecodeMorton((uint)subPageIdx);
        //        if (!IsValid(mip, coord.x, coord.y))
        //        {
        //            continue;
        //        }

        //        PageEntry* subPageEntry = _mipBasePages[mip] + subPageIdx;
        //        if (subPageEntry->mappingSlot != -1 && subPageEntry->activeMip == mip) //当子页面已加载资源时需要将其区域再覆盖一次
        //        {
        //            //for (int subPageMip = mip; subPageMip >= 0; --subPageMip)
        //            //{
        //            //    AddUpdateReMap(coord.x, coord.y, mip, subPageMip, *subPageEntry);
        //            //}
        //            var subPageId = new VirtualPageID()
        //            {
        //                x = coord.x,
        //                y = coord.y,
        //                mip = mip,
        //            };
        //            subPageEntry->mappingSlot = (short)fallbackSlot;
        //            subPageEntry->activeMip = (ushort)fallbackMip;
        //            removePageMethod(subPageId);
        //            Logger.Debug($"[VT]: remove subpage : {subPageId}");
        //        }
        //    }
        //    AddUpdateReMap(id.x, id.y, id.mip, mip, *pageEntry);
        //}

        for (int mip = id.mip - 1; mip >= 0; --mip)
        {
            AddUpdateReMap(id.x, id.y, id.mip, mip, *pageEntry);
        }

        Span<MortonDFSNode> stack = stackalloc MortonDFSNode[64];
        int top = 0;
        stack[top++] = new MortonDFSNode
        {
            mip = id.mip - 1,
            morton = rootMorton << 2
        };
        while (top > 0)
        {
            MortonDFSNode node = stack[--top];

            if (node.mip < 0)
                continue;

            PageEntry* entry =
                _mipBasePages[node.mip] + node.morton;

            if (entry->mappingSlot == -1 ||
                    entry->activeMip != node.mip)
            {
                continue;
            }
            (int x, int y) = MathExtension.DecodeMorton((uint)node.morton);

            Debug.Assert(IsValid(node.mip, x, y));

            var entryID = new VirtualPageID()
            {
                mip = node.mip,
                x = x,
                y = y
            };
            entry->mappingSlot = (short)fallbackSlot;
            entry->activeMip = (ushort)fallbackMip;
            removePageMethod(entryID);

            for (uint childBits = 0; childBits < 4; ++childBits)
            {
                stack[top++] = new MortonDFSNode
                {
                    mip = node.mip - 1,
                    morton = (node.morton << 2) | childBits
                };
            }
        }
    }

    private void SubmitBatch(ref SortedSet<PageUpdateInfo>[] activeSets)
    {
        bool hasData = false;
        foreach (var set in activeSets)
        {
            if (set.Count > 0) { hasData = true; break; }
        }
        if (!hasData) return;

        var setsToSubmit = activeSets;
        activeSets = _setArrayPool.Rent();
        RenderingServer.CallOnRenderThread(Callable.From(() => //todo: use Callable.Bind() in the future.
        {
            Dispatch(setsToSubmit, _setArrayPool);
        }));
    }

    public void UpdateMap() => SubmitBatch(ref _activeMapSets);

    public void UpdateReMap() => SubmitBatch(ref _activeRemapSets);

    private void Dispatch(SortedSet<PageUpdateInfo>[] pageUpdateSetPerMip, SortedSetArrayPool pool)
    {
        var rd = RenderingServer.GetRenderingDevice();

        var list = rd.ComputeListBegin();
        rd.ComputeListBindComputePipeline(list, _pipeline);
        rd.ComputeListBindUniformSet(list, _descriptorSet, 0);

        for (int coveredMip = 0; coveredMip < pageUpdateSetPerMip.Length; coveredMip++)
        {
            SortedSet<PageUpdateInfo> pageUpdateInfos = pageUpdateSetPerMip[coveredMip];
            foreach (PageUpdateInfo info in pageUpdateInfos)
            {
                int scale = 1 << (info.mip - coveredMip);

                var origin = new Vector2I(
                  info.x * scale,
                  info.y * scale
                );

                var (w, h) = _mipRealBounds[coveredMip];

                var size = new Vector2I(
                  Math.Min(scale, w - origin.X),
                  Math.Min(scale, h - origin.Y)
                );

                if (size.X <= 0 || size.Y <= 0)
                    continue;

                var value = new Vector2I(
                  info.mappingSlot,
                  info.activeMip
                );

                var pushConstants = new ShaderPushConstants()
                {
                    origin = origin,
                    size = size,
                    mip = coveredMip,
                    value = value,
                };
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(
                    MemoryMarshal.CreateReadOnlySpan<ShaderPushConstants>(ref pushConstants, 1));

                rd.ComputeListSetPushConstant(list, bytes, (uint)bytes.Length);
                rd.ComputeListDispatch(list, (uint)Math.Ceiling(size.X / 8f), (uint)Math.Ceiling(size.Y / 8f), 1);
                rd.ComputeListAddBarrier(list);
            }
            pageUpdateInfos.Clear();
        }
        rd.ComputeListEnd();
        pool.Return(pageUpdateSetPerMip);
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
        for (int mip = 0; mip < IndirectTextures.Length; ++mip)
        {
            indirectTextureBinding.AddId(IndirectTextures[mip]);
        }
        // Fill the remaining slots.
        for (int mip = IndirectTextures.Length; mip < MaxPageTableMipInGpu; ++mip)
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
            foreach (var texture in IndirectTextures)
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
    private unsafe void AddUpdateMap(int x, int y, int mip, int coveredMip, PageEntry entry)
    {
        _activeMapSets[coveredMip].Add(new PageUpdateInfo()
        {
            x = x,
            y = y,
            mip = mip,
            mappingSlot = entry.mappingSlot,
            activeMip = entry.activeMip,
        });
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AddUpdateReMap(int x, int y, int mip, int coveredMip, PageEntry entry)
    {
        _activeRemapSets[coveredMip].Add(new PageUpdateInfo()
        {
            x = x,
            y = y,
            mip = mip,
            mappingSlot = entry.mappingSlot,
            activeMip = entry.activeMip,
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