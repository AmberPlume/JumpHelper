using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using JumpHelper.Models;

namespace JumpHelper.Services;

/// <summary>云端路线条目（云端列表一行：路线名 + 地图 + 模式 + 上传人 + 描述）。</summary>
public class CloudRouteInfo
{
    /// <summary>路线名称（= COS 对象名去掉 .json）。</summary>
    public string Name { get; set; } = "";

    /// <summary>录制地图名（云端清单里冗余存放，免逐个下载解析）。</summary>
    public string MapName { get; set; } = "";

    /// <summary>段落模式（线性/碎片）。</summary>
    public string ModeLabel { get; set; } = "";

    /// <summary>上传人昵称。</summary>
    public string Uploader { get; set; } = "";

    /// <summary>路线描述。</summary>
    public string Description { get; set; } = "";

    /// <summary>上传时间（ISO 8601，显示用）。</summary>
    public string UploadedAt { get; set; } = "";
}

/// <summary>函数上传结果（POST /upload 响应）。</summary>
public class CloudUploadResult
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("del_token")] public string? DelToken { get; set; }
}

/// <summary>
/// 云端路线服务（腾讯云 COS + SCF 云函数审核）：
/// 列表/下载 = COS 公有读（GET，无需鉴权）；
/// 上传 = POST 到 SCF 云函数（服务端做内容校验/限流/审计后用主密钥写 COS + 维护 index.json）。
///
/// 安全模型（2026-08-21 用户定稿）：
///   - 插件内【不含任何 COS 写凭证】——玩家反编译也灌不了垃圾（唯一写入通道是云函数）
///   - 函数公开 POST，靠「内容校验 + 限流(8次/60s) + audit.json 审计 + adminToken 删除」把关
///   - 函数 URL/配置见 CloudUploadUrl 常量（Web 函数 Flask，路由 /upload /delete）
/// </summary>
public static class CloudRouteService
{
    // ===== 常量（硬编码，勿放主账号密钥） =====
    private const string CloudBucketBase = "https://jumphelper-routes-1324136629.cos.ap-shanghai.myqcloud.com";
    private const string CloudPrefix = "routes/";
    private const string CloudUploadUrl = "https://1324136629-gsbdghqpx0.ap-shanghai.tencentscf.com/upload";
    private const string CloudFeedbackUrl = "https://1324136629-gsbdghqpx0.ap-shanghai.tencentscf.com/feedback";
    private const string CloudSuggestUrl = "https://1324136629-gsbdghqpx0.ap-shanghai.tencentscf.com/suggest";
    private const string CloudDeleteUrl = "https://1324136629-gsbdghqpx0.ap-shanghai.tencentscf.com/delete";

    /// <summary>恒可用（上传走云函数，无需本地密钥）。</summary>
    public static bool IsConfigured => true;

