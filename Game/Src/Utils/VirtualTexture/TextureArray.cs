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

internal class TextureArray : IDisposable
{
    public GDTexture2DArray Texture => _texture;

    private GDTexture2DArray _texture = null!;

    private List<(int slot, IMemoryOwner<byte> data)> _pendingUpdateEntries = [];

    public TextureArray(int maxPageCount, int pageSize, RenderingDevice.DataFormat format)
    {
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            var desc = new GDTextureDesc()
            {
                Format = format,
                Width = (uint)(pageSize),
                Height = (uint)(pageSize),
                LayerCount = (uint)maxPageCount,
                Mipmaps = 1,
                UsageBits = RenderingDevice.TextureUsageBits.SamplingBit | RenderingDevice.TextureUsageBits.CanUpdateBit |
                RenderingDevice.TextureUsageBits.CanCopyToBit | RenderingDevice.TextureUsageBits.CanCopyFromBit,
            };
            _texture = GDTexture2DArray.Create(desc);
        }));
    }


    public void Update()
    {
        foreach (var entry in _pendingUpdateEntries)
        {
            Upload(entry.slot, entry.data);
        }
        _pendingUpdateEntries.Clear();
    }

    public void AddUpdate(int slot, IMemoryOwner<byte> data)
    {
        _pendingUpdateEntries.Add((slot, data));
    }

    public void Upload(int slot, IMemoryOwner<byte> data)
    {
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            var rd = RenderingServer.GetRenderingDevice();
            using (data)
            {
                rd.TextureUpdate(_texture.Rid, (uint)slot, data.Memory.Span);
            } 
        }));
    }

    public void Dispose()
    {
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            _texture.Dispose();
        }));
    }
}
