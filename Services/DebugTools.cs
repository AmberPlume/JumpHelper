namespace JumpHelper.Services;

/// <summary>
/// 调试工具集：历史诊断日志的统一开关（默认关闭，不进入设置面板——故障诊断时手动打开重新编译即可）。
/// const bool = 编译期开关：关闭时相关代码不生成，发布版零运行时开销。
/// 历史调试命令已随 /jh 命令精简移除（cmd 功能全部由 UI 承担）；如需恢复诊断命令，在本类补充开关+入口。
/// </summary>
public static class DebugTools
{
    // ===== 诊断日志开关（ReplayEngine / MovementController 内使用） =====

    /// <summary>[走位诊断]——走位进度每 200ms 一条（走位不执行/走位偏差排查）。</summary>
    public const bool DiagWalk = false;

    /// <summary>预助跑前等待静止 / 输入门控（additive/e1/e2）诊断。</summary>
    public const bool DiagPreRun = false;

    /// <summary>时间线起跳前每帧输入日志（@XXXms 注入值）。</summary>
    public const bool DiagTimeline = false;
}
