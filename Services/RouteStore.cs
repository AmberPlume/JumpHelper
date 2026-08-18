using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Plugin.Services;
using JumpHelper.Models;

namespace JumpHelper.Services;

/// <summary>
/// 路线文件存取：一路线一 JSON 文件。
/// 默认目录为插件配置目录下的 Routes/；若配置了 RouteDirectory 则使用自定义目录（便于观测/共享）。
/// </summary>
public class RouteStore
{
    private string _routesDir;
    private readonly IPluginLog _log;
    private readonly JsonSerializerOptions _jsonOptions;

    public RouteStore()
    {
        var customDir = (Service.PluginInterface.GetPluginConfig() as Configuration)?.RouteDirectory;
        _routesDir = string.IsNullOrWhiteSpace(customDir)
            ? Path.Combine(Service.PluginInterface.ConfigDirectory.FullName, "Routes")
            : customDir;
        Directory.CreateDirectory(_routesDir);
        _log = Service.Log;

        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Converters = { new JsonStringEnumConverter() }
        };
    }

    /// <summary>当前路线文件目录。</summary>
    public string RoutesDirectory => _routesDir;

    /// <summary>修改路线目录（运行时生效并保存到配置；目录不存在则创建）。</summary>
    public bool ChangeDirectory(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir))
            return false;
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            Service.Log.Error(ex, $"路线目录创建失败: {dir}");
            return false;
        }
        _routesDir = dir;
        var cfg = Service.PluginInterface.GetPluginConfig() as Configuration;
        if (cfg != null)
        {
            cfg.RouteDirectory = dir;
            cfg.Save();
        }
        PluginLog.Info($"路线目录已修改: {dir}");
        return true;
    }

    /// <summary>列出所有已保存的路线名（按名称排序）。</summary>
    public IEnumerable<string> ListRouteNames()
        => Directory.EnumerateFiles(_routesDir, "*.json")
                    .Select(Path.GetFileNameWithoutExtension)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)!;

    /// <summary>路线文件是否已存在。</summary>
    public bool Exists(string name)
        => !string.IsNullOrWhiteSpace(name) && File.Exists(GetRoutePath(name));

    /// <summary>加载路线文件；不存在或解析失败返回 null。</summary>
    public RouteFile? Load(string name)
    {
        var path = GetRoutePath(name);
        if (!File.Exists(path))
            return null;

        try
        {
            var route = JsonSerializer.Deserialize<RouteFile>(File.ReadAllText(path), _jsonOptions);
            if (route == null)
            {
                _log.Warning($"路线文件解析为空: {path}");
                return null;
            }

            route.Name = Path.GetFileNameWithoutExtension(path);
            return route;
        }
        catch (Exception ex)
        {
            Service.Log.Error(ex, $"路线文件解析失败: {path}");
            return null;
        }
    }

    /// <summary>保存路线文件（覆盖写）。</summary>
    public bool Save(RouteFile route)
    {
        if (string.IsNullOrWhiteSpace(route.Name))
            return false;

        try
        {
            var path = GetRoutePath(route.Name);
            File.WriteAllText(path, JsonSerializer.Serialize(route, _jsonOptions));
            return true;
        }
        catch (Exception ex)
        {
            Service.Log.Error(ex, $"路线文件保存失败: {route.Name}");
            return false;
        }
    }

    /// <summary>删除路线文件。</summary>
    public bool Delete(string name)
    {
        var path = GetRoutePath(name);
        if (!File.Exists(path))
            return false;

        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            Service.Log.Error(ex, $"路线文件删除失败: {path}");
            return false;
        }
    }

    /// <summary>导出路线为 JSON 文本（分享：复制到剪贴板/游戏内聊天传播）。</summary>
    public string? ExportToText(RouteFile route)
    {
        try
        {
            return JsonSerializer.Serialize(route, _jsonOptions);
        }
        catch (Exception ex)
        {
            Service.Log.Error(ex, $"路线导出失败: {route.Name}");
            return null;
        }
    }

    /// <summary>从 JSON 文本导入路线（分享导入）。导入成功后保存到路线目录（若同名已存在则拒绝，返回 null）。</summary>
    public RouteFile? ImportFromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        try
        {
            var route = JsonSerializer.Deserialize<RouteFile>(text, _jsonOptions);
            if (route == null || string.IsNullOrWhiteSpace(route.Name))
            {
                PluginLog.Info("路线文本解析失败（文本为空或缺少 Name）");
                return null;
            }
            if (Exists(route.Name))
            {
                PluginLog.Info($"导入失败：路线「{route.Name}」已存在（新不覆盖旧——请改名或先删除旧路线）");
                return null;
            }
            if (!Save(route))
            {
                PluginLog.Info($"导入失败：路线「{route.Name}」保存到磁盘失败（IO 错误，查看日志）");
                return null;
            }
            PluginLog.Info($"已从文本导入路线: {route.Name}（{route.Segments.Count} 段）");
            return route;
        }
        catch (Exception ex)
        {
            Service.Log.Error(ex, "路线文本解析失败");
            return null;
        }
    }

    /// <summary>从外部 JSON 文件导入路线（复制到路线目录；同名已存在则拒绝）。</summary>
    public RouteFile? ImportFile(string filePath)
    {
        try
        {
            var text = File.ReadAllText(filePath);
            var route = ImportFromText(text);
            if (route != null)
                PluginLog.Info($"已从文件导入路线: {filePath} → {route.Name}");
            return route;
        }
        catch (Exception ex)
        {
            Service.Log.Error(ex, $"路线文件导入失败: {filePath}");
            return null;
        }
    }

    private string GetRoutePath(string name)
        => Path.Combine(_routesDir, SanitizeFileName(name) + ".json");

    /// <summary>清洗文件名，避免非法字符导致路径问题。</summary>
    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c));
    }
}