    /// <summary>恒可用（上传走云函数，无需本地密钥）。</summary>
    public static bool IsUploadReady => true;

    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        // 国内直连 COS/云函数，绝不走系统代理——玩家可能开加速器/Clash，代理会把
        // 腾讯云域名请求带偏导致超时/失败（实测其他玩家云端列表空即此因）。
        UseProxy = false,
        AutomaticDecompression = System.Net.DecompressionMethods.All
    })
    { Timeout = TimeSpan.FromSeconds(10) };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>桶内文件路径前缀（规范化：去除首尾斜杠 + 尾部斜杠）。</summary>
    public static string NormalizedPrefix()
    {
        var p = CloudPrefix.Trim('/');
        return p.Length == 0 ? "" : p + "/";
    }

    /// <summary>对象 key 的 URL 编码（逐段编码，保留 /；下载用）。</summary>
    private static string EncodeKey(string key)
        => string.Join("/", key.Split('/').Select(Uri.EscapeDataString));

    /// <summary>拉取云端路线清单（含上传人/描述）：GET index.json。
    /// 返回 null = 拉取失败（调用方保留旧缓存并提示）；空列表 = 桶空（404）。</summary>
    public static List<CloudRouteInfo>? ListRouteInfos()
    {
        try
        {
            var url = $"{CloudBucketBase.TrimEnd('/')}/{NormalizedPrefix()}index.json";
            using var resp = Http.GetAsync(url).GetAwaiter().GetResult();
            if (resp.IsSuccessStatusCode)
            {
                var text = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                return JsonSerializer.Deserialize<List<CloudRouteInfo>>(text, JsonOpts);
                // 云端清单每秒后台拉取，成功不记日志（防 jump.log 刷屏）
            }
            if ((int)resp.StatusCode == 404)
                return new List<CloudRouteInfo>(); // 空桶（尚无路线）
            PluginLog.Error($"CloudRouteService: 云端清单拉取失败 HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            Service.Log.Error(ex, "CloudRouteService: 云端清单拉取异常");
        }
        return null; // 失败
    }

    /// <summary>下载并解析云端路线：GET 路线 JSON → RouteFile（与本地路线 JSON 同格式）。</summary>
    public static RouteFile? DownloadRoute(string name)
    {
        try
        {
            var key = NormalizedPrefix() + EncodeKey(name) + ".json";
            var url = $"{CloudBucketBase.TrimEnd('/')}/{key}";
            using var resp = Http.GetAsync(url).GetAwaiter().GetResult();
            if (resp.IsSuccessStatusCode)
            {
                var text = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                var route = JsonSerializer.Deserialize<RouteFile>(text, JsonOpts);
                PluginLog.Info($"CloudRouteService: 云端路线「{name}」下载成功（{text.Length} 字节）");
                return route;
            }
            PluginLog.Error($"CloudRouteService: 云端路线「{name}」下载失败 HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            Service.Log.Error(ex, $"CloudRouteService: 云端路线「{name}」下载异常");
        }
        return null;
    }

    /// <summary>一键上传路线（带上传人昵称与描述）：POST 云函数（服务端校验/限流/审计后写 COS + 维护 index.json）。
    /// 成功返回删除令牌（函数用主密钥 HMAC(路线名) 生成，仅本机保存——删除自己上传的路线用）。</summary>
    public static (bool Ok, string? DelToken) UploadRoute(RouteFile route, string uploader, string description)
    {
        try
        {
            var jsonText = JsonSerializer.Serialize(route, JsonOpts);
            var map = !string.IsNullOrWhiteSpace(route.TerritoryName)
                ? route.TerritoryName
                : $"地图 {route.TerritoryId}";
            var mode = route.SegmentMode == SegmentMode.Fragment ? "碎片" : "线性";
            var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["name"] = route.Name,
                ["json_text"] = jsonText,
                ["uploader"] = uploader,
                ["description"] = description,
                ["map"] = map,
                ["mode"] = mode
            }, JsonOpts);

            using var req = new HttpRequestMessage(HttpMethod.Post, CloudUploadUrl);
            req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var resp = Http.SendAsync(req).GetAwaiter().GetResult();
            var respBody = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode)
            {
                PluginLog.Error($"CloudRouteService: 路线上传失败 HTTP {(int)resp.StatusCode}: {respBody}");
                return (false, null);
            }
            var result = JsonSerializer.Deserialize<CloudUploadResult>(respBody, JsonOpts);
            if (result == null || !result.Ok)
            {
                PluginLog.Error($"CloudRouteService: 路线上传被拒: {result?.Error ?? respBody}");
                return (false, null);
            }
            PluginLog.Info($"CloudRouteService: 路线「{route.Name}」上传成功（云函数）");
            return (true, result.DelToken);
        }
        catch (Exception ex)
        {
            Service.Log.Error(ex, $"CloudRouteService: 路线「{route.Name}」上传异常");
            return (false, null);
        }
    }

    /// <summary>删除自己上传的云端路线：POST 云函数 /delete（del_token 由上传时函数下发，仅本机持有）。</summary>
    public static bool DeleteRoute(string name, string delToken)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["name"] = name,
                ["del_token"] = delToken
            }, JsonOpts);
            using var req = new HttpRequestMessage(HttpMethod.Post, CloudDeleteUrl);
            req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var resp = Http.SendAsync(req).GetAwaiter().GetResult();
            var respBody = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (resp.IsSuccessStatusCode)
            {
                PluginLog.Info($"CloudRouteService: 删除云端路线「{name}」成功");
                return true;
            }
            PluginLog.Error($"CloudRouteService: 删除「{name}」失败 HTTP {(int)resp.StatusCode}: {respBody}");
        }
        catch (Exception ex)
        {
            Service.Log.Error(ex, $"CloudRouteService: 删除「{name}」异常");
        }
        return false;
    }

    /// <summary>路线反馈（对某条云端路线提问题/意见）：POST 云函数 /feedback（记入 feedback.json，
    /// 维护者导出查阅后清空）。与「提出建议」（/suggest）通道分离。</summary>
    public static bool SendRouteFeedback(string name, string text)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["name"] = name,
                ["text"] = text
            }, JsonOpts);
            using var req = new HttpRequestMessage(HttpMethod.Post, CloudFeedbackUrl);
            req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var resp = Http.SendAsync(req).GetAwaiter().GetResult();
            if (resp.IsSuccessStatusCode)
            {
                PluginLog.Info($"CloudRouteService: 路线反馈「{name}」提交成功");
                return true;
            }
            PluginLog.Error($"CloudRouteService: 路线反馈「{name}」提交失败 HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            Service.Log.Error(ex, $"CloudRouteService: 路线反馈「{name}」提交异常");
        }
        return false;
    }

    /// <summary>提出建议/反馈问题（设置页入口）：POST 云函数 /suggest（记入 suggestions.json，
    /// 维护者导出查阅后清空）。</summary>
    public static bool SendSuggestion(string text)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new Dictionary<string, object?> { ["text"] = text }, JsonOpts);
            using var req = new HttpRequestMessage(HttpMethod.Post, CloudSuggestUrl);
            req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var resp = Http.SendAsync(req).GetAwaiter().GetResult();
            if (resp.IsSuccessStatusCode)
            {
                PluginLog.Info("CloudRouteService: 建议提交成功");
                return true;
            }
            PluginLog.Error($"CloudRouteService: 建议提交失败 HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            Service.Log.Error(ex, "CloudRouteService: 建议提交异常");
        }
        return false;
    }
}
