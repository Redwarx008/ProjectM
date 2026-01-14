using Core;
using Godot;
using ProjectM;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[Tool]
public partial class Terrain : Node3D
{
    private TerrainProcessor? _processor;
    [Export]
    private Camera3D? _activeCamera;

    public Camera3D? ActiveCamera => _activeCamera;

    public TerrainData Data { get; private set; } = null!;

    public uint LeafNodeSize { get; set; } = 32;

    public static readonly int MaxLodCount = 10;

    public static readonly uint MaxVTPageCount = 400;

    [Export]
    public float TolerableError { get; set; } = 9f;

    [Export]
    public float HeightScale
    {
        get => _heightScale;
        set
        {
            if (_heightScale != value)
            {
                if (_planeMesh != null)
                {
                    SetMaterialParameter("u_heightScale", value);
                }
                _heightScale = value;
            }
        }
    }

    private float _heightScale = 200f;

    public float ViewPortWidth { get; private set; }


    private TerrainMesh? _planeMesh;

    private TerrainMesh? _skirtMesh;

    private ShaderMaterial? _planeMaterial;

    private ShaderMaterial? _skirtMaterial;

    public override void _Notification(int what)
    {
        base._Notification(what);
        if (what == NotificationVisibilityChanged)
        {
            if (_planeMesh != null)
            {
                _planeMesh.Visible = Visible;
            }
            if (_skirtMesh != null)
            {
                _skirtMesh.Visible = Visible;
            }
        }
    }

    public override void _Ready()
    {
#if !EXPORTRELEASE
        if (Engine.IsEditorHint())
        {
            _activeCamera = EditorInterface.Singleton.GetEditorViewport3D(0).GetCamera3D();
        }
#endif
        _activeCamera ??= GetViewport().GetCamera3D();
        ViewPortWidth = ActiveCamera!.GetViewport().GetVisibleRect().Size.X;
        _planeMaterial = new ShaderMaterial()
        {
            Shader = GD.Load<Shader>("res://Shaders/Terrain.gdshader")
        };
        _skirtMaterial = new ShaderMaterial()
        {
            Shader = GD.Load<Shader>("res://Shaders/TerrainSkirt.gdshader")
        };
        Data = new TerrainData(this);
        CreateMesh();
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
        _skirtMesh?.Dispose();
        _processor?.Dispose();
        Data.Dispose();
    }

    public void Load()
    {
        Data.Load();
        InitMaterialParameters();
        _processor = new TerrainProcessor(this, _planeMesh, _skirtMesh);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetMaterialParameter(StringName param, Variant value)
    {
        Debug.Assert(_planeMaterial != null);
        Debug.Assert(_skirtMaterial != null);
        _planeMaterial.SetShaderParameter(param, value);
        _skirtMaterial.SetShaderParameter(param, value);
    }

    private void InitMaterialParameters()
    {
        Debug.Assert(Data.GeometricVT != null);
        GDTexture2D? pageTable = Data.GeometricVT.GetIndirectTexture();
        Debug.Assert(pageTable != null);
        SetMaterialParameter("u_heightScale", HeightScale);
        SetMaterialParameter("u_baseChunkSize", LeafNodeSize);
        SetMaterialParameter("u_PagePadding", Data.GeometricVT.Padding);
        SetMaterialParameter("u_VTPhysicalHeightmap", Data.GeometricVT.GetPhysicalTexture(0).ToTexture2DArrayRD());
        SetMaterialParameter("u_VTPageTable", pageTable.ToTexture2d());
        SetMaterialParameter("u_HeightmapLodOffset", Data.HeightmapLodOffset);
        SetMaterialParameter("u_heightmapSize",new Vector2I(Data.GeometricVT.Width, Data.GeometricVT.Height));
        SetMaterialParameter("u_VTPageTableSize", Data.GeometricVT.GetIndirectTextureSize());
    }

    private void CreateMesh()
    {
        _planeMesh = TerrainMesh.CreatePlane(Constants.MaxNodeInSelect, (int)LeafNodeSize, LeafNodeSize, GetWorld3D());
        _planeMesh.Material = _planeMaterial;

        _skirtMesh = TerrainMesh.CreateSkirt(Constants.MaxNodeInSelect, (int)LeafNodeSize, LeafNodeSize, GetWorld3D());
        _skirtMesh.Material = _skirtMaterial;
    }
}
