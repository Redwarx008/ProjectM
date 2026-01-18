using Core;
using Godot;
using ProjectM;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Logger = Core.Logger;


public struct TerrainConfig()
{
    public string? heightmapPath;
    public string? splatmapPath; 
    public string? minmaxmapPath;
    public uint patchSize = 16;
    public float width = 0f;
    public float length = 0f;
    public float height = 200f;
    public float morphRange = 0.2f;
    public float subdivisionDistanceFactor = 2.0f;
}

[Tool]
public partial class Terrain : Node3D
{
    public static readonly int MaxLodCount = 10;

    public static readonly uint MaxVTPageCount = 400;

    private TerrainProcessor? _processor;
    private TerrainMesh? _mesh;

    [Export]
    private Camera3D? _activeCamera;

    public Camera3D? ActiveCamera => _activeCamera;

    public ShaderMaterial Material { get; private set; } = null!;

    public TerrainData Data { get; private set; } = null!;


    #region Debug Parameters

    [Export]
    public uint PatchSize
    {
        get => _patchSize;
        set
        {
            if (_patchSize != value)
            {
                if (_mesh != null)
                {
                    Material.SetShaderParameter("u_GridDimension", value);
                }
                _patchSize = value;
            }
        }
    }

    private uint _patchSize;

    [Export]
    public float Width
    {
        get => _width;
        set
        {
            if (_width != value)
            {
                if (_mesh != null)
                {
                    Material.SetShaderParameter("u_MapSize", new Vector2(value, Length));
                }
                _width = value;
            }
        }
    }
    private float _width;

    [Export]
    public float Length
    {
        get => _length;
        set
        {
            if (_length != value)
            {
                if (_mesh != null)
                {
                    Material.SetShaderParameter("u_MapSize", new Vector2(Width, value));
                }
                _length = value;
            }
        }
    }
    private float _length;

    [Export]
    public float Height
    {
        get => _height;
        set
        {
            if (_height != value)
            {
                if (_mesh != null)
                {
                    Material.SetShaderParameter("u_Height", value);
                }
                _height = value;
            }
        }
    }

    private float _height;

    [Export]
    public float MorphRange
    {
        get=> _morphRange;
        set
        {
            if (_morphRange != value)
            {
                if (_mesh != null)
                {
                    Material.SetShaderParameter("u_MorphRange", value);
                }
                _morphRange = value;
            }
        }
    }

    private float _morphRange;

    #endregion

    public float ViewRange { get; set; } = 1000f;


    public override void _Notification(int what)
    {
        base._Notification(what);
        if (what == NotificationVisibilityChanged)
        {
            if (_mesh != null)
            {
                _mesh.Visible = Visible;
            }
        }

        if(what == NotificationLocalTransformChanged)
        {
            Logger.Info("Position changed");
            Vector3 offset = Position;
            Material.SetShaderParameter("u_MapOffset", new Vector2(offset.X, offset.Z));
            Material.SetShaderParameter("u_HeightOffset", offset.Y);
        }
    }

    public override void _Ready()
    {
        SetNotifyLocalTransform(true);
#if !EXPORTRELEASE
        if (Engine.IsEditorHint())
        {
            _activeCamera = EditorInterface.Singleton.GetEditorViewport3D(0).GetCamera3D();
        }
#endif
        _activeCamera ??= GetViewport().GetCamera3D();
        Material = new ShaderMaterial()
        {
            Shader = GD.Load<Shader>("res://Shaders/Terrain.gdshader")
        };

        Data = new TerrainData(this);
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
        _mesh?.Dispose();
        _processor?.Dispose();
        Data.Dispose();
    }

    public void LoadConfig(TerrainConfig config)
    {
        PatchSize = config.patchSize;
        Width = config.width;
        Length = config.length;
        Height = config.height;
        MorphRange = config.morphRange;
        ViewRange = Math.Max(Width, Length) * 2;
        Data.Load(config);
        CreateMesh();
        InitMaterialParameters();
        _processor = new TerrainProcessor(this, _mesh);
    }

    private void InitMaterialParameters()
    {
        Debug.Assert(Data.GeometricVT != null);
        GDTexture2D? pageTable = Data.GeometricVT.GetIndirectTexture();
        Debug.Assert(pageTable != null);
        Material.SetShaderParameter("u_MapSize", new Vector2(Width, Length));
        Vector3 offset = Position;
        Material.SetShaderParameter("u_MapOffset", new Vector2(offset.X, offset.Z));
        Material.SetShaderParameter("u_HeightOffset", offset.Y);
        Material.SetShaderParameter("u_Height", Height);
        Material.SetShaderParameter("u_GridDimension", PatchSize);
        Material.SetShaderParameter("u_MorphRange", MorphRange);
        Material.SetShaderParameter("u_LodCount", Data.LodCount);
        Material.SetShaderParameter("u_PagePadding", Data.GeometricVT.Padding);
        Material.SetShaderParameter("u_VTPhysicalHeightmap", Data.GeometricVT.GetPhysicalTexture(0).ToTexture2DArrayRD());
        Material.SetShaderParameter("u_VTPageTable", pageTable.ToTexture2d());
        Material.SetShaderParameter("u_HeightmapLodOffset", Data.HeightmapLodOffset);
        Material.SetShaderParameter("u_HeightmapSize",new Vector2I(Data.GeometricVT.Width, Data.GeometricVT.Height));
        Material.SetShaderParameter("u_VTPageTableSize", Data.GeometricVT.GetIndirectTextureSize());
    }

    private void CreateMesh()
    {
        _mesh = TerrainMesh.CreatePlane(TerrainProcessor.MaxNodeInSelect, (int)PatchSize, PatchSize, GetWorld3D());
        _mesh.Material = Material;
    }
}
