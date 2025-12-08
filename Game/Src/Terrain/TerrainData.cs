
using Core;
using Godot;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using static System.Runtime.InteropServices.JavaScript.JSType;
using Logger = Core.Logger;

public class TerrainData : IDisposable
{
    public GDTexture2D? Heightmap { get; private set; }
    
    public Texture2D? DebugGridTexture { get; private set; }

    public MinMaxErrorMap[]? MinMaxErrorMaps { get; private set; }

    public int GeometricWidth { get; private set; }
    public int GeometricHeight { get; private set; }

    private ShaderMaterial? _material;

    public TerrainData(Terrain terrain)
    {
        _material = terrain.Material;
    }

    /// <summary>
    /// Notice: Must be called On rendering thread.
    /// </summary>
    public void Load(MapDefinition definition)
    {
        LoadHeightmap(definition, out int width, out int height, out byte[]? data);
        GeometricWidth = width;
        GeometricHeight = height;
        Debug.Assert(data != null);
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            CreateHeightmapTexture(width, height, data);
            LoadTextures(definition);
        }));
    }
    
    public void Dispose()
    {
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            Heightmap?.Dispose();
        }));
    }

    private void LoadTextures(MapDefinition definition)
    {
        Debug.Assert(_material != null);
        DebugGridTexture = GD.Load<Texture2D>("res://EditorAssets/Textures/Grid_Gray_128x128.png");
        _material.SetShaderParameter("u_debugGridTexture", DebugGridTexture);
    }
    private void LoadHeightmap(MapDefinition definition, out int width, out int height, out byte[]? data)
    {
        string? heightmapFile = VirtualFileSystem.Instance.ResolvePath("Map/heightmap.png");
        if (!File.Exists(heightmapFile))
        {
            Logger.Error($"Can't find heightmap file at {heightmapFile}.");
            width = 0;
            height = 0;
            data = null;
            return;
        }

        using var stream = File.OpenRead(heightmapFile);

        int _width;
        int _height;
        var stbiContext = new StbImageSharp.StbImage.stbi__context(stream);
        ReadOnlySpan<byte> _data;
        unsafe
        {
            int channels;
            ushort* buffer = StbImageSharp.StbImage.stbi__load_and_postprocess_16bit(stbiContext, &_width, &_height, &channels, 1);
            ReadOnlySpan<ushort> bufferSpan = new Span<ushort>(buffer, _width * _height * channels);
            _data = MemoryMarshal.AsBytes(bufferSpan);
        }
        width = _width; height = _height; data = _data.ToArray();
    }

    private void CreateHeightmapTexture(int width, int height, byte[] data)
    {
        var heightmapFormat = new GDTextureDesc()
        {
            Format = RenderingDevice.DataFormat.R16Unorm,
            Width = (uint)width,
            Height = (uint)height,
            Mipmaps = 1,
            UsageBits = RenderingDevice.TextureUsageBits.CanUpdateBit |
                RenderingDevice.TextureUsageBits.SamplingBit | RenderingDevice.TextureUsageBits.CanCopyFromBit
        };
        Heightmap = GDTexture2D.Create(heightmapFormat, data);

        _material?.SetShaderParameter("u_heightmap", Heightmap.ToTexture2d());
        _material?.SetShaderParameter("u_heightmapSize",
            new Vector2I((int)Heightmap.Width, (int)Heightmap.Height));
    }

    public void LoadMinMaxErrorMaps()
    {
        string? file = VirtualFileSystem.Instance.ResolvePath("Map/heightmap.height");
        if (!File.Exists(file))
        {
            Logger.Error($"Can't find heightmap.height file at {file}.");
            return;
        }
        MinMaxErrorMaps = MinMaxErrorMap.LoadAll(file);
    }
}