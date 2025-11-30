using System.IO;
internal static class Configs
{
    #region map
    public static float WaterHeight { get; private set; }

    #endregion
    static Configs()
    {
        //LodMod(Constants.NativeModPath);
    }

    public static void LodMod(string folder)
    {
        //try
        //{
        //    using StreamReader reader = File.OpenText(Path.Combine(folder, "mod.toml"));
        //    TomlTable table = TOML.Parse(reader);
        //    foreach (TomlNode node in table["replace"])
        //    {
        //        switch (node.AsString.Value)
        //        {
        //            case "Map":
        //                {
        //                    MapFolder = Path.Combine(folder, "Map");
        //                    LoadMapConfig(MapFolder);
        //                    break;
        //                }
        //            default:
        //                break;
        //        }
        //    }

        //}
        //catch (FileNotFoundException exception)
        //{
        //    Logger.Warn($"{exception.Message}");
        //}
    }

    private static void LoadMapConfig(in string folder)
    {

    }
}