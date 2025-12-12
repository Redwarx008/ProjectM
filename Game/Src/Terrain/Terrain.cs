using Core;
using Godot;
using ProjectM;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

[Tool]
public partial class Terrain : Node3D
{
    private TerrainProcessor? _processor;
    [Export]
    private Camera3D? _activeCamera;

    public Camera3D? ActiveCamera => _activeCamera;

    public TerrainData Data { get; private set; } = null!;

    public ShaderMaterial? Material { get; set; }

    public VirtualTexture? GeometricVT { get; private set; } // height or normal

    public uint LeafNodeSize { get; set; } = 32;

    public static readonly int MaxLodCount = 10;

    public static readonly uint MaxVTPageCount = 200;

    public float ViewPortWidth { get; private set; }

    public int HeightmapLodOffset { get; private set; }

    private TerrainMesh? _planeMesh;

    public override void _Notification(int what)
    {
        base._Notification(what);
        if (what == NotificationVisibilityChanged)
        {
            if (_planeMesh != null)
            {
                _planeMesh.Visible = Visible;
            }
        }
    }

    public override void _Ready()
    {
        if (Engine.IsEditorHint())
        {
            _activeCamera = EditorInterface.Singleton.GetEditorViewport3D(0).GetCamera3D();
        }
        if (_activeCamera == null)
        {
            _activeCamera = GetViewport().GetCamera3D();
        }
        ViewPortWidth = ActiveCamera!.GetViewport().GetVisibleRect().Size.X;
        Material = new ShaderMaterial()
        {
            Shader = GD.Load<Shader>("res://Shaders/Terrain.gdshader")
        };
        Data = new TerrainData(this);
        CreatePlaneMesh();
        InitVirtualTexture();
        Debug.Assert(GeometricVT != null);
        CalcHeightmapLodOffsetToMip(GeometricVT.TileSize, (int)LeafNodeSize);
    }

    public override void _Process(double delta)
    {
        base._Process(delta);
        Debug.Assert(_activeCamera != null);
        if (_processor == null || !_processor.Inited)
            return;
        _processor.Process(_activeCamera);
    }

    public override void _ExitTree()
    {
        base._ExitTree();
        _planeMesh?.Dispose();
        _processor?.Dispose();
        GeometricVT?.Dispose();
        Data.Dispose();
    }

    public void Init(MapDefinition definition)
    {
        Data.LoadMinMaxErrorMaps();
        InitMaterialParameters(definition);
        Data.Load(definition);
        _processor = new TerrainProcessor(this, _planeMesh, definition);
    }

    private void InitVirtualTexture()
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
    }

    private void InitMaterialParameters(MapDefinition definition)
    {
        Debug.Assert(Material != null && GeometricVT != null);
        Material.SetShaderParameter("u_heightScale", definition.TerrainHeightScale);
        Material.SetShaderParameter("u_baseChunkSize", LeafNodeSize);
        Material.SetShaderParameter("u_PagePadding", GeometricVT.Padding);
        Material.SetShaderParameter("u_VTPhysicalHeightmap", GeometricVT.GetPhysicalTexture(0).ToTexture2DArrayRD());
        Texture2Drd[] pageTable = new Texture2Drd[VirtualTexture.MaxPageTableMipInGpu];
        for (int i = 0; i < GeometricVT.MipCount; ++i)
        {
            pageTable[i] = GeometricVT.GetPageTableInMipLevel(i);
        }
        for(int i = GeometricVT.MipCount; i < VirtualTexture.MaxPageTableMipInGpu; ++i)
        {
            pageTable[i] = GeometricVT.GetPageTableInMipLevel(GeometricVT.MipCount - 1);
        }
        Material.SetShaderParameter("u_VTPageTable", pageTable);
        Material.SetShaderParameter("u_HeightmapLodOffset", HeightmapLodOffset);
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

    private void CreatePlaneMesh()
    {
        _planeMesh = TerrainMesh.Create(Constants.MaxNodeInSelect, (int)LeafNodeSize, LeafNodeSize, GetWorld3D());
        _planeMesh.Material = Material;
    }
}
