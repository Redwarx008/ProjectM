using Core;
using Godot;
using ProjectM;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using static System.Runtime.InteropServices.JavaScript.JSType;
using Logger = Core.Logger;

public class TerrainData : IDisposable
{
    public VirtualTexture? MapVT { get; private set; } // height or normal

    public Texture2D? DebugGridTexture { get; private set; }

    public GDTexture2D[]? MinMaxMaps { get; private set; }

    internal QuadTree? StreamingQuadTree { get; private set; }

    public int HeightmapDimX { get; private set; }
    public int HeightmapDimY { get; private set; }
    public int HeightmapLodOffset { get; private set; }
    public int LodCount
    {
        get
        {
            if (MinMaxMaps != null)
            {
                return MinMaxMaps.Length;
            }
            return 0;
        }
    }

    public int RenderPatchSize { get; private set; }

    private Terrain _terrain;

    public TerrainData(Terrain terrain)
    {
        _terrain = terrain;
    }

    /// <summary>
    /// Notice: Must be called On rendering thread.
    /// </summary>
    public void Load(TerrainConfig config)
    {
        LoadHeightMapVT(config.heightmapPath);
        MinMaxMap[] data = LoadMinMaxMapsData(config.minmaxmapPath);
        CalcHeightmapLodOffsetToMip(MapVT!.TileSize, RenderPatchSize);
        StreamingQuadTree = new QuadTree(data[HeightmapLodOffset..], MapVT!.TileSize, 
            HeightmapDimX, HeightmapDimY, HeightmapLodOffset);
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            LoadTextures();
            CreateMinMaxMapTexture(data);
        }));
    }

    public void Dispose()
    {
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            MapVT?.Dispose();
        }));
    }

    private void LoadTextures()
    {
        DebugGridTexture = GD.Load<Texture2D>("res://EditorAssets/Textures/Grid_Gray_128x128.png");
        _terrain.Material.SetShaderParameter("u_DebugGridTexture", DebugGridTexture);
    }

    private void CreateMinMaxMapTexture(MinMaxMap[] minMaxMaps)
    {
        Debug.Assert(MinMaxMaps != null);
        RenderingDevice rd = RenderingServer.GetRenderingDevice();
        for (int i = 0; i < MinMaxMaps.Length; ++i)
        {
            var desc = new GDTextureDesc()
            {
                Format = RenderingDevice.DataFormat.R16G16Unorm,
                Width = (uint)minMaxMaps[i].Width,
                Height = (uint)minMaxMaps[i].Height,
                Mipmaps = 1,
                UsageBits = RenderingDevice.TextureUsageBits.SamplingBit | RenderingDevice.TextureUsageBits.CanUpdateBit
                | RenderingDevice.TextureUsageBits.StorageBit
            };
            MinMaxMaps[i] = GDTexture2D.Create(desc);
            ReadOnlySpan<byte> data = MemoryMarshal.AsBytes(minMaxMaps[i].Data.AsSpan());
            rd.TextureUpdate(MinMaxMaps[i], 0, data);
        }
    }

    private MinMaxMap[] LoadMinMaxMapsData(string? file)
    {
        if (!File.Exists(file))
        {
            Logger.Error($"Can't find heightmap.height file at {file}.");
            throw new FileNotFoundException($"Can't find heightmap.bounds file at {file}.");
        }
        MinMaxMap[] minMaxMapsData = MinMaxMap.LoadAll(file, out int leafNodeSize);
        Debug.Assert(leafNodeSize * minMaxMapsData[0].Width == HeightmapDimX && leafNodeSize * minMaxMapsData[0].Height == HeightmapDimY);
        RenderPatchSize = leafNodeSize;
        MinMaxMaps = new GDTexture2D[minMaxMapsData.Length];
        return minMaxMapsData;
    }

    private void LoadHeightMapVT(string? file)
    {
        if (!File.Exists(file))
        {
            Logger.Error($"Can't find heightmap.svt file at {file}.");
            throw new FileNotFoundException($"Can't find heightmap.svt file at {file}.");
        }
        VirtualTextureDesc[] descs =
        {
            new VirtualTextureDesc()
            {
                format = RenderingDevice.DataFormat.R16Unorm,
                filePath = file
            }
        };
        MapVT = new VirtualTexture(Terrain.MaxVTPageCount, descs);
        HeightmapDimX = MapVT.Width;
        HeightmapDimY = MapVT.Height;
    }

    private void CalcHeightmapLodOffsetToMip(int pageSize, int leafNodeSize)
    {
        //int lodOffsetToMip = 0;
        //while (leafNodeSize < pageSize)
        //{
        //    leafNodeSize *= 2;
        //    ++lodOffsetToMip;
        //}
        //return lodOffsetToMip;

        int offset = System.Numerics.BitOperations.Log2((uint)pageSize) -
                     System.Numerics.BitOperations.Log2((uint)leafNodeSize);

        HeightmapLodOffset = Math.Max(0, offset);
    }
}