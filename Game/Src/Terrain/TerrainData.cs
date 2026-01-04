
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
    public VirtualTexture? GeometricVT { get; private set; } // height or normal

    public Texture2D? DebugGridTexture { get; private set; }

    public MinMaxErrorMap[]? MinMaxErrorMaps { get; private set; }

    public int GeometricWidth { get; private set; }
    public int GeometricHeight { get; private set; }
    public int HeightmapLodOffset { get; private set; }

    private Terrain _terrain;

    public TerrainData(Terrain terrain)
    {
        _terrain = terrain;
    }

    /// <summary>
    /// Notice: Must be called On rendering thread.
    /// </summary>
    public void Load()
    {
        LoadMinMaxErrorMaps();
        LoadHeightMapVT();
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            LoadTextures();
        }));
    }
    
    public void Dispose()
    {
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            GeometricVT?.Dispose();
        }));
    }

    private void LoadTextures()
    {
        DebugGridTexture = GD.Load<Texture2D>("res://EditorAssets/Textures/Grid_Gray_128x128.png");
        _terrain.SetMaterialParameter("u_debugGridTexture", DebugGridTexture);
    }

    private void LoadMinMaxErrorMaps()
    {
        string? file = VirtualFileSystem.Instance.ResolvePath("Map/heightmap.height");
        if (!File.Exists(file))
        {
            Logger.Error($"Can't find heightmap.height file at {file}.");
            return;
        }
        MinMaxErrorMaps = MinMaxErrorMap.LoadAll(file);
    }

    private void LoadHeightMapVT()
    {
        VirtualTextureDesc[] descs =
{
            new VirtualTextureDesc()
            {
                format = RenderingDevice.DataFormat.R16Unorm,
                filePath = VirtualFileSystem.Instance.ResolvePath("Map/heightmap.svt")!
            }
        };
        GeometricVT = new VirtualTexture(Terrain.MaxVTPageCount, descs);
        GeometricWidth = GeometricVT.Width;
        GeometricHeight = GeometricVT.Height;
        CalcHeightmapLodOffsetToMip(GeometricVT.TileSize, (int)_terrain.LeafNodeSize);
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