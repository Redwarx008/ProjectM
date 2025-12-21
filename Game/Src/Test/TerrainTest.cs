using Godot;
using System;
using System.Diagnostics;
using Logger = Core.Logger;
using Core;

[Tool]
public partial class TerrainTest : Node3D
{

    [Export]
    private Label? _fpsLabel;

    [Export]
    private Label? _cameraPosLabel;

    [Export]
    private Terrain? _terrain;

    private Camera3D? _camera;

    private MapDefinition _map;
    public async override void _Ready()
    {
        base._Ready();
        _map = await MapDefinition.LoadFromJsonFileAsync(VirtualFileSystem.Instance.ResolvePath("Map/descriptor.json"));
        if (_terrain != null)
        {
            _terrain.Init(_map);
            _camera = _terrain.ActiveCamera;
            Logger.Info("terrain init");
        }
    }
    public override void _Process(double delta)
    {
        base._Process(delta);
        Debug.Assert(_fpsLabel != null);
        _fpsLabel.Text = $" FPS: {Godot.Engine.GetFramesPerSecond()}";
        if(_camera != null)
        {
            var cameraPos = _camera.GlobalPosition;
            _cameraPosLabel.Text = $"Camera Positon: ({(int)cameraPos.X}, {(int)cameraPos.Y}, {(int)cameraPos.Z})";
        }
    }

    public override void _Input(InputEvent @event)
    {
        base._Input(@event);
        if (@event is InputEventKey key && key.Pressed)
        {
            if (key.Keycode == Key.F1)
            {
                if (GetViewport().DebugDraw == Viewport.DebugDrawEnum.Disabled)
                {
                    GetViewport().DebugDraw = Viewport.DebugDrawEnum.Wireframe;
                }
                else
                {
                    GetViewport().DebugDraw = Viewport.DebugDrawEnum.Disabled;
                }
            }
        }
    }
}
