using Godot;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static ProjectM.VirtualTexture;

namespace ProjectM;

internal class PhysicalTexture : IDisposable
{
    private GDTexture2DArray[] _textures;

    private List<int> _freeSlots;

    private int _reservedCount;

    public GDTexture2DArray this[int i] => _textures[i];

    public PhysicalTexture(uint maxPageCount, int reservedSlotCount, VTInfo vTInfo, VirtualTextureDesc[] vtDescs)
    {
        int vTCount = vtDescs.Length;
        int pageSize = vTInfo.tileSize;
        int dynamicCapacity = (int)(maxPageCount - reservedSlotCount);
        if (dynamicCapacity <= 0)
            throw new ArgumentException("Total capacity must be greater than reserved count.");
        _freeSlots = new List<int>(dynamicCapacity);
        for(int i = 0; i < dynamicCapacity; ++i)
        {
            _freeSlots.Add(reservedSlotCount + i);
        }
        _reservedCount = reservedSlotCount;
        _textures = new GDTexture2DArray[vTCount];
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            for (int i = 0; i < vTCount; ++i)
            {
                var desc = new GDTextureDesc()
                {
                    Format = vtDescs[i].format,
                    Width = (uint)(pageSize + 2 * vTInfo.padding),
                    Height = (uint)(pageSize + 2 * vTInfo.padding),
                    LayerCount = maxPageCount,
                    Mipmaps = 1,
                    UsageBits = RenderingDevice.TextureUsageBits.SamplingBit | RenderingDevice.TextureUsageBits.CanUpdateBit |
                    RenderingDevice.TextureUsageBits.CanCopyToBit | RenderingDevice.TextureUsageBits.CanCopyFromBit,
                };
                _textures[i] = GDTexture2DArray.Create(desc);
            }
        }));
    }

    public bool TryAllocateDynamic(out int slot)
    {
        if (_freeSlots.Count > 0)
        {
            slot = _freeSlots[_freeSlots.Count - 1];
            _freeSlots.RemoveAt(_freeSlots.Count - 1);
            return true;
        }
        slot = -1;
        return false;
    }

    public void FreeDynamic(int slot)
    {
        if (slot < _reservedCount)
            throw new InvalidOperationException($"Cannot free reserved slot {slot}");
        Debug.Assert(!_freeSlots.Contains(slot));
        _freeSlots.Add(slot);
    }

    public void Upload(int texture, int slot, IMemoryOwner<byte> data)
    {
        Debug.Assert(texture >= 0 && texture < _textures.Length);
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            var rd = RenderingServer.GetRenderingDevice();
            using (data)
            {
                rd.TextureUpdate(_textures[texture].Rid, (uint)slot, data.Memory.Span);
            } 
        }));
    }

    public void Dispose()
    {
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            foreach (var t in _textures)
            {
                t.Dispose();
            }
        }));
    }
}
