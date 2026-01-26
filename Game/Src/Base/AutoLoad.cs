using Core;
using Godot;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Logger = Core.Logger;

[Tool]
internal partial class AutoLoad : Node
{
    public override void _Ready()
    {
        base._Ready();
        SetLogFunction();
        InitVirtualFileSystem();
    }

    private void InitVirtualFileSystem()
    {
        try
        {
            //string binPath = Directory.GetCurrentDirectory();
            VirtualFileSystem.Instance.Configure(
                dataRoot: "../Data",
                modsRoot: "../Mods",
                launchSettingsPath: "LaunchSetting.json"
            ).Initialize(true);

            Logger.Info("[VFS] Initialization completed successfully");
        }
        catch (Exception e)
        {
            Logger.Error($"[VFS] Initialization failed: {e.Message}");
        }
    }
    private void SetLogFunction()
    {
        Logger.LogInfoFunc = (string message, params object[] args) =>
        {
            GD.Print(string.Format($"[Info] {message}", args));
        };

        Logger.LogWarnFunc = (string message, params object[] args) =>
        {
            GD.PushWarning(string.Format($"[Warning] {message}", args));
        };

        Logger.LogErrorFunc = (string message, params object[] args) =>
        {
            GD.PushError(string.Format($"[Error] {message}", args));
        };
#if DEBUG
        Logger.LogDebugFunc = (string message, params object[] args) =>
        {
            GD.Print(string.Format($"[Debug] {message}", args));
        };
#endif
    }
}