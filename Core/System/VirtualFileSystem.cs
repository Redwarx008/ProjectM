using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Core;
public class LaunchSetting
{
    public List<string>? EnableMods { get; set; }

    public static LaunchSetting Load(string path)
    {
        if (!File.Exists(path))
            return new LaunchSetting();

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<LaunchSetting>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? new LaunchSetting();
    }

    public string GetSignature() =>
        EnableMods == null ? string.Empty : string.Join(",", EnableMods).Trim();
}

public class ModInfo
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "1.0";
    public List<string>? Dependencies { get; set; }
    public List<string>? Override { get; set; }

    public static ModInfo Load(string modJsonPath)
    {
        var json = File.ReadAllText(modJsonPath);
        return JsonSerializer.Deserialize<ModInfo>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        }) ?? new ModInfo();
    }

    public string GetSignature() =>
        $"{Name}:{Version}:{string.Join(",", Override)}";
}

public class VfsEntry
{
    public string VirtualPath { get; set; } = "";
    public string PhysicalPath { get; set; } = "";
    public string SourceMod { get; set; } = "";
    public long FileSize { get; set; } = 0;
    public DateTime LastModified { get; set; } = DateTime.MinValue;
}

public class VfsCacheHeader
{
    public string GameSettingsSignature { get; set; } = "";
    public Dictionary<string, string> ModSignatures { get; set; } = new();
    public DateTime BuildTime { get; set; } = DateTime.Now;
    public List<VfsEntry> Entries { get; set; } = new();
}

/// <summary>
/// 单例虚拟文件系统，支持 Base 游戏 + Mod 索引、Override 覆盖目录、缓存。
/// </summary>
public sealed class VirtualFileSystem
{
    private static VirtualFileSystem? _instance;
    public static VirtualFileSystem Instance => _instance ??= new VirtualFileSystem();
    private VirtualFileSystem() { }

