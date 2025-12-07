using Godot;

internal static class Constants
{
    public static Rid NullRid { get; } = new Rid();

    #region Path

    public static string DataRoot { get; } = "Data";

    #endregion

    #region Terrain
    public static int MaxNodeInSelect { get; } = 200;

    #endregion
}
