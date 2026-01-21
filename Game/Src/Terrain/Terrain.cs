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
}

[Tool]
public partial class Terrain : Node3D
{
    public static readonly int MaxLodCount = 12;

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
                    Material.SetShaderParameter("u_MapSize", new Vector2(Length, value));
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
                    Material.SetShaderParameter("u_MapSize", new Vector2(value, Width));
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

    public Vector3 Offset
    {
        get => _offset;
        set
        {
            if (_offset != value)
            {
                _offset = value;
                if (_mesh != null)
                {
                    Material.SetShaderParameter("u_MapOffset", new Vector2(_offset.X, _offset.Z));
                    Material.SetShaderParameter("u_HeightOffset", _offset.Y);
                }
            }
        }
    }
    private Vector3 _offset;

    private float _morphRange;

    #endregion

    public float ViewRange { get; set; } = 1000f;
    public float[] LodRanges { get; private set; } = new float[MaxLodCount];

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

        if (what == NotificationLocalTransformChanged)
        {
            Logger.Info("Position changed");
            Offset = Position;
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
        _morphRange = config.morphRange;
        Data.Load(config);
        if (Width == 0)
        {
            Width = Data.HeightmapDimY;
        }
        if (Length == 0)
        {
            Length = Data.HeightmapDimX;
        }
        ViewRange = Math.Max(Width, Length) * 2;
        UpdateViewRange();
        CreateMesh();
        InitMaterialParameters();
        _processor = new TerrainProcessor(this, _mesh);
    }

    private void InitMaterialParameters()
    {
        Debug.Assert(Data.MapVT != null);
        GDTexture2D? pageTable = Data.MapVT.GetIndirectTexture();
        Debug.Assert(pageTable != null);
        Material.SetShaderParameter("u_MapScale", new Vector4(Length / Data.HeightmapDimX, Height, Width / Data.HeightmapDimY, 1));
        Vector3 offset = Position;
        Material.SetShaderParameter("u_MapOffset", new Vector4(offset.X, offset.Y, offset.Z, 1));
        Material.SetShaderParameter("u_GridDimension", PatchSize);
        Span<Vector2> morphConsts = stackalloc Vector2[MaxLodCount];
        CalculateMorphConsts(morphConsts);
        Material.SetShaderParameter("u_MorphConsts", morphConsts);
        Material.SetShaderParameter("u_LodCount", Data.LodCount);
        Material.SetShaderParameter("u_PagePadding", Data.MapVT.Padding);
        Material.SetShaderParameter("u_VTPhysicalHeightmap", Data.MapVT.GetPhysicalTexture(0).ToTexture2DArrayRD());
        Material.SetShaderParameter("u_VTPageTable", pageTable.ToTexture2d());
        Material.SetShaderParameter("u_HeightmapLodOffset", Data.HeightmapLodOffset);
        Material.SetShaderParameter("u_HeightmapSize", new Vector2I(Data.MapVT.Width, Data.MapVT.Height));
        Material.SetShaderParameter("u_VTPageTableSize", Data.MapVT.GetIndirectTextureSize());
    }

    private void CreateMesh()
    {
        _mesh = TerrainMesh.CreatePlane(TerrainProcessor.MaxNodeInSelect, (int)PatchSize, PatchSize, GetWorld3D());
        _mesh.Material = Material;
    }

    private void UpdateViewRange()
    {
        //Initialize visible range per level.
        float lodNear = 0;
        float lodFar = ViewRange;
        float detailBalance = 2;
        int layerCount = Data.LodCount;
        float currentDetailBalance = 1f;

        Span<float> lODVisRangeDistRatios = stackalloc float[layerCount];
        for (int i = 0; i < layerCount - 1; i++)
        {
            lODVisRangeDistRatios[i] = currentDetailBalance;
            if (i == 0)
            {
                lODVisRangeDistRatios[i] = 0.9f;
            }
            currentDetailBalance *= detailBalance;
        }

        lODVisRangeDistRatios[layerCount - 1] = currentDetailBalance;
        for (int i = 0; i < layerCount; i++)
        {
            lODVisRangeDistRatios[i] /= currentDetailBalance;
        }

        for (int i = 0; i < layerCount; i++)
        {
            LodRanges[i] = lodNear + lODVisRangeDistRatios[i] * (lodFar - lodNear);
        }
    }

    private void CalculateMorphConsts(Span<Vector2> morphConsts)
    {
        float morphStartRatio = Math.Clamp(1.0f - _morphRange, 0, 1.0f);
        float prevPos = 0;
        for (int i = 0; i < morphConsts.Length; ++i)
        {
            float morphEnd = LodRanges[i];
            float morphStart = prevPos + (morphEnd - prevPos) * morphStartRatio;
            morphConsts[i].X = morphEnd / (morphEnd - morphStart);
            morphConsts[i].Y = 1.0f / (morphEnd - morphStart);
            prevPos = morphStart;
        }
    }
}