    private readonly Dictionary<string, (string root, ModInfo info)> _mods = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string root, ModInfo info)> _loadOrder = new();
    private readonly Dictionary<string, VfsEntry> _index = new(StringComparer.OrdinalIgnoreCase);

    private LaunchSetting _settings = new();

    private string _dataRoot = "";
    private string _modsRoot = "";
    private string _settingsPath = "";
    private string _cachePath = "";

    public IReadOnlyDictionary<string, VfsEntry> Index => _index;

    public VirtualFileSystem Configure(string dataRoot, string modsRoot, string launchSettingsPath, string cachePath = "vfs_cache.json")
    {
        _dataRoot = Path.GetFullPath(dataRoot);
        _modsRoot = Path.GetFullPath(modsRoot);
        _settingsPath = Path.GetFullPath(launchSettingsPath);
        _cachePath = Path.GetFullPath(cachePath);
        return this;
    }

    // ======== 同步初始化 ========
    public void Initialize(bool forceRebuild = false)
    {
        ValidatePaths();

        _settings = LaunchSetting.Load(_settingsPath);

        if (!forceRebuild && TryLoadCache())
        {
            Logger.Info("[VFS] Loaded from cache successfully.");
            return;
        }

        LoadAllMods();
        RebuildIndex();
        SaveCache();
    }
    private void ValidatePaths()
    {
        if (string.IsNullOrEmpty(_dataRoot) || !Directory.Exists(_dataRoot))
            throw new DirectoryNotFoundException($"Data root not found: {_dataRoot}");
        if (string.IsNullOrEmpty(_modsRoot) || !Directory.Exists(_modsRoot))
            throw new DirectoryNotFoundException($"Mods folder not found: {_modsRoot}");
        if (string.IsNullOrEmpty(_settingsPath))
            throw new FileNotFoundException("Game settings path not configured.");
    }

    // ======== 同步加载 Mod 信息 ========
    private void LoadAllMods()
    {
        _mods.Clear();
        foreach (var modDir in Directory.GetDirectories(_modsRoot))
        {
            string modJson = Path.Combine(modDir, "mod.json");
            if (!File.Exists(modJson)) continue;
            var info = ModInfo.Load(modJson);
            _mods[info.Name] = (Path.GetFullPath(modDir), info);
        }

        _loadOrder.Clear();
        if (_settings.EnableMods != null)
        {
            foreach (var modName in _settings.EnableMods)
            {
                if (!_mods.TryGetValue(modName, out var mod))
                {
                    Logger.Info($"[VFS][WARN] Enabled mod '{modName}' not found.");
                    continue;
                }

                if (mod.info.Dependencies != null)
                {
                    foreach (var dep in mod.info.Dependencies)
                    {
                        if (!_settings.EnableMods.Contains(dep))
                            Logger.Info($"[VFS][WARN] Mod '{modName}' depends on '{dep}' not enabled.");
                    }
                }

                _loadOrder.Add(mod);
            }
        }

        Logger.Info("[VFS] Active mods:");
        foreach (var (_, info) in _loadOrder)
            Logger.Info($"  - {info.Name}");
    }

    // ======== 索引构建 ========
    private void RebuildIndex()
    {
        _index.Clear();

        // Base 游戏
        BuildIndexForFolder(_dataRoot, "BaseGame");

        // Mods
        foreach (var (modRoot, info) in _loadOrder)
        {
            // 先处理 Override 子目录
            if (info.Override != null)
            {
                foreach (var folder in info.Override)
                {
                    string overridePrefix = NormalizePath(folder).TrimEnd('/') + "/";

                    // 删除被覆盖目录下文件
                    var keysToRemove = _index.Keys
                        .Where(k => k.StartsWith(overridePrefix, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    foreach (var key in keysToRemove)
                        _index.Remove(key);
                }
            }

            foreach (var file in Directory.EnumerateFiles(modRoot, "*", SearchOption.AllDirectories))
            {
                string rel = NormalizePath(Path.GetRelativePath(modRoot, file));

                var fileInfo = new FileInfo(file);
                _index[rel] = new VfsEntry
                {
                    VirtualPath = rel,
                    PhysicalPath = file,
                    SourceMod = info.Name,
                    FileSize = fileInfo.Length,
                    LastModified = fileInfo.LastWriteTimeUtc
                };
            }
        }

        Logger.Info($"[VFS] Index rebuilt: {_index.Count} files.");
    }

    private void BuildIndexForFolder(string root, string sourceName, bool isOverride = false)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string rel = NormalizePath(Path.GetRelativePath(root, file));
            if (!isOverride && _index.ContainsKey(rel))
                continue;

            var info = new FileInfo(file);
            _index[rel] = new VfsEntry
            {
                VirtualPath = rel,
                PhysicalPath = file,
                SourceMod = sourceName,
                FileSize = info.Length,
                LastModified = info.LastWriteTimeUtc
            };
        }
    }

    // ======== 缓存 ========
    private bool TryLoadCache()
    {
        if (!File.Exists(_cachePath)) return false;

        try
        {
            var cache = JsonSerializer.Deserialize<VfsCacheHeader>(File.ReadAllText(_cachePath));
            if (cache == null) return false;

            if (cache.GameSettingsSignature != _settings.GetSignature())
            {
                Logger.Info("[VFS] Cache invalid: game_settings.json changed.");
                return false;
            }

            _loadOrder.Clear();
            if (_settings.EnableMods != null) // Ensure EnableMods is not null before iterating
            {
                foreach (var modName in _settings.EnableMods)
                {
                    if (!_mods.TryGetValue(modName, out var mod))
                    {
                        Logger.Info($"[VFS][WARN] Enabled mod '{modName}' not found.");
                        continue;
                    }

                    if (mod.info.Dependencies != null) // Ensure Dependencies is not null before iterating
                    {
                        foreach (var dep in mod.info.Dependencies)
                        {
                            if (!_settings.EnableMods.Contains(dep))
                                Logger.Info($"[VFS][WARN] Mod '{modName}' depends on '{dep}' not enabled.");
                        }
                    }

                    _loadOrder.Add(mod);
                }

                foreach (var modName in _settings.EnableMods)
                {
                    string modPath = Path.Combine(_modsRoot, modName, "mod.json");
                    if (!File.Exists(modPath)) return false;

                    var info = ModInfo.Load(modPath);
                    var sig = info.GetSignature();
                    if (!cache.ModSignatures.TryGetValue(modName, out var cachedSig) || cachedSig != sig)
                    {
                        Logger.Info($"[VFS] Cache invalid: mod '{modName}' changed.");
                        return false;
                    }
                }
            }

            _index.Clear();
            foreach (var e in cache.Entries)
                _index[e.VirtualPath] = e;

            Logger.Info($"[VFS] Cache loaded ({_index.Count} entries).");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Info($"[VFS] Cache load failed: {ex.Message}");
            return false;
        }
    }

    private void SaveCache()
    {
        var cache = new VfsCacheHeader
        {
            GameSettingsSignature = _settings.GetSignature(),
            ModSignatures = _loadOrder.ToDictionary(m => m.info.Name, m => m.info.GetSignature()),
            BuildTime = DateTime.Now,
            Entries = _index.Values.ToList()
        };

        var json = JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_cachePath, json);
        Logger.Info($"[VFS] Cache saved ({_index.Count} entries).");
    }

    // ======== 查询 ========
    public string? ResolvePath(string relativePath)
    {
        relativePath = NormalizePath(relativePath);
        return _index.TryGetValue(relativePath, out var entry) ? entry.PhysicalPath : null;
    }

    private static string NormalizePath(string path) => path.Replace("\\", "/").TrimStart('/');
}
