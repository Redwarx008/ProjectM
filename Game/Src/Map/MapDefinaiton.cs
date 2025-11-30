using Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

public record struct MapDefinition
{
    public float TerrainTolerableError { get; set; }
    public float TerrainHeightScale { get; set; }
    public float WaterHeight { get; set; }

    /// <summary>
    /// 从JSON字符串反序列化MapDefinition
    /// </summary>
    /// <param name="jsonString">包含地形参数的JSON字符串</param>
    /// <returns>反序列化后的MapDefinition实例</returns>
    public static MapDefinition FromJson(string jsonString)
    {
        try
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };
            var result = JsonSerializer.Deserialize<MapDefinition>(jsonString, options);
            
            if (result == null)
            {
                Logger.Error("Failed to deserialize MapDefinition: result is null");
                return new MapDefinition();
            }
            
            return result;
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to deserialize MapDefinition: {ex.Message}");
            return new MapDefinition();
        }
    }

    /// <summary>
    /// 从JSON文件加载MapDefinition
    /// </summary>
    /// <param name="filePath">JSON文件路径</param>
    /// <returns>加载的MapDefinition实例</returns>
    public static async Task<MapDefinition> LoadFromJsonFileAsync(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                Logger.Error($"JSON file not found: {filePath}");
                return new MapDefinition();
            }
            
            string jsonString = await File.ReadAllTextAsync(filePath);
            return FromJson(jsonString);
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to load MapDefinition from file: {ex.Message}");
            return new MapDefinition();
        }
    }

    public static MapDefinition LoadFromJsonFile(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                Logger.Error($"JSON file not found: {filePath}");
                return new MapDefinition();
            }

            string jsonString = File.ReadAllText(filePath);
            return FromJson(jsonString);
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to load MapDefinition from file: {ex.Message}");
            return new MapDefinition();
        }
    }
}