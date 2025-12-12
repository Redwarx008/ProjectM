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
    private Terrain? _terrain;

    private MapDefinition _map;
    public async override void _Ready()
    {
        base._Ready();
        _map = await MapDefinition.LoadFromJsonFileAsync(VirtualFileSystem.Instance.ResolvePath("Map/descriptor.json"));
        if (_terrain != null)
        {
            _terrain.Init(_map);
            Logger.Info("terrain init");
        }
    }
    public override void _Process(double delta)
    {
        base._Process(delta);
        Debug.Assert(_fpsLabel != null);
        _fpsLabel.Text = $" FPS: {Godot.Engine.GetFramesPerSecond()}";
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
