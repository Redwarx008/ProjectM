using System;
using Godot;

public struct GDTexture2DDesc()
{
    public RenderingDevice.DataFormat Format { get; set; }
    public uint Width { get; set; }
    public uint Height { get; set; }
    public uint Mipmaps { get; set; } = 1;
    public RenderingDevice.TextureUsageBits UsageBits { get; set; }
}

public class GDTexture2D : IDisposable
{
    public Rid Rid { get; private init; }
    public uint Width { get; private set; }
    public uint Height { get; private set; }
    public uint Mipmaps { get; private set; }

    // Not all types can be sampled, so only fill this field if necessary.
    private Texture2Drd? _texture2D;

    private GDTexture2D()
    {
    }

    /// <summary>
    /// Should be called on render thread.
    /// </summary>
    public static GDTexture2D Create(in GDTexture2DDesc desc, ReadOnlySpan<byte> data)
    {
        RenderingDevice rd = RenderingServer.GetRenderingDevice();
        var format = new RDTextureFormat()
        {
            Format = desc.Format,
            Width = desc.Width,
            Height = desc.Height,
            Mipmaps = desc.Mipmaps,
            UsageBits = desc.UsageBits,
            TextureType = RenderingDevice.TextureType.Type2D,
            Depth = 1,
            ArrayLayers = 1
        };
        var texture = new GDTexture2D()
        {
            Rid = rd.TextureCreate(format, new RDTextureView(), [data.ToArray()]),
            Width = format.Width,
            Height = format.Height,
            Mipmaps = format.Mipmaps,
        };
        return texture;
    }

    public static GDTexture2D Create(in GDTexture2DDesc desc)
    {
        RenderingDevice rd = RenderingServer.GetRenderingDevice();
        var format = new RDTextureFormat()
        {
            Format = desc.Format,
            Width = desc.Width,
            Height = desc.Height,
            Mipmaps = desc.Mipmaps,
            UsageBits = desc.UsageBits,
            TextureType = RenderingDevice.TextureType.Type2D,
            Depth = 1,
            ArrayLayers = 1
        };
        var texture = new GDTexture2D()
        {
            Rid = rd.TextureCreate(format, new RDTextureView()),
            Width = format.Width,
            Height = format.Height,
            Mipmaps = format.Mipmaps,
        };
        return texture;
    }

    ~GDTexture2D()
    {
        Dispose();
    }

    public void Dispose()
    {
        if (_texture2D != null)
        {
            _texture2D.TextureRdRid = new Rid();
        }
        RenderingDevice rd = RenderingServer.GetRenderingDevice();
        rd.FreeRid(Rid);
        GC.SuppressFinalize(this);
    }

    public static implicit operator Texture2Drd(GDTexture2D texture)
    {
        if (texture._texture2D == null)
        {
            texture._texture2D = new Texture2Drd()
            {
                TextureRdRid = texture.Rid
            };
        }

        return texture._texture2D;
    }

    public Texture2Drd ToTexture2d()
    {
        if (_texture2D == null)
        {
            _texture2D = new Texture2Drd()
            {
                TextureRdRid = Rid
            };
        }

        return _texture2D;
    }
}