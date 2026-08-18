namespace JumpHelper.Services;

/// <summary>
/// 插件独立日志：正常流程日志写入 ConfigDirectory/Logs/jump.log（不污染卫月主日志）。
/// 用户反馈问题时直接发该文件即可。文件超限（2MB）自动清空重写（及时清理，只保留最近日志）；
/// 卫月 _log.Error 仅保留真正的异常（catch/ex）路径。
/// 线程安全：游戏主线程 + 少量 Task 后台写入，统一加锁。
/// </summary>
public static class PluginLog
{
    private const long MaxBytes = 2 * 1024 * 1024; // 2MB 上限，超限清空
    private static readonly object Sync = new();
    private static string? _path;

    /// <summary>初始化日志文件（插件构造时调用一次；目录不存在则创建）。</summary>
    public static void Init(string configDir)
    {
        try
        {
            var dir = Path.Combine(configDir, "Logs");
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, "jump.log");
            // 启动清理：超限文件直接清空（防累积）
            var fi = new FileInfo(_path);
            if (fi.Exists && fi.Length > MaxBytes)
                File.WriteAllText(_path, "");
        }
        catch
        {
            _path = null; // 日志目录不可写则静默禁用（不影响插件功能）
        }
    }

    /// <summary>正常流程日志（执行段/落地/达标/采集/标定等）。</summary>
    public static void Info(string msg) => Write("INFO", msg);

    /// <summary>业务错误日志（失败/超时/未移动等回放失败原因——用户反馈时文件里可查）。</summary>
    public static void Error(string msg) => Write("ERROR", msg);

    private static void Write(string level, string msg)
    {
        if (_path == null)
            return;
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {msg}\n";
        lock (Sync)
        {
            try
            {
                // 超限清空（保留最近日志，防无限增长）
                if (new FileInfo(_path).Length + line.Length > MaxBytes)
                    File.WriteAllText(_path, "");
                File.AppendAllText(_path, line);
            }
            catch
            {
                // 日志写入失败不影响插件
            }
        }
    }
}
