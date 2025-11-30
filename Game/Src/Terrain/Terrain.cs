using Godot;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

[Tool]
public partial class Terrain : Node3D
{
    private TerrainProcessor _processor =  new();
    [Export]
    private Camera3D? _activeCamera;

    public Camera3D? ActiveCamera => _activeCamera;

    public TerrainData Data { get; private set; } = null!;

    public ShaderMaterial? Material { get; set; }

    public uint LeafNodeSize { get; set; } = 32;

    public uint PatchSize { get; set; } = 8;

    public float ViewPortWidth { get; private set; }

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
    }

    public override void _Process(double delta)
    {
        base._Process(delta);
        Debug.Assert(_activeCamera != null);
        if (!_processor.Inited)
            return;
        _processor.Process(_activeCamera);
    }

    public override void _ExitTree()
    {
        base._ExitTree();
        _planeMesh?.Dispose();
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            Data.Dispose();
            _processor.Dispose();
        }));
    }

    public void Init(MapDefinition definition)
    {
        Data.LoadMinMaxErrorMaps();
        InitMaterialParameters(definition);
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            Data.Load(definition);
            Debug.Assert(Data.Heightmap != null);
            _processor.Init(this, _planeMesh, definition);
        }));
    }

    private void InitMaterialParameters(MapDefinition definition)
    {
        Debug.Assert(Material != null);
        Material.SetShaderParameter("u_heightScale", definition.TerrainHeightScale);
        Material.SetShaderParameter("u_baseChunkSize", Constants.LeafNodeSize);
    }

    private void CreatePlaneMesh()
    {
        _planeMesh = TerrainMesh.Create(Constants.MaxNodeInSelect, (int)LeafNodeSize, LeafNodeSize, GetWorld3D());
        _planeMesh.Material = Material;
    }
}
