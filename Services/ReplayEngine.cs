using System.Numerics;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using JumpHelper.Models;

namespace JumpHelper.Services;

/// <summary>回放引擎状态。</summary>
public enum ReplayState
{
    /// <summary>空闲。</summary>
    Idle,

    /// <summary>走位到本段冲刺起点（录制时最后直线冲刺段的起点）。</summary>
    NavToRunStart,

    /// <summary>时间线模式预助跑：沿录制起始朝向助跑，速度达到录制起点速度后开始时间线重放（位置轨迹对齐）。</summary>
    PreRunToSpeed,

    /// <summary>全量输入时间线重放（对齐时间线起点后逐帧重放录制输入）。</summary>
    TimelinePlay,

    /// <summary>转向到起跳朝向（录制时起跳瞬间朝向；旧数据回退为落点方向）。</summary>
    TurnToTakeoff,

    /// <summary>朝落点方向助跑，接近起跳点瞬间起跳。</summary>
    RunToTakeoff,

    /// <summary>等待起跳生效（Y 上升）。</summary>
    Jump,

    /// <summary>空中。</summary>
    InAir,

    /// <summary>落地校验：与录制落点距离。</summary>
    Land,

    /// <summary>走位微调对齐（落点偏离但在平台上）。</summary>
    WalkAdjust,

    /// <summary>等待玩家手动走到下一段起跳点（段间长距离——跳跳乐地图机制复杂，
    /// 段间行走路径回放不可靠，交玩家手动走，到位后自动继续跳）。</summary>
    AwaitPlayer,

    /// <summary>移速状态修正（段前校验：录制段需冲刺/慢跑/速行而玩家当前没有时——
    /// 自动施放技能补齐并等待 buff 出现；冲刺等慢跑场景等待状态转换。状态必须一致，
    /// 否则起跳速度不匹配跳过头/跳不够——用户实测结论）。</summary>
    AwaitStatus,

    /// <summary>常速段冲突暂停（录制常速、玩家当前带移速状态）：无法自动取消状态，暂停等待玩家
    /// 自行处理（清除状态或确认带状态继续），点悬浮窗「继续」恢复或「终止」取消。</summary>
    PausedForStatus,

    /// <summary>成功（到达终点标记）。</summary>
    Success,

    /// <summary>失败（重试耗尽）。</summary>
    Failed
}

/// <summary>
/// 回放引擎（段驱动）：路线由录制时自动采集的跳跃段组成，
/// 每段 = { 起跳点, 落点 }。回放逐段：转向起跳方向 → 助跑 → 接近起跳点瞬间起跳
/// → 空中 → 落地校验（与录制落点距离）→ 微调/重试。
/// 物理前提（已标定）：跳跃距离由起跳瞬间水平速度决定，空中前进无效；
/// 因此回放必须"跑动中起跳"（助跑 ≥2m 满速）才能在录制起跳点复现录制落点。
/// </summary>
public unsafe sealed class ReplayEngine : IDisposable
{
    private readonly MovementController _movement;
    private readonly JumpExecutor _jump;
    private readonly IPluginLog _log;

    public ReplayState State { get; private set; } = ReplayState.Idle;

    /// <summary>当前回放路线。</summary>
    public RouteFile? CurrentRoute { get; private set; }

    /// <summary>当前段索引。</summary>
    public int CurrentSegment { get; private set; }

    /// <summary>终点段索引。</summary>
    public int EndSegment { get; private set; }

    /// <summary>距当前目标（落点/起跳点）的水平距离（米），供 UI 显示。</summary>
    public float DistanceToTarget { get; private set; }

    // 段执行状态
    private Vector3 _takeoffPos;
    private Vector3 _landPos;
    private Vector3 _runStartPos;     // 冲刺起点（录制时最后直线冲刺段起点）
    private float _takeoffYaw;        // 起跳瞬间朝向（录制数据或落点方向回退）
    private float _timelineStartYaw;  // 时间线起点朝向（录制时起跳前 InputLeadMs 的玩家朝向）
    private bool _useTimeline;        // 段含全量输入时间线（全量记录模式）
    private List<InputFrame> _timeline = new(); // 当前段输入时间线
    private long _timelineStart;      // 时间线重放开始时刻
    private long _timelineSkipToMs;   // 站定快进：时间线起点前移量（跳过录制时起跳前的无移动输入站定期）
    private int _timelineIdx;         // 当前时间线帧索引
    private bool _lastTimelineJump;   // 上一帧 jump 状态（边沿检测）
    private long _jumpHoldUntil;      // jump 按下保持截止时刻（120ms 保险）
    private long _timelineEndMs;      // 时间线播完时刻（最后帧 + 落地缓冲）
    private long _timelineJumpAtMs = -1; // 时间线中第一个 jump 帧的相对时间（起跳前朝向锁定用；-1 = 无 jump）
    private bool _timelineHasYaw;     // 时间线含绝对朝向字段（新路线）；旧路线（无 Yaw）保持相对转向基准
    private float _preRunSpeed;       // 时间线起点速度（录制值）——预助跑目标
    private float _preRunDist;        // 预助跑起点后移距离（StartPos 后方，抵消加速位移）
    private int _preRunMatchStreak;   // 预助跑速度连续达标帧数（防帧间瞬时速度抖动提前触发）
    private Vector3 _prevFramePos;    // 预助跑帧间速度计算
    private long _prevFrameTime;
    private float _takeoffY;
    private float _prevY;
    private float _prevFrameY;        // 时间线播放帧间 Y（落地提前检测用）
    private Vector3 _playPrevPos;     // 时间线播放帧间位置（起跳速度诊断：jump 帧瞬间实际速度 vs 录制）
    private long _playPrevTime;
    private long _lastWalkDiagMs;     // 走位进度诊断节流
    private long _walkFellSince;      // 走位中高度偏差超阈值起始时刻（0 = 未触发；摔落检测确认计时）
    private Vector3 _awaitTarget;     // 等待玩家到位目标（下一段起跳点）
    private bool _wasDescending;
    private float _fallAccum;
    private long _stateEnteredAt;

    // 移速状态修正（AwaitStatus）：等待目标状态出现（自动施放技能 / 冲刺等变慢跑）
    private MoveState _awaitTargetState;   // 等待的目标状态
    private bool _awaitCastDone;           // 技能已施放（true = 只需等 buff 自然出现，如冲刺结束变慢跑）
    private long _awaitLastCastAt;         // 上次尝试施放时刻（施放重试节流）
    private long _awaitTimeoutMs;          // 本段状态修正超时（按目标状态动态计算）
    private bool _awaitCastWarned;         // 施放失败是否已提醒（防 2s 重试刷屏）
    private bool _skipStatusOnce;          // 玩家在 PausedForStatus 点「继续」后跳过本段状态检查（确认带状态继续）
    private bool? _walkBeforeReplay;       // 回放开始前的走路模式（Stop 时恢复——插件切了走路不能留给玩家困惑）
    private bool _awaitIsWalkSwitch;       // 当前 AwaitStatus 是"走路切换"（达标=IsWalking 到位，之后重入走 buff 修正）
    private bool _awaitNeedWalk;           // 走路切换目标（true=慢走，false=跑步）
    private long _lastInputGateDiagMs;     // 输入等待诊断日志限频（LastInputAllowed=false 时打印门控分量）
    private long _preRunDiagMs;            // 预助跑采样诊断限频（定位"位置在动但速度采样 0"）

    // ===== 链跳测试（/ja chaintest：对比"单独跳"与"连跳第二跳"的跳距——验证落地速度是否给后一跳加成） =====
    private bool _chainMode;               // 链跳测试进行中
    private int _chainJumps;               // 已完成跳数（0=第1跳前，1=第1跳完成，2=全部完成）
    private float _chainFirstDist;         // 第 1 跳跳距

    // 标定模式
    private bool _calibrationMode;
    private float _calibrationRunDist;

    // 超时（毫秒）
    private const long RunupMs = 800;
    private const long TurnTimeoutMs = 3000;
    private const long PreRunTimeoutMs = 5000;

    /// <summary>连续快跳判定阈值（毫秒）：时间线中第一个 jump 帧早于此值 = 录制"落地即跳"，
    /// 时间线起点（落地后 LandBufferMs）截掉了落地后的加速段 → 用录制起跳速度做预助跑补偿。</summary>
    private const long EarlyJumpMs = 150;

    /// <summary>预助跑补偿的"提前冲刺"判定提前量（毫秒）：jump 帧前此时间之外存在位移输入才算
    /// "有提前冲刺段"（排除 jump 帧本身/起跳瞬间的 W 输入——落地即跳段 jump 前几 ms 就有 F=1）。</summary>
    private const long PreJumpLeadMs = 150;

    /// <summary>
    /// 时间线内绝对朝向修正的满值角（弧度，15°）：delta ≥ 此值 → turn=±1 全速转向，< 此值 → 比例收敛。
    /// 录制鼠标转向快、回放键盘转向慢——15° 满值：常见起跳前转向量（≤30°）全程全速，
    /// 接近时比例收敛，起跳朝向残差压到死区以内。
    /// </summary>
    private const float TimelineTurnFullAngle = MathF.PI / 12;

    /// <summary>绝对朝向修正死区（弧度）：朝向偏差小于此值停止注入转向（比转向判定容差更紧，缩小起跳朝向残差）。</summary>
    private const float YawCorrectionDeadzone = 0.005f;

    /// <summary>起跳点高度差容差（米）：起跳必须与录制起跳点同层平台，防止在错误高度起跳。</summary>
    private const float TakeoffHeightTolerance = 0.5f;

    private const long JumpTimeoutMs = 1500;
    private const long InAirTimeoutMs = 6000;
    private const long RunToTakeoffTimeoutMs = 8000;
    private const long NavToRunStartTimeoutMs = 10000;
    private const long WalkAdjustTimeoutMs = 6000;

    /// <summary>预助跑起点对齐容差（米）：进入预助跑前玩家与预助跑起点的偏差超过此值 → 重新走位。
    /// 严格到位 0.01m 是 NavToRunStart 的目标；此值用于"复核"，防走位误判/移动平台漂移带偏起跳位置。</summary>
    private const float PreRunStartAlignTolerance = 0.3f;

    /// <summary>预助跑兜底强制开始的最低速度（m/s）：越过起点但仍低于此速度 = 玩家根本没跑动
    /// （卡住/输入未生效/平台漂移），不盲跳——止损失败并提示，避免零速度起跳落点必偏。</summary>
    private const float PreRunMinSpeedToStart = 1.0f;

    /// <summary>预助跑前静止确认阈值（m/s）：落地滑行/走位残余速度低于此值才允许开始预助跑。
    /// 防"滑过起点后停稳 → overshoot 误触发未移动"（实测段 1 走两步不动）。</summary>
    private const float PreRunStartSpeedTol = 0.15f;

    /// <summary>移速状态修正超时（毫秒）：速行/冲刺施放后生效快（≤6s）；冲刺等变慢跑
    /// （20s 冲刺效果 + 缓冲）放宽到 26s；通用兜底 30s。</summary>
    private const long AwaitStatusTimeoutBaseMs = 6000;
    private const long AwaitStatusSprintToJogMs = 26000;
    private const long AwaitStatusTimeoutMaxMs = 30000;

    /// <summary>走位摔落检测：走位中玩家 Y 与目标 Y 差超过此值 = 已不在目标平台（摔落/被障碍推下）——
    /// 走位是水平移动，正常场景高度差应 <1m（同平台）；跳跳乐平台间大高差由跳跃段处理，走位不负责跨平台。</summary>
    private const float WalkFellHeightDiff = 2.0f;

    /// <summary>走位摔落确认时间（毫秒）：高度差持续超阈值此时长才判定摔落——防落地瞬间/上坡途中 Y 摆动误判。</summary>
    private const long WalkFellConfirmMs = 300;

    // 阈值

    public ReplayEngine(MovementController movement, JumpExecutor jump)
    {
        _movement = movement;
        _jump = jump;
        _log = Service.Log;
        Service.Framework.Update += Tick;
    }

    // ===== 入口 =====

    /// <summary>
    /// 按段索引回放：从起点段执行到终点段（段内自含起点对齐——ExecuteSegment 的 NavToRunStart
    /// 会走位到该段预助跑起点，玩家需自行到起点段起跳点所在平台，高度匹配检查保证）。
    /// 段表格的"读档"用此接口（起点/终点 = 段，比标记更细粒度；不丢弃任何段）。
    /// </summary>
    public void StartRouteSegments(RouteFile route, int startSegment, int endSegment)
    {
        if (route.Segments.Count == 0)
        {
            PluginLog.Info("ReplayEngine: 路线无段");
            return;
        }

        // 重置模式标志（防止上次标定/单跳/链跳残留劫持回放流程）
        _calibrationMode = false;
        _calibrationRunDist = 0f;
        _chainMode = false;
        _chainJumps = 0;

        if (startSegment < 0 || endSegment < startSegment || endSegment >= route.Segments.Count)
        {
            PluginLog.Info($"ReplayEngine: 非法段范围 start={startSegment} end={endSegment} segments={route.Segments.Count}");
            return;
        }

        if (route.TerritoryId != Service.ClientState.TerritoryType)
            PluginLog.Info($"ReplayEngine: 警告 路线地图 Territory={route.TerritoryId} 与当前 {Service.ClientState.TerritoryType} 不符");

        CurrentRoute = route;
        CheckControlMode(route);
        CurrentSegment = startSegment;
        EndSegment = endSegment;
        _walkBeforeReplay = MoveStatusHelper.IsWalking; // 回放结束（Stop）恢复走路模式

        // 高度匹配检查：玩家需与起点段起跳点同平台（误差 ≤0.01m）——不一致直接放弃移动
        var startPos = route.Segments[startSegment].Takeoff;
        var player = Service.ObjectTable.LocalPlayer;
        if (player != null)
        {
            var yAlign = Service.Config.YAlignTolerance; // 路不平地图放宽（默认 0.3m）
            var yDiff = MathF.Abs(player.Position.Y - startPos.Y);
            if (yDiff > yAlign)
            {
                PluginLog.Error($"ReplayEngine: 放弃移动：起点段起跳点高度不匹配" +
                           $"（当前位置 {player.Position.Y:F2} vs 起点 {startPos.Y:F2}，差 {yDiff:F2}m > {yAlign:F2}m）" +
                           $"——请先到起点段起跳点所在平台再读档");
                return;
            }
        }

        _movement.ReleaseAll();
        ExecuteSegment(startSegment);
        PluginLog.Info($"ReplayEngine: 回放开始 段[{startSegment}→{endSegment}]，先对齐起点段起跳点 ({startPos.X:F1},{startPos.Y:F1},{startPos.Z:F1})");
    }

    /// <summary>单跳测试：朝当前朝向助跑后跳跃 dist 米（保留调试）。</summary>
    public void StartSingleJump(float dist)
    {
        var p = Service.ObjectTable.LocalPlayer;
        if (p == null)
        {
            PluginLog.Info("ReplayEngine: 玩家不存在");
            return;
        }

        var forward = new Vector3(MathF.Sin(p.Rotation), 0, MathF.Cos(p.Rotation));
        _landPos = p.Position + forward * dist;
        PluginLog.Info($"ReplayEngine: 单跳测试 → 目标 ({_landPos.X:F2}, {_landPos.Y:F2}, {_landPos.Z:F2})");

        // 清空路线引用：单跳必须走"跑足时长再跳"分支，不能复用上次回放的路线
        CurrentRoute = null;
        _calibrationMode = false;
        _movement.SetForward(true);
        _takeoffPos = p.Position;
        _takeoffY = p.Position.Y;
        EnterState(ReplayState.RunToTakeoff);
    }

    /// <summary>标定：助跑 runDist 米后起跳（runDist=0 = 原地跳不助跑），报告起跳点→落点实际跳距。</summary>
    public void StartCalibration(float runDist)
    {
        var p = Service.ObjectTable.LocalPlayer;
        if (p == null)
        {
            PluginLog.Info("ReplayEngine: 玩家不存在");
            return;
        }

        // 清空路线引用：标定必须走"跑足距离再跳"分支，不能复用上次回放的路线
        CurrentRoute = null;
        _calibrationMode = true;
        _calibrationRunDist = Math.Max(0f, runDist);
        _takeoffPos = p.Position;
        _takeoffY = p.Position.Y;
        // 标定目标点（前方 20m）：Land 状态只报告"起跳点→落点实际距离"，目标本身无关紧要
        var forward = new Vector3(MathF.Sin(p.Rotation), 0, MathF.Cos(p.Rotation));
        _landPos = p.Position + forward * 20f;

        if (runDist <= 0f)
        {
            // 原地跳（不助跑）：测"从静止起跳"的基线跳距
            _movement.ReleaseAll();
            _jump.Jump();
            EnterState(ReplayState.Jump);
            PluginLog.Info("ReplayEngine: 标定开始 原地跳（助跑 0m）");
        }
        else
        {
            _movement.SetForward(true);
            EnterState(ReplayState.RunToTakeoff);
            PluginLog.Info($"ReplayEngine: 标定开始 助跑 {_calibrationRunDist:F2}m");
        }
    }

    /// <summary>链跳对比测试：同一助跑距离，第 1 跳（静止起步）与落地后立即的第 2 跳（连跳）对比跳距。
    /// 若第 2 跳明显更远 → 落地速度保留给了后一跳起跳（录制连跳段 TakeoffSpeed 偏高、回放静止起步跳不够的物理根因）。</summary>
    public void StartChainTest(float runDist)
    {
        var p = Service.ObjectTable.LocalPlayer;
        if (p == null)
        {
            PluginLog.Info("ReplayEngine: 玩家不存在");
            return;
        }
        CurrentRoute = null;
        _calibrationMode = true;                       // 复用"跑足距离再跳"的单跳逻辑
        _calibrationRunDist = Math.Max(0f, runDist);
        _chainMode = true;
        _chainJumps = 0;
        _chainFirstDist = 0f;
        _takeoffPos = p.Position;
        _takeoffY = p.Position.Y;
        var forward = new Vector3(MathF.Sin(p.Rotation), 0, MathF.Cos(p.Rotation));
        _landPos = p.Position + forward * 20f;

        if (runDist <= 0f)
        {
            _movement.ReleaseAll();
            _jump.Jump();
            EnterState(ReplayState.Jump);
            PluginLog.Info("ReplayEngine: 链跳测试 第1跳 原地跳（助跑 0m）");
        }
        else
        {
            _movement.SetForward(true);
            EnterState(ReplayState.RunToTakeoff);
            PluginLog.Info($"ReplayEngine: 链跳测试 第1跳 助跑 {_calibrationRunDist:F2}m（静止起步）");
        }
    }

    /// <summary>紧急停止：释放全部输入回到 Idle，并清空回放路线引用（世界标记不再显示旧回放路线）。</summary>
    public void Stop()
    {
        _movement.TimelineInput = null;
        _movement.WorldMoveYaw = null;
        _jump.SetHeld(false);
        _movement.ReleaseAll();
        _jump.Stop();
        // 恢复回放开始前的走路模式（插件自动切过走路/跑步的话，回放结束交还给玩家原状态）
        if (_walkBeforeReplay is { } walkBefore)
            MoveStatusHelper.SetWalking(walkBefore);
        _walkBeforeReplay = null;
        CurrentRoute = null; // 回放结束：RouteOverlay 路线来源回退到当前录制路线，避免旧回放标记残留
        State = ReplayState.Idle;
    }

    /// <summary>常速冲突暂停（PausedForStatus）后继续：跳过本段状态检查直接执行——
    /// 玩家点「继续」即确认带状态继续（自担跳过头风险）。</summary>
    public void Resume()
    {
        if (State != ReplayState.PausedForStatus || CurrentRoute == null)
            return;
        _skipStatusOnce = true;
        PluginLog.Info($"ReplayEngine: 玩家确认继续（跳过段 {CurrentSegment + 1} 状态检查）");
        ExecuteSegment(CurrentSegment);
    }

    /// <summary>聊天黄色提示（信息类醒目提示——FF14 UIColor 45 淡黄，区别于默认白色/灰暗文本）。</summary>
    private static void ChatInfo(string msg)
        => Service.ChatGui.Print(new SeStringBuilder().AddUiForeground(45).AddText(msg).Build());

    // ===== 段执行 =====

    /// <summary>操作模式一致性检测（同 RecorderService）：录制模式 vs 当前模式不一致 → 警告。
    /// 输入语义依赖操作模式（标准=相对角色朝向可复现；传统=相对相机不可复现），混合必偏。</summary>
    private void CheckControlMode(RouteFile route)
    {
        var cur = _movement.IsLegacyMode ? 1 : 0;
        if (cur == route.ControlMode)
            return;
        var modeName = cur == 1 ? "传统" : "标准";
        var recName = route.ControlMode == 1 ? "传统" : "标准";
        Service.ChatGui.PrintError($"操作模式警告：当前是{modeName}模式，但该路线录制时是{recName}模式——输入语义不同，" +
                                   $"回放方向会偏差。请切回{recName}模式（建议统一用标准模式录制/回放）");
        PluginLog.Info($"ReplayEngine: 操作模式不一致 当前 {modeName} vs 录制 {recName}——回放方向可能偏差");
    }

    /// <summary>
    /// 进入下一段：扩展段（Extended 标记或旧长路径数据——时间线含段间行走）→ 直接执行（完整复现行走）；
    /// 段间长距离（本段落点 → 下一段起跳点 XZ > LongWalkDist）→ 等待玩家手动走到下一段起跳点
    /// （半自动——跳跳乐地图机制复杂，段间行走回放不可靠）；短距离段间保持全自动走位衔接。
    /// </summary>
    private void GoNextSegment()
    {
        var next = CurrentSegment + 1;
        if (next > EndSegment)
        {
            ExecuteSegment(next); // 到终点（Success）
            return;
        }

        var nextSeg = CurrentRoute!.Segments[next];
        if (IsExtendedSegment(nextSeg))
        {
            ExecuteSegment(next); // 扩展段：时间线完整复现段间行走（机制确定的高精度场景）
            return;
        }

        var land = CurrentRoute.Segments[CurrentSegment].Land;
        var takeoff = nextSeg.Takeoff;
        var dx = takeoff.X - land.X;
        var dz = takeoff.Z - land.Z;
        var dist = MathF.Sqrt(dx * dx + dz * dz);
        if (dist > Service.Config.LongWalkDist)
        {
            _awaitTarget = takeoff;
            _awaitStableSince = 0;
            _awaitLastPos = Service.ObjectTable.LocalPlayer?.Position ?? takeoff;
            EnterState(ReplayState.AwaitPlayer);
            Service.ChatGui.Print($"段 {CurrentSegment + 1} 完成——请手动走到段 {next + 1} 起跳点（{takeoff.X:F0},{takeoff.Y:F0},{takeoff.Z:F0}，需同高度），到位后自动继续跳");
            PluginLog.Info($"ReplayEngine: 段 {CurrentSegment + 1} 完成，段间距离 {dist:F1}m（>{Service.Config.LongWalkDist:F1}m）——等待玩家手动走到段 {next + 1} 起跳点");
        }
        else
        {
            ExecuteSegment(next);
        }
    }

    /// <summary>是否扩展段（需完整复现段间行走）：仅玩家在段落编辑主动勾选「扩展」的段（Extended == true）。
    /// 旧数据时间线即使含行走也不算扩展段——走半自动（段间长距离等待玩家手动走，到位后自动继续），
    /// 时间线行走部分由 TrimTimeline 截断（玩家已手动到位，防止重复行走错位）。</summary>
    private static bool IsExtendedSegment(JumpSegment seg) => seg.Extended == true;

    /// <summary>时间线兼容截断保留段（毫秒）：旧长路径段（非扩展）保留 jump 帧前此长度（助跑+跳），
    /// 行走部分已由玩家手动完成——与 RecorderService 录制侧 InputLeadMs 语义对齐。</summary>
    private const long TrimKeepBeforeJumpMs = 600;

    /// <summary>
    /// 时间线兼容：非扩展段（首 jump 帧 > LongPathJumpMs 的旧行走数据）在"玩家手动到位"流程下
    /// 行走部分已由玩家完成——截断到 jump 帧前 TrimKeepBeforeJumpMs（只保留助跑+跳），防止重复行走错位；
    /// 扩展段（Extended 标记 true，需完整复现行走）或短路径段原样返回。
    /// </summary>
    private List<InputFrame> TrimTimeline(List<InputFrame> inputs)
    {
        int jumpIdx = -1;
        for (int i = 0; i < inputs.Count; i++)
        {
            if (inputs[i].Jump)
            {
                jumpIdx = i;
                break;
            }
        }
        if (jumpIdx < 0)
            return inputs;

        var jumpAt = inputs[jumpIdx].TimeMs;
        if (jumpAt <= LongPathJumpMs)
            return inputs; // 短路径：原样

        // 旧长路径数据（非扩展段）：保留 jump 前 TrimKeepBeforeJumpMs（玩家已手动到位，行走部分丢弃）
        var cutAt = Math.Max(0, jumpAt - TrimKeepBeforeJumpMs);
        var result = new List<InputFrame>(inputs.Count);
        for (int i = 0; i < inputs.Count; i++)
        {
            if (inputs[i].TimeMs < cutAt)
                continue;
            result.Add(new InputFrame
            {
                TimeMs = inputs[i].TimeMs - cutAt,
                Left = inputs[i].Left,
                Forward = inputs[i].Forward,
                Turn = inputs[i].Turn,
                Jump = inputs[i].Jump,
                Yaw = inputs[i].Yaw
            });
        }
        PluginLog.Info($"ReplayEngine: 旧长路径段时间线截断（首 jump @{jumpAt}ms > 800ms）→ 保留 jump 前 {TrimKeepBeforeJumpMs}ms（{result.Count} 帧）——玩家已手动到位");
        return result;
    }

    private void ExecuteSegment(int index)
    {
        if (index > EndSegment)
        {
            PluginLog.Info("ReplayEngine: 到达终点 ✅");
            State = ReplayState.Success;
            Stop();
            return;
        }

        var seg = CurrentRoute!.Segments[index];
        CurrentSegment = index;
        _takeoffPos = seg.Takeoff;
        _landPos = seg.Land;

        // 移速状态校验（段前）：录制段需冲刺/慢跑/速行而玩家当前没有 → 进入 AwaitStatus 自动补/等待。
        // 返回 false = 已进入 AwaitStatus 或已按"仅提醒"放行（不再执行本段走位，等待状态就绪后重入）。
        if (!EnsureMoveStatus(seg, index))
            return;

        PluginLog.Info($"ReplayEngine: 执行段 {index}: 起跳({seg.TakeoffX:F2},{seg.TakeoffY:F2},{seg.TakeoffZ:F2}) → 落地({seg.LandX:F2},{seg.LandY:F2},{seg.LandZ:F2}) " +
                   $"StartSpeed={seg.StartSpeed:F2} TakeoffSpeed={seg.TakeoffSpeed:F2} jump@{(seg.HasTimeline ? seg.Inputs.FirstOrDefault(f => f.Jump)?.TimeMs ?? -1 : -1)}ms");

        if (seg.HasTimeline)
        {
            // 全量输入时间线模式：对齐预助跑起点（时间线起点 StartPos 后方的加速段起点）
            // → 转向到录制起始朝向 → 预助跑到录制起点速度 → 逐帧重放录制输入。
            // 录制时玩家落地速度连续（直接助跑），回放从静止起步会偏后 0.7~0.9m（jump 帧位置没到起跳点，
            // 极限距离跳不够）。预助跑 = 从 StartPos 后方 0.02×v² 处起步加速，经过 StartPos 时速度≈录制值，
            // 位置/速度双对齐后 jump 帧位置 ≈ 录制起跳点。
            _useTimeline = true;
            // 时间线兼容处理：旧长路径段（时间线含段间行走）在"玩家手动到位"流程下行走部分已由玩家完成，
            // 截断到 jump 帧前 600ms（只保留助跑+跳），防止重复行走错位；新路线（短路径）原样使用。
            _timeline = TrimTimeline(seg.Inputs);
            _timelineIdx = 0;
            _timelineStartYaw = seg.StartYaw;
            _takeoffYaw = seg.TakeoffYaw; // 起跳方向（非时间线模式的投影判定用；时间线模式由绝对朝向修正保证）
            _timelineHasYaw = _timeline.Count > 0 && _timeline[0].Yaw != null;
            _timelineEndMs = _timeline.Count > 0 ? _timeline[^1].TimeMs + 400 : 400; // 最后帧 + 落地缓冲
            // 预扫描第一个 jump 帧的相对时间（起跳前朝向锁定窗口用；无 jump = -1）
            _timelineJumpAtMs = -1;
            for (int i = 0; i < _timeline.Count; i++)
            {
                if (_timeline[i].Jump)
                {
                    _timelineJumpAtMs = _timeline[i].TimeMs;
                    break;
                }
            }
            _preRunSpeed = seg.StartSpeed;
            // 连续快跳补偿：jump 帧在时间线起点附近（< EarlyJumpMs）**且 jump 前无"提前冲刺段"**——
            // "落地即跳"段：时间线起点被 LandBufferMs 截掉落地后的加速段 → StartSpeed 采样失真≈0，
            // 但录制实际起跳有速度（落地速度连续）→ 用录制起跳速度做预助跑目标，回放补回加速。
            // "提前冲刺段"判定：jump 帧前 PreJumpLeadMs(150ms) 之前存在位移输入——排除 jump 帧本身及
            // 起跳瞬间的 W 输入（段16 型 jump@53ms 前 6ms 就有 F=1，但那是起跳瞬间的输入，不是冲刺段）。
            // 若有提前冲刺（如 jump@460ms 前 300ms 冲刺），时间线含完整加速段 → 不补偿（避免双重加速）。
            bool preJumpHasMove = false;
            for (int i = 0; i < _timeline.Count && _timeline[i].TimeMs < _timelineJumpAtMs - PreJumpLeadMs; i++)
            {
                if (MathF.Abs(_timeline[i].Forward) > 0.1f || MathF.Abs(_timeline[i].Left) > 0.1f)
                {
                    preJumpHasMove = true;
                    break;
                }
            }
            if (_timelineJumpAtMs >= 0 && _timelineJumpAtMs < EarlyJumpMs && !preJumpHasMove
                && seg.TakeoffSpeed > Service.Config.PreRunSpeedMin)
                _preRunSpeed = seg.TakeoffSpeed;
            // 预助跑起点 = StartPos 后方 d（沿 StartYaw 反向）。d = 0.02×v² 由实测加速曲线近似
            // （0.16m 助跑→3.04m/s、0.28m→4.16m/s 等，d≈0.02v²）；起点速度低（原地跳/微调跳）不预助跑。
            _preRunDist = _preRunSpeed > Service.Config.PreRunSpeedMin
                ? Service.Config.PreRunDistFactor * _preRunSpeed * _preRunSpeed
                : 0f;
            _preRunMatchStreak = 0;
            var backDir = new Vector3(-MathF.Sin(_timelineStartYaw), 0, -MathF.Cos(_timelineStartYaw));
            _runStartPos = seg.StartPos + backDir * _preRunDist; // 预助跑起点（含 0 位移 = 原时间线起点）
            _movement.MoveTo(_runStartPos, Service.Config.AlignTolerance);
            EnterState(ReplayState.NavToRunStart);
        }
        else if (seg.HasRunStart)
        {
            // 冲刺起点模式：走位到录制冲刺起点 → 面朝录制起跳朝向 → 冲刺到起跳点
            _useTimeline = false;
            _runStartPos = seg.RunStart;
            _takeoffYaw = seg.TakeoffYaw;
            _movement.MoveTo(_runStartPos, Service.Config.AlignTolerance);
            EnterState(ReplayState.NavToRunStart);
        }
        else
        {
            // 旧版路线（无冲刺起点数据）：回退为原地转向 + 朝落点方向助跑
            _useTimeline = false;
            _takeoffYaw = TakeoffYaw();
            _movement.SetDesiredFacing(_takeoffYaw);
            EnterState(ReplayState.TurnToTakeoff);
        }
    }

    private void EnterState(ReplayState state)
    {
        State = state;
        _stateEnteredAt = Environment.TickCount64;
        _walkFellSince = 0; // 重置摔落计时（防上一状态的高度差计时残留到新状态）
        if (state == ReplayState.InAir)
        {
            _wasDescending = false;
            _fallAccum = 0f;
        }
        if (state == ReplayState.TurnToTakeoff)
        {
            // 重置速度采样基准：静止确认/预助跑用 CurrentSpeed——若沿用段 0 时间线播放的过期基准
            // （几秒前的位置/时间），会算出虚假"残余速度"或让采样错乱（实测段 1 预助跑速度 0 嫌疑）。
            var p = Service.ObjectTable.LocalPlayer;
            if (p != null)
            {
                _prevFramePos = p.Position;
                _prevFrameTime = _stateEnteredAt;
            }
        }
    }

    /// <summary>
    /// 段前移速状态校验（起跳速度必须与录制一致——用户实测：带状态跳常速段会跳过头，小平台必跌）。
    /// 返回 true = 状态就绪/已按设置放行，可继续执行本段；false = 已进入 AwaitStatus 等待（或
    /// "仅提醒"模式已放行，无需等待）。
    /// 规则：
    ///   - 一致 → 直接放行；
    ///   - 录制常速、当前带状态 → 无法自动取消（慢跑 CanStatusOff=false、FFXIVClientStructs 无
    ///     RemoveStatus），仅聊天提醒（大缓冲平台可过、小平台会跌），玩家自行决定继续/终止；
    ///   - 录制冲刺、当前无 → 自动施放冲刺（需开关开启；CD 前置检查）；
    ///   - 录制慢跑/速行、当前无 → 有速行职业自动施放速行（同速即生效）；无速行职业施放冲刺后
    ///     等 20s 变慢跑（冲刺直接跳会过头，不能立即跳）；
    ///   - 录制慢跑/速行、当前冲刺 → 等冲刺效果结束变慢跑（≤20s，无需施放）；
    ///   - 自动释放开关关闭 → 全部仅提醒不施放。
    /// </summary>
    private bool EnsureMoveStatus(JumpSegment seg, int index)
    {
        // 玩家在常速冲突暂停中点「继续」：跳过本段状态检查（确认带状态继续，自担风险）
        if (_skipStatusOnce)
        {
            _skipStatusOnce = false;
            return true;
        }

        var segNo = index + 1;

        // 走路模式切换（与移速 buff 正交的输入模式，客户端本地状态——可自动切换，无需玩家手动）：
        // 目标段需慢走而当前不是 → 切慢走；目标不需慢走而当前慢走 → 切回跑步。
        // 走路限速主导起跳速度（0.74m 级超短跳），不匹配则跳距必偏。
        var needWalk = seg.MoveState == MoveState.Walk;
        if (needWalk != MoveStatusHelper.IsWalking)
        {
            if (!Service.Config.AutoCastMoveBuffs)
            {
                Service.ChatGui.PrintError($"段 {segNo} 需要{(needWalk ? "慢走" : "跑步")}状态——已关闭自动切换，请手动处理");
                PluginLog.Info($"ReplayEngine: 段 {segNo} 需切{(needWalk ? "慢走" : "跑步")}但自动释放已关闭，仅提醒");
                return true;
            }
            MoveStatusHelper.SetWalking(needWalk);
            ChatInfo($"段 {segNo} 已自动切{(needWalk ? "慢走" : "跑步")}");
            PluginLog.Info($"ReplayEngine: 段 {segNo} 自动切{(needWalk ? "慢走" : "跑步")}（Control.IsWalking）");
            // 走路切换：达标 = IsWalking 到位（不是 buff 状态）——达标后重入 ExecuteSegment 继续 buff 维度修正
            // （bug 修复：旧实现把 _awaitCastDone=true 且按 buff 达标判定，导致切回跑步后目标冲刺段
            //  永远不施放冲刺 → 6s 超时失败。实测 cast 3 施放正常、buff 50 出现，问题在状态机衔接）。
            _awaitIsWalkSwitch = true;
            _awaitNeedWalk = needWalk;
            _awaitTargetState = seg.MoveState;
            _awaitCastDone = false;
            _awaitLastCastAt = 0;
            _awaitCastWarned = false;
            _awaitTimeoutMs = AwaitStatusTimeoutBaseMs;
            EnterState(ReplayState.AwaitStatus);
            return false;
        }
        // 慢走段：走路限速主导起跳速度，buff 维度忽略（正交），直接放行
        if (seg.MoveState == MoveState.Walk)
            return true;

        var target = seg.MoveState;
        var current = MoveStatusHelper.DetectCurrentState();
        if (current == target)
        {
            // 速行自动续期：本段仍需慢跑/速行且速行将到期（剩余<3s）→ 自动续上。
            // 仅自动释放开启时；慢跑是永久状态无需续（PelotonRemainingSeconds 无速行时返回 MaxValue）。
            if (Service.Config.AutoCastMoveBuffs && target == MoveState.SlowBuff
                && MoveStatusHelper.PelotonRemainingSeconds() < 3f && MoveStatusHelper.CastPeloton())
                ChatInfo($"段 {segNo} 速行将到期，已自动续上");
            return true;
        }

        // 录制常速、当前带状态：无法自动取消（慢跑 CanStatusOff=false，实测 ExecuteCommand RemoveStatus
        // 也取消不了）→ 暂停等玩家处理（实测：仅提醒玩家来不及反应，且状态不一致必跳过头——直接暂停最稳）
        if (target == MoveState.None)
        {
            Service.ChatGui.PrintError($"段 {segNo} 录制为常速，当前带{MoveStatusHelper.StateName(current)}——已暂停，处理状态后点「继续」或「终止」");
            PluginLog.Info($"ReplayEngine: 段 {segNo} 常速段带状态 {MoveStatusHelper.StateName(current)}——暂停等待玩家处理");
            EnterState(ReplayState.PausedForStatus);
            return false;
        }

        // 自动释放关闭：仅提醒不施放（未处理则回放因起跳速度不匹配失败）
        if (!Service.Config.AutoCastMoveBuffs)
        {
            Service.ChatGui.PrintError($"段 {segNo} 需要{MoveStatusHelper.StateName(target)}状态，当前{MoveStatusHelper.StateName(current)}——已关闭自动释放，请手动处理");
            PluginLog.Info($"ReplayEngine: 段 {segNo} 缺 {MoveStatusHelper.StateName(target)}（当前 {MoveStatusHelper.StateName(current)}）——自动释放已关闭，仅提醒");
            return true;
        }

        // 进入状态修正等待：目标慢跑/速行且当前冲刺 → 等冲刺结束变慢跑（无需施放）
        _awaitIsWalkSwitch = false;
        _awaitTargetState = target;
        _awaitCastDone = current == MoveState.Sprint && target == MoveState.SlowBuff;
        _awaitLastCastAt = 0;
        _awaitCastWarned = false;
        // 超时：冲刺目标（施放即生效）6s；慢跑/速行目标 26s——可能走"冲刺等变慢跑"路径
        // （无速行职业：施放冲刺后等 20s 冲刺效果结束附加慢跑；当前冲刺等变慢跑同理）。
        _awaitTimeoutMs = target == MoveState.Sprint ? AwaitStatusTimeoutBaseMs : AwaitStatusSprintToJogMs;
        PluginLog.Info($"ReplayEngine: 段 {segNo} 状态修正开始：目标 {MoveStatusHelper.StateName(target)}（当前 {MoveStatusHelper.StateName(current)}）" +
                   $"{( _awaitCastDone ? "——等当前冲刺结束变慢跑" : "——将自动施放技能")}");
        EnterState(ReplayState.AwaitStatus);
        return false;
    }

    /// <summary>时间线模式转向目标朝向：新路线（有 Yaw）且无预助跑 → 直接转起跳朝向（外观预对齐，
    /// 起跳前朝向提前到位，避免时间线内大角度转向键盘转不完 → 起跳瞬间侧对/背对跳跃）；
    /// 其余（有预助跑 / 旧路线无 Yaw）→ 转时间线起点朝向（预助跑方向基准 / 相对转向基准）。</summary>
    /// <summary>长路径段判定（毫秒）：时间线中首 jump 帧晚于此值 = 时间线含段间行走（长路径分流段）——
    /// 转向目标用时间线起点朝向（行走从录制起点朝向开始，拐弯由绝对朝向修正复现）；
    /// 短路径段（jump 早）转起跳朝向（起跳前外观预对齐）。</summary>
    private const long LongPathJumpMs = 800;

    /// <summary>长路径段起跳前朝向锁定窗口（毫秒）：jump 帧前此窗口内目标朝向锁定为录制起跳朝向 TakeoffYaw
    /// （保证起跳瞬间朝向精确）；更早的行走阶段跟随录制插值朝向（复现拐弯，不被锁死成起跳朝向）。</summary>
    private const long TakeoffPreAlignMs = 250;

    /// <summary>等待玩家到位判定（米）：段间长距离时玩家与下一段起跳点 XZ 距离小于此值且 Y 匹配
    /// （|Y差| ≤ YAlignTolerance，默认 0.3m 路不平放宽）= 已到位 → 自动继续执行下一段（执行时 NavToRunStart 会精确对齐）。</summary>
    private const float AwaitArriveDist = 1.5f;

    /// <summary>等待稳定确认的帧位移阈值（米）：玩家每帧 XZ 位移超过此值 = 仍在移动（走路 ~0.025m/帧），重置计时。</summary>
    private const float AwaitStableMovePerCheck = 0.02f;

    /// <summary>等待玩家到位超时（毫秒，180s）：玩家走长路径/机关等待可能较久，超时兜底失败（防无限等）。</summary>
    private const long AwaitTimeoutMs = 180000;

    /// <summary>等待玩家到位稳定确认计时（毫秒时刻；0 = 未开始/已重置）：进入起跳点范围后
    /// 基本静止持续 AwaitStableMs 才确认到位——防"路过起跳点"误触发抢控制（玩家只是经过也会短暂进入范围）。</summary>
    private long _awaitStableSince;

    /// <summary>等待稳定确认的上一次玩家位置（帧位移检测：移动中重置计时）。</summary>
    private Vector3 _awaitLastPos;

    private float TimelineTurnTarget()
    {
        // 长路径段（时间线含行走）→ 转时间线起点朝向（行走起点），拐弯由时间线复现
        if (_timelineHasYaw && _timelineJumpAtMs > LongPathJumpMs)
            return _timelineStartYaw;
        // 短路径段：新路线（有 Yaw）且无预助跑 → 直接转起跳朝向（起跳前朝向提前到位，外观正常）
        if (_timelineHasYaw && _preRunDist <= 0f)
            return _takeoffYaw;
        return _timelineStartYaw;
    }

    /// <summary>启动时间线重放（预助跑达标或起点静止直接重放共用）。</summary>
    private void StartTimelineReplay()
    {
        _movement.SetForward(false);
        // 站定快进：录制时起跳前玩家可能站着（无移动输入）等待——回放无需按录制节奏干等站定期，
        // 快进到第一个"有位移输入"的帧（保留 100ms 衔接缓冲）。只跳完全静止段（无位移=跳过无影响；
        // 起跳前朝向由锁定保证，无需站定期转向）。连续快跳段（jump 帧≈0ms）无站定期，不快进。
        // 阈值 0.1：玩家站定期微调（Forward<0.1 的极轻移动）也被跳过，位移 <0.05m 由落地走位吸收。
        _timelineSkipToMs = 0;
        for (int i = 0; i < _timeline.Count; i++)
        {
            var f = _timeline[i];
            if (MathF.Abs(f.Forward) > 0.1f || MathF.Abs(f.Left) > 0.1f)
            {
                _timelineSkipToMs = Math.Max(0, f.TimeMs - 100);
                break;
            }
        }
        // 保护：快进不越过第一个 jump 帧（保留起跳前锁定段；jump 边沿必须由 while 推进触发）
        if (_timelineJumpAtMs >= 0)
            _timelineSkipToMs = Math.Min(_timelineSkipToMs, _timelineJumpAtMs);
        if (_timelineSkipToMs > 0)
            PluginLog.Info($"ReplayEngine: 站定快进 {_timelineSkipToMs}ms（起跳前无移动输入段）");
        _timelineStart = Environment.TickCount64 - _timelineSkipToMs;
        _timelineIdx = 0;
        _lastTimelineJump = false;
        _jumpHoldUntil = 0;
        _prevFrameY = Service.ObjectTable.LocalPlayer?.Position.Y ?? 0f; // 落地检测帧间 Y 基准
        EnterState(ReplayState.TimelinePlay);
    }

    /// <summary>帧间 XZ 速度（预助跑达标检测用；空中帧 Y 变化不计入）。</summary>
    private float CurrentSpeed(Vector3 pos, long now)
    {
        var dt = (now - _prevFrameTime) / 1000.0;
        _prevFrameTime = now;
        if (dt <= 0)
            return 0f;
        var d = pos - _prevFramePos;
        d.Y = 0;
        _prevFramePos = pos;
        return d.Length() / (float)dt;
    }

    // ===== 帧驱动 =====

    private void Tick(IFramework framework)
    {
        if (State == ReplayState.Idle)
            return;

        var player = Service.ObjectTable.LocalPlayer;
        if (player == null)
        {
            Stop();
            return;
        }

        var now = Environment.TickCount64;
        var deltaY = player.Position.Y - _prevY;
        _prevY = player.Position.Y;

        switch (State)
        {
            case ReplayState.NavToRunStart:
                if (now - _stateEnteredAt > NavToRunStartTimeoutMs)
                {
                    PluginLog.Error("ReplayEngine: 走位到冲刺起点超时");
                    Fail();
                    break;
                }
                // 落地硬直/输入禁用：走位注入会被游戏忽略（玩家不动）——等待输入恢复再推进
                // （连续跳根因：A→B 落地瞬间 B→C 段走位注入无效 → 位置不动/残留 → 起跳速度错）。
                // 注意：超时检查在上方（等待 break 不能拦截超时——否则输入恒不可用时无限卡死，实测段1）。
                if (!_movement.LastInputAllowed)
                {
                    if (DebugTools.DiagPreRun && now - _lastInputGateDiagMs > 3000)
                    {
                        _lastInputGateDiagMs = now;
                        var g = _movement.LastInputGate;
                        PluginLog.Info($"ReplayEngine: 走位等待输入恢复（additive={g.AdditiveUnk} e1={g.Enable1} e2={g.Enable2} 已等 {now - _stateEnteredAt}ms）");
                    }
                    break;
                }

                // 诊断：走位进度（排查"走位不执行"——段16→17 玩家落地后未移动到下一段起点）
                if (DebugTools.DiagWalk && now - _lastWalkDiagMs > 200)
                {
                    _lastWalkDiagMs = now;
                    var dd = _runStartPos - player.Position;
                    dd.Y = 0;
                    PluginLog.Info($"ReplayEngine: [走位诊断] 到起点 位置({player.Position.X:F2},{player.Position.Y:F2},{player.Position.Z:F2}) " +
                               $"距目标 {dd.Length():F2}m IsMoveArrived={_movement.IsMoveArrived}");
                }

                // 摔落/平台不符检测：走位中高度差持续超阈值 = 玩家摔落（实测段21 走位对齐摔落 27m 仍被拉向目标）→ 止损
                if (CheckWalkFell(player.Position, _runStartPos, now))
                    break;

                // 冲刺起点/时间线起点必须精确到达（对齐容差）——它决定起跳位置，任何可见偏差都会偏离录制路径。
                // IsMoveArrived 兜底：MoveTo 低速逼近锁定到位即继续（防止全速越过最近点后 IsNear 不满足卡死）
                if (_movement.IsMoveArrived || IsNear(player.Position, _runStartPos, Service.Config.AlignTolerance))
                {
                    _movement.ReleaseAll();
                    if (_useTimeline)
                    {
                        // 时间线模式：转向到时间线基准朝向再重放。
                        // 目标 = 新路线（有 Yaw）且无预助跑 → 直接转起跳朝向（起跳前朝向提前到位，
                        // 避免时间线内大角度转向受键盘转向速度限制转不完 → 起跳瞬间侧对/背对跳跃的外观异常）；
                        // 其余（有预助跑 / 旧路线无 Yaw）→ 转时间线起点朝向（预助跑方向基准 / 相对转向基准）。
                        // 注：不做"走位中并行转向"——standard 模式下转向注入会让移动方向跟随旋转中的朝向，
                        // 走位路径扭曲、IsMoveArrived 最近点锁定误判到位 → 预助跑起点错（实测段16 起跳偏 0.7m）。
                        _movement.SetDesiredFacing(TimelineTurnTarget());
                        EnterState(ReplayState.TurnToTakeoff);
                    }
                    else
                    {
                        _movement.SetDesiredFacing(_takeoffYaw);
                        EnterState(ReplayState.TurnToTakeoff);
                    }
                }
                break;

            case ReplayState.PreRunToSpeed:
            {
                // 落地硬直/输入禁用：SetForward 注入会被游戏忽略（速度 0）——等待输入恢复再助跑
                // （连续跳"从 B 开始 100% 失败"根因：B 落地硬直期预助跑注入无效 → 速度 0 → 未移动失败）。
                // 超时检查必须可达（等待 break 放在超时之后——否则输入恒不可用时无限卡死，实测段1）。
                if (!_movement.LastInputAllowed)
                {
                    if (DebugTools.DiagPreRun && now - _lastInputGateDiagMs > 3000)
                    {
                        _lastInputGateDiagMs = now;
                        var g = _movement.LastInputGate;
                        PluginLog.Info($"ReplayEngine: 预助跑等待输入恢复（additive={g.AdditiveUnk} e1={g.Enable1} e2={g.Enable2} 已等 {now - _stateEnteredAt}ms）");
                    }
                    if (now - _stateEnteredAt > PreRunTimeoutMs)
                    {
                        PluginLog.Error("ReplayEngine: 预助跑超时（等待输入恢复）");
                        Fail();
                    }
                    break;
                }
                // 外观预对齐：预助跑期间持续转向到录制起跳朝向 TakeoffYaw——键盘转向慢，不能等时间线里再转，
                // 提前把朝向转好，起跳瞬间面朝跳跃方向（外观正常，不侧对/背对）。
                // 移动方向由 WorldMoveYaw 锁定时间线起点朝向 StartYaw（解耦，不受角色朝向影响）→ 助跑方向正确。
                if (_timelineHasYaw)
                {
                    var dTurn = NormalizeAngle(_takeoffYaw - player.Rotation);
                    var turn = MathF.Abs(dTurn) <= YawCorrectionDeadzone
                        ? 0f
                        : Math.Clamp(dTurn / TimelineTurnFullAngle, -1f, 1f);
                    _movement.TimelineInput = (0f, 1f, turn);
                    _movement.WorldMoveYaw = _timelineStartYaw;
                }

                // 沿录制起始朝向助跑，XZ 速度达到录制起点速度 → 开始时间线重放。
                // 速度达标是闭环触发（无论加速曲线实际如何），位置偏差仅剩"预助跑起点后移量的模型误差"，
                // 由落地走位对齐（LandWalkDist）吸收。
                var speed = CurrentSpeed(player.Position, now);
                // 达标判定：1 帧达标（阈值 0.95 已贴近目标）。原"连续 2 帧"会因速度采样抖动
                // （注入满幅加速 + 帧间位置量化，目标速度附近 ±0.1~0.3 抖动）断断续续错过触发，
                // 实际 2.96（超 23%）才达标，时间线 0~1ms 助跑输入再加速 → jump 帧 3.35 超调 39%
                // （实测段 1 跳过头 0.49~0.53m 掉平台）。1 帧达标 + 收紧阈值：触发速度 ≈ 目标×0.95~1.1，
                // 残余偏差由落地走位对齐吸收（LandWalkDist 2.5m）。
                if (speed >= _preRunSpeed * Service.Config.SpeedMatchTolerance)
                    _preRunMatchStreak++;
                else
                    _preRunMatchStreak = 0;

                if (_preRunMatchStreak >= 1)
                {
                    PluginLog.Info($"ReplayEngine: 预助跑达标 速度 {speed:F2}m/s（目标 {_preRunSpeed:F2}m/s）→ 时间线重放");
                    StartTimelineReplay();
                    break;
                }

                // 兜底：沿 StartYaw 已越过 StartPos 前方 Overshoot 仍未达标（加速比预期慢/被阻挡）→ 直接开始，
                // 宁可略偏（由落地走位吸收），不无限等待。
                // 但速度过低（< PreRunMinSpeedToStart）= 玩家根本没跑动（卡住/输入未生效/移动平台漂移）——
                // 零速度起跳落点必偏（实测段 48：速度 0.00 越过起点 0.53m → 起跳偏 0.59m、落点偏 0.93m），
                // 此时不盲跳，止损失败并给出诊断。
                var startPos = _runStartPos + new Vector3(MathF.Sin(_timelineStartYaw), 0, MathF.Cos(_timelineStartYaw)) * _preRunDist;
                var fwd = new Vector3(MathF.Sin(_timelineStartYaw), 0, MathF.Cos(_timelineStartYaw));
                var overshoot = Vector3.Dot(player.Position - startPos, fwd);
                if (overshoot > Service.Config.PreRunOvershoot)
                {
                    if (speed < PreRunMinSpeedToStart)
                    {
                        // 全量诊断（"走两步不动"：位置在动但速度采样 0——完整几何一次拿全）
                        var diagD = player.Position - _prevFramePos;
                        diagD.Y = 0;
                        PluginLog.Error($"ReplayEngine: 预助跑未移动（速度 {speed:F2}m/s，越过起点 {overshoot:F2}m）→ 放弃本段。" +
                                   $"诊断: pos=({player.Position.X:F2},{player.Position.Y:F2},{player.Position.Z:F2}) " +
                                   $"runStart=({_runStartPos.X:F2},{_runStartPos.Y:F2},{_runStartPos.Z:F2}) " +
                                   $"startPos=({startPos.X:F2},{startPos.Y:F2},{startPos.Z:F2}) preRunDist={_preRunDist:F2} " +
                                   $"yaw={_timelineStartYaw:F3} prevFrame=({_prevFramePos.X:F2},{_prevFramePos.Z:F2}) " +
                                   $"d={diagD.Length():F3}m dt={(now - _prevFrameTime) / 1000.0 * 1000:F0}ms");
                        Fail();
                        break;
                    }
                    PluginLog.Info($"ReplayEngine: 预助跑未达标但已越过起点 {overshoot:F2}m（速度 {speed:F2}m/s < {_preRunSpeed:F2}m/s）→ 直接开始");
                    StartTimelineReplay();
                    break;
                }

                if (now - _stateEnteredAt > PreRunTimeoutMs)
                {
                    PluginLog.Error($"ReplayEngine: 预助跑超时（速度 {speed:F2}m/s < {_preRunSpeed:F2}m/s）");
                    Fail();
                }
                break;
            }

            case ReplayState.TimelinePlay:
            {
                // 逐帧重放录制输入（按相对时间推进）——时间戳起跳：jump 帧到点即按空格
                var elapsed = now - _timelineStart;
                var count = _timeline.Count;
                // 帧间 Y（落地提前检测用）
                var frameDeltaY = player.Position.Y - _prevFrameY;
                _prevFrameY = player.Position.Y;
                // 起跳速度诊断：帧间 XZ 位移速度（jump 帧瞬间实际速度 vs 录制 TakeoffSpeed——
                // 定位"起跳位置准但落点系统性偏短"：起跳速度不足 or 物理差异）
                var playSpeed = 0f;
                if (_playPrevTime > 0)
                {
                    var dp = player.Position - _playPrevPos;
                    dp.Y = 0;
                    var dts = (now - _playPrevTime) / 1000.0;
                    if (dts > 0)
                        playSpeed = dp.Length() / (float)dts;
                }
                _playPrevPos = player.Position;
                _playPrevTime = now;

                // 边沿推进 + jump 检测（逐帧，任何帧率下不漏跳）
                while (_timelineIdx < count && _timeline[_timelineIdx].TimeMs <= elapsed)
                {
                    var f = _timeline[_timelineIdx];
                    // jump 边沿（false→true）：记录按下保持截止时刻（120ms 保险，防 down 边沿漏检）
                    if (f.Jump && !_lastTimelineJump)
                    {
                        _jumpHoldUntil = now + 120;
                        // 起跳诊断：边沿帧对比回放位置 vs 录制起跳点（时间戳起跳的位置偏差量化）+ 朝向偏差
                        // + 起跳瞬间速度 vs 录制 TakeoffSpeed（定位落点系统性偏短：速度不足 or 物理差异）
                        var dPos = player.Position - _takeoffPos;
                        dPos.Y = 0;
                        var yawDelta = NormalizeAngle(player.Rotation - _takeoffYaw);
                        PluginLog.Info($"ReplayEngine: 起跳帧 @{f.TimeMs}ms 位置({player.Position.X:F2},{player.Position.Y:F2},{player.Position.Z:F2}) " +
                                   $"录制起跳点({_takeoffPos.X:F2},{_takeoffPos.Y:F2},{_takeoffPos.Z:F2}) 偏差 {dPos.Length():F2}m " +
                                   $"朝向差 {MathF.Abs(yawDelta) * 180f / MathF.PI:F2}°（录制 {_takeoffYaw:F3} vs 当前 {player.Rotation:F3}）" +
                                   $"起跳速度 {playSpeed:F2}m/s（录制 {CurrentRoute!.Segments[CurrentSegment].TakeoffSpeed:F2}m/s）");
                    }
                    _lastTimelineJump = f.Jump;
                    _timelineIdx++;
                }

                if (_timelineIdx >= count)
                {
                    // 所有帧播完 → 立即停止输入注入（防止最后帧前进输入在落地缓冲期把角色推出平台——
                    // 录制落地帧玩家可能还按着前进，若保持到 _timelineEndMs 会带着前进冲出落点）
                    _movement.TimelineInput = null;
                    _movement.WorldMoveYaw = null;
                    _jump.SetHeld(false);
                }
                else
                {
                    // 插值注入：每帧注入"elapsed 时刻"的插值输入（Left/Forward/Turn 线性插值、
                    // Yaw 短路径插值）——消除帧率量化：不同帧率下注入的输入曲线一致 → 回放路径一致
                    // （旧逻辑每帧只注入"推进到的最后一帧"的原始值，帧率不同 → 输入时间量化不同 → 路径波动）。
                    InputFrame prev, next;
                    float t;
                    if (_timelineIdx == 0)
                    {
                        prev = next = _timeline[0];
                        t = 1f;
                    }
                    else
                    {
                        prev = _timeline[_timelineIdx - 1];
                        next = _timeline[_timelineIdx];
                        var span = next.TimeMs - prev.TimeMs;
                        t = span > 0 ? Math.Clamp((float)(elapsed - prev.TimeMs) / span, 0f, 1f) : 1f;
                    }

                    var left = prev.Left + (next.Left - prev.Left) * t;
                    var fwd = prev.Forward + (next.Forward - prev.Forward) * t;
                    if (next.Yaw is { } nextYaw)
                    {
                        // 朝向目标：
                        //  - 短路径段（jump 帧早，起跳前即直线冲刺）：整个起跳前锁定录制起跳朝向 TakeoffYaw
                        //    （朝向=冲刺方向=TakeoffYaw，锁定保证起跳瞬间朝向精确、外观正常）——原逻辑；
                        //  - 长路径段（jump 帧晚，起跳前是段间行走+拐弯）：行走阶段跟随录制插值朝向
                        //    （拐弯/转向逐帧复现——否则行走方向被锁死成起跳朝向 → WorldMoveYaw 恒定 → 走反，
                        //    实测段5→6 起跳帧偏差 51.73m），仅起跳前 TakeoffPreAlignMs 窗口锁定 TakeoffYaw
                        //    （保证起跳瞬间朝向精确）。
                        // 起跳后（空中）跟随插值录制 Yaw——FF14 空中转向带动轨迹弯曲（弯曲量≈转向量，
                        // 跳跳乐空中调整落点技巧），回放跟随录制转向即复现弯曲，落点=录制落点。
                        var preLock = _timelineJumpAtMs >= 0 && elapsed < _timelineJumpAtMs
                                      && (_timelineJumpAtMs <= LongPathJumpMs
                                          || elapsed >= _timelineJumpAtMs - TakeoffPreAlignMs);
                        var targetYaw = preLock
                            ? _takeoffYaw
                            : (prev.Yaw is { } prevYaw ? LerpYaw(prevYaw, nextYaw, t) : nextYaw);
                        var delta = NormalizeAngle(targetYaw - player.Rotation);
                        // 转向注入（仅外观，尽力跟随；死区 YawCorrectionDeadzone，满值角 15° 更快收敛）。
                        // 空中（elapsed ≥ jump 帧）转向不影响轨迹 → 全速转向（±1）快速把外观转正——
                        // 录制鼠标转向瞬时完成，回放键盘转向慢，地面段受收敛限制，空中放开补转。
                        var turn = MathF.Abs(delta) <= YawCorrectionDeadzone
                            ? 0f
                            : (elapsed >= _timelineJumpAtMs
                                ? MathF.Sign(delta)
                                : Math.Clamp(delta / TimelineTurnFullAngle, -1f, 1f));
                        _movement.TimelineInput = (left, fwd, turn);
                        // 移动方向解耦只用于地面（起跳前）：目标朝向 + 录制输入相对角 = 录制移动方向，
                        // 不受角色转向速度限制——起跳矢量精确等于录制值（地面转向慢不再拖累起跳方向）。
                        // 空中（elapsed ≥ jump 帧）必须清 WorldMoveYaw、恢复相对输入（left/fwd 相对身体朝向）：
                        // FF14 空中"按住前进 + 转向"会把速度矢量拉向身体朝向 → 轨迹弯曲（弯曲量≈空中转向量，
                        // 跳跳乐空中调整落点技巧，实测段2 录制空中转 47°→落点偏 47°、段12 转 99°→偏 99°）。
                        // 世界方向注入在空中不产生弯曲（空中移动输入仅相对身体的 W/A/D 语义生效）——
                        // 19:23 空中用 WorldMoveYaw 后弯曲缺失（段2 落点横向偏 0.94m），19:08 相对注入时弯曲正常（段12 0.15m）。
                        var moveRel = MathF.Atan2(left, fwd);
                        _movement.WorldMoveYaw = elapsed >= _timelineJumpAtMs
                            ? null
                            : NormalizeAngle(targetYaw + moveRel);
                        // 调试：起跳前移动轨迹（侧移问题定位——段18 型：录制侧移分量回放疑似丢失）
                        if (DebugTools.DiagTimeline && elapsed < _timelineJumpAtMs && elapsed % 100 < 20)
                        {
                            PluginLog.Info($"ReplayEngine: 起跳前 @{elapsed}ms 位置({player.Position.X:F2},{player.Position.Y:F2},{player.Position.Z:F2}) " +
                                       $"面向 {player.Rotation:F3} 注入 L={left:F2} F={fwd:F2} T={turn:F2} " +
                                       $"WorldYaw={( _movement.WorldMoveYaw is { } wy ? wy.ToString("F3") : "null")}");
                        }
                    }
                    else
                    {
                        // 旧路线（无 Yaw）：相对转向线性插值
                        var turn = prev.Turn + (next.Turn - prev.Turn) * t;
                        _movement.TimelineInput = (left, fwd, turn);
                        _movement.WorldMoveYaw = null;
                    }
                    // jump：边沿后保持按下 120ms（游戏对持续 down 去重，只起跳一次），之后松开
                    _jump.SetHeld(now < _jumpHoldUntil);
                }

                // 落地提前结束：录制时间线含"落地后冗余帧"（玩家落地后继续操作直到段结束，每段 0.4~0.7s），
                // 回放复现到落地即可进下一段。检测：jump 后 ≥250ms（滞空下限，防中间平台误判）
                // + XZ 距录制落点 <1m + |Y-落点Y|≤0.6m + 单帧下降回稳（deltaY ≥ -0.02）。
                if (_timelineJumpAtMs >= 0 && elapsed >= _timelineJumpAtMs + 250)
                {
                    var dXZ = player.Position - _landPos;
                    dXZ.Y = 0;
                    if (dXZ.LengthSquared() < 1f
                        && MathF.Abs(player.Position.Y - _landPos.Y) <= 0.6f
                        && frameDeltaY >= -0.02f)
                    {
                        _movement.TimelineInput = null;
                        _movement.WorldMoveYaw = null;
                        _jump.SetHeld(false);
                        _movement.ReleaseAll();
                        PluginLog.Info($"ReplayEngine: 检测到落地（jump 后 {elapsed - _timelineJumpAtMs}ms）→ 提前结束时间线进落地校验");
                        EnterState(ReplayState.Land);
                        break;
                    }
                }

                // 时间线重放超时兜底（必须在播完判定之前——原 else-if 位置恒不可达：
                // elapsed = 已播时长+skipToMs ≥ 已播时长，播完判定 elapsed > _timelineEndMs 先触发，
                // 超时分支永不执行；若落地检测/播完均未触发（jump 帧缺失/卡空中）将无限重放）。
                if (now - _stateEnteredAt > RunToTakeoffTimeoutMs + _timelineEndMs)
                {
                    PluginLog.Error("ReplayEngine: 时间线重放超时");
                    Fail();
                    break;
                }

                // 播完（含落地缓冲）→ 停止注入 → 落地校验
                if (elapsed > _timelineEndMs)
                {
                    _movement.TimelineInput = null;
                    _movement.WorldMoveYaw = null;
                    _jump.SetHeld(false);
                    _movement.ReleaseAll();
                    PluginLog.Info($"ReplayEngine: 时间线播完（{count} 帧）→ 落地校验");
                    EnterState(ReplayState.Land);
                }
                break;
            }

            case ReplayState.TurnToTakeoff:
                // 不等待 LastInputAllowed（实测段 1：落地站定后门控误报 false → 每帧 break、连超时都到不了，
                // 玩家朝向已对准录制朝向却无限卡死，20.8s 后输入恢复才进超时检查失败）。
                // 转向注入在硬直期被游戏忽略无害（朝向不变，3s 超时兜底）；rmiWalkIsInputEnabled 疑似
                // "移动中才 true"（走位时注入正常、站定转向卡死）——用 LastInputGate 诊断分量确认。
                var turnTarget = _useTimeline ? TimelineTurnTarget() : _takeoffYaw;
                if (now - _stateEnteredAt > TurnTimeoutMs)
                {
                    PluginLog.Error($"ReplayEngine: 转向超时 当前朝向 {player.Rotation:F3} 目标 {turnTarget:F3} " +
                               $"差 {NormalizeAngle(player.Rotation - turnTarget):F3} rad（注入可能被玩家输入/移动模式干扰）");
                    Fail();
                    break;
                }

                if (IsFacing(player.Rotation, turnTarget))
                {
                    if (_useTimeline)
                    {
                        if (_preRunDist > 0f)
                        {
                            // 预助跑前位置校验：玩家必须接近预助跑起点（StartPos 后方 d 处）。
                            // 防两类带偏进预助跑（实测段 48：进入时已在起点前方 0.53m → 速度 0 + overshoot
                            // 第一帧触发兜底 → jump 帧偏 0.59m 起跳、落点偏 0.93m）：
                            //   1) NavToRunStart 走位误判到位（IsMoveArrived 最近点锁定，已知坑）；
                            //   2) 玩家站在移动平台上被平台漂移带走（转向期间不注入移动）。
                            // 偏差超 0.3m → 重新走位一次（二次仍偏由 NavToRunStart 超时/二次校验兜底失败）。
                            var dRun = player.Position - _runStartPos;
                            dRun.Y = 0;
                            if (dRun.Length() > PreRunStartAlignTolerance)
                            {
                                PluginLog.Info($"ReplayEngine: 预助跑起点偏差 {dRun.Length():F2}m（目标 {_runStartPos.X:F2},{_runStartPos.Z:F2}，" +
                                           $"当前 {player.Position.X:F2},{player.Position.Z:F2}）——走位未到位或平台漂移，重新走位");
                                _movement.MoveTo(_runStartPos, Service.Config.AlignTolerance);
                                EnterState(ReplayState.NavToRunStart);
                                break;
                            }

                            // 预助跑前静止确认：落地滑行/走位残余速度会污染预助跑——滑过起点后停稳，
                            // overshoot 初始即 >0.5 且速度 0 → 被"未移动"兜底误杀（实测段 1 走两步不动）。
                            // 速度归零后才 SetForward 起步（预助跑必须从静止开始，与起点速度对齐闭环一致）。
                            var slideSpeed = CurrentSpeed(player.Position, now);
                            if (slideSpeed > PreRunStartSpeedTol)
                            {
                                if (DebugTools.DiagPreRun && now - _preRunDiagMs > 2000)
                                {
                                    _preRunDiagMs = now;
                                    PluginLog.Info($"ReplayEngine: 预助跑前等待静止（残余速度 {slideSpeed:F2}m/s，超 {PreRunStartSpeedTol:F2} 阈值）");
                                }
                                break; // 保持 TurnToTakeoff，下一帧再检查（转向已对准，超时由 TurnTimeoutMs 兜底）
                            }

                            // 预助跑：沿录制起始朝向冲刺，速度达到录制起点速度 → 开始时间线重放
                            // （位置/速度双对齐，jump 帧位置 ≈ 录制起跳点，极限距离不再偏后）
                            _movement.SetForward(true);
                            _prevFramePos = player.Position;
                            _prevFrameTime = now;
                            PluginLog.Info($"ReplayEngine: 预助跑开始 目标速度 {_preRunSpeed:F2}m/s 起点后方 {_preRunDist:F2}m");
                            EnterState(ReplayState.PreRunToSpeed);
                        }
                        else
                        {
                            // 起点静止（原地跳/微调跳）：直接按时间重放
                            StartTimelineReplay();
                        }
                    }
                    else
                    {
                        // 转向完成：沿录制起跳朝向冲刺，接近起跳点瞬间起跳
                        _movement.SetForward(true);
                        EnterState(ReplayState.RunToTakeoff);
                    }
                }
                break;

            case ReplayState.RunToTakeoff:
                if (now - _stateEnteredAt > RunToTakeoffTimeoutMs)
                {
                    PluginLog.Error("ReplayEngine: 助跑/接近起跳点超时");
                    Fail();
                    break;
                }

                if (CurrentRoute == null)
                {
                    // 单跳/标定：跑足距离（标定 runDist）或默认时长（单跳 800ms）后起跳
                    var enough = _calibrationMode && _calibrationRunDist > 0
                        ? XZDistance(player.Position, _takeoffPos) >= _calibrationRunDist
                        : now - _stateEnteredAt >= RunupMs;
                    if (enough)
                    {
                        _takeoffY = player.Position.Y;
                        _jump.Jump();
                        EnterState(ReplayState.Jump);
                    }
                }
                else if (IsPastTakeoff(player.Position, _takeoffPos))
                {
                    // 段回放：冲刺经过录制起跳点（投影判定，容差收紧）→ 跑动中起跳
                    PluginLog.Info($"ReplayEngine: 起跳点 ({_takeoffPos.X:F2},{_takeoffPos.Y:F2},{_takeoffPos.Z:F2}) 当前 ({player.Position.X:F2},{player.Position.Y:F2},{player.Position.Z:F2})");
                    _takeoffY = player.Position.Y;
                    _jump.Jump();
                    EnterState(ReplayState.Jump);
                }
                break;

            case ReplayState.Jump:
                if (player.Position.Y - _takeoffY > Service.Config.TakeoffDeltaY)
                {
                    EnterState(ReplayState.InAir);
                }
                else if (now - _stateEnteredAt > JumpTimeoutMs)
                {
                    PluginLog.Error("ReplayEngine: 起跳超时（跳跃未触发）");
                    Fail();
                }
                break;

            case ReplayState.InAir:
                // 空中刹停：助跑注入（_forwardHeld）在滞空期间持续推着玩家走（空中移动有效），
                // 触地后 1-2 帧才 ReleaseAll → 落地后继续向前走几步（实测 calib 0.1 落地走出平台）。
                // 非时间线路径（单跳/标定/旧路线）空中无需移动注入，进入即释放；时间线模式的空中
                // 由 TimelineInput 注入（不受 _forwardHeld 影响），不经过本分支。
                _movement.ReleaseAll();
                if (deltaY < 0)
                    _fallAccum += -deltaY;
                if (!_wasDescending && _fallAccum >= Service.Config.DescendAccumY)
                    _wasDescending = true;

                if (_wasDescending && deltaY >= -Service.Config.DescendEndDeltaY)
                {
                    _movement.ReleaseAll();
                    _jump.Stop();
                    _wasDescending = false;
                    _fallAccum = 0f;
                    EnterState(ReplayState.Land);
                }
                else if (now - _stateEnteredAt > InAirTimeoutMs)
                {
                    PluginLog.Error("ReplayEngine: 滞空超时");
                    Fail();
                }
                break;

            case ReplayState.Land:
            {
                var tolerance = Service.Config.LandTolerance;
                var d = _landPos - player.Position;
                DistanceToTarget = d.Length(); // 3D 距离（含高度——高度不对=掉层，必须计入）
                PluginLog.Info($"ReplayEngine: 落地距目标 {DistanceToTarget:F2}m 落地({player.Position.X:F2},{player.Position.Y:F2},{player.Position.Z:F2}) 目标({_landPos.X:F2},{_landPos.Y:F2},{_landPos.Z:F2})");

                if (_calibrationMode)
                {
                    // 标定/链跳报告"起跳点→落点"的实际水平跳距（目标点仅用于流程，不作判据）
                    var jumpD = player.Position - _takeoffPos;
                    jumpD.Y = 0;
                    if (_chainMode)
                    {
                        if (_chainJumps == 0)
                        {
                            // 第 1 跳完成 → 落地瞬间立刻第 2 跳（连跳：不等待，落地即助跑）
                            _chainFirstDist = jumpD.Length();
                            _chainJumps = 1;
                            _takeoffPos = player.Position;   // 第 2 跳起跳点 = 第 1 跳落点
                            _takeoffY = player.Position.Y;
                            PluginLog.Info($"ReplayEngine: 链跳 第1跳 {_chainFirstDist:F2}m → 立即第 2 跳（连跳，助跑 {_calibrationRunDist:F2}m）");
                            _movement.SetForward(true);
                            EnterState(ReplayState.RunToTakeoff);
                        }
                        else
                        {
                            var d2 = jumpD.Length();
                            PluginLog.Info($"ReplayEngine: 链跳测试完成 → 第1跳(静止起步) {_chainFirstDist:F2}m  第2跳(连跳) {d2:F2}m  差 {d2 - _chainFirstDist:+.2F}m" +
                                       $"（{(d2 > _chainFirstDist + 0.1f ? "连跳获得落地速度加成 ✓" : "无明显加成")}）");
                            Stop();
                        }
                        break;
                    }
                    PluginLog.Info($"ReplayEngine: 标定完成 → 跳跃距离 {jumpD.Length():F2}m（助跑{_calibrationRunDist:F2}m）");
                    Stop();
                    break;
                }

                if (DistanceToTarget <= tolerance)
                {
                    GoNextSegment();
                }
                else if (DistanceToTarget <= Service.Config.LandWalkDist)
                {
                    PluginLog.Info($"ReplayEngine: 落点偏离 {DistanceToTarget:F2}m，走位对齐（≤{Service.Config.LandWalkDist:F1}m 自动对齐，非失败）");
                    _movement.MoveTo(_landPos, Service.Config.AlignTolerance);
                    EnterState(ReplayState.WalkAdjust);
                }
                else
                {
                    // 落点偏离过远（掉出平台/大幅偏离）：回起跳点重跳大概率路径不可达（跳跳乐地形），
                    // 只会造成无意义的异常移动——直接失败，由玩家读档/手动处理
                    PluginLog.Error($"ReplayEngine: 落点偏离过远 {DistanceToTarget:F2}m（>{Service.Config.LandWalkDist:F1}m），回跳无意义，回放失败");
                    Fail();
                }
                break;
            }

            case ReplayState.WalkAdjust:
                if (now - _stateEnteredAt > WalkAdjustTimeoutMs)
                {
                    PluginLog.Error("ReplayEngine: 走位对齐超时");
                    Fail();
                    break;
                }

                // 诊断：走位进度（排查"走位不执行"——段16→17 玩家落地后未移动到下一段起点）
                if (DebugTools.DiagWalk && now - _lastWalkDiagMs > 200)
                {
                    _lastWalkDiagMs = now;
                    var dd = _landPos - player.Position;
                    dd.Y = 0;
                    PluginLog.Info($"ReplayEngine: [走位诊断] 对齐 位置({player.Position.X:F2},{player.Position.Y:F2},{player.Position.Z:F2}) " +
                               $"距目标 {dd.Length():F2}m IsMoveArrived={_movement.IsMoveArrived}");
                }

                // 摔落/平台不符检测：走位对齐中玩家摔落（实测段21 落点偏 1.03m 走位对齐时从平台摔落，
                // Y 65→37 掉 27.6m，插件仍注入 8 秒把玩家拉向落点 → 与玩家手动走回互相拉扯，且 XZ 到位后
                // 从错误高度执行下一段）→ 检测到立即释放控制并失败
                if (CheckWalkFell(player.Position, _landPos, now))
                    break;

                if (_movement.IsMoveArrived || IsNear(player.Position, _landPos, Service.Config.AlignTolerance))
                {
                    _movement.ReleaseAll();
                    GoNextSegment();
                }
                break;

            case ReplayState.AwaitPlayer:
            {
                // 段间长距离：等待玩家手动走到下一段起跳点（跳跳乐地图机制复杂，段间行走交玩家），
                // 到位（XZ 近 + 同平台）且基本静止稳定 AwaitStableMs → 自动继续执行下一段
                var aDist = player.Position - _awaitTarget;
                DistanceToTarget = aDist.Length();
                var aXZ = aDist;
                aXZ.Y = 0;
                var near = aXZ.Length() < AwaitArriveDist && MathF.Abs(aDist.Y) <= Service.Config.YAlignTolerance;
                if (near)
                {
                    // 稳定确认：玩家在起跳点附近需基本静止持续 AwaitStableMs（防路过误触发抢控制）
                    if (_awaitStableSince == 0)
                    {
                        _awaitStableSince = now;
                        _awaitLastPos = player.Position;
                    }
                    else
                    {
                        var move = player.Position - _awaitLastPos;
                        move.Y = 0;
                        if (move.Length() > AwaitStableMovePerCheck)
                            _awaitStableSince = now; // 仍在移动（路过/走动）→ 重置计时
                        _awaitLastPos = player.Position;
                        if (now - _awaitStableSince >= (long)Service.Config.AwaitStableMs)
                        {
                            _awaitStableSince = 0;
                            PluginLog.Info($"ReplayEngine: 玩家已到位并稳定 {Service.Config.AwaitStableMs:F0}ms → 继续执行段 {CurrentSegment + 2}");
                            ExecuteSegment(CurrentSegment + 1);
                            break;
                        }
                    }
                }
                else
                {
                    _awaitStableSince = 0; // 离开范围 → 重置稳定计时
                    _awaitLastPos = player.Position;
                }
                if (now - _stateEnteredAt > AwaitTimeoutMs)
                {
                    PluginLog.Error("ReplayEngine: 等待玩家到位超时（180s），回放失败");
                    Fail();
                }
                break;
            }

            case ReplayState.AwaitStatus:
            {
                // 移速状态修正：走路切换达标 = IsWalking 到位；buff 修正达标 = 目标 buff 状态出现。
                // 走路切换达标后重入 ExecuteSegment → 继续 buff 维度修正（两级串联：先切走路再补 buff）。
                // 状态必须一致（用户实测）——超时无法就绪直接失败（不带着错误状态硬跳，必过头/不够）。
                var seg = CurrentRoute!.Segments[CurrentSegment];
                var done = _awaitIsWalkSwitch
                    ? MoveStatusHelper.IsWalking == _awaitNeedWalk
                    : MoveStatusHelper.DetectCurrentState() == _awaitTargetState;
                if (done)
                {
                    PluginLog.Info(_awaitIsWalkSwitch
                        ? $"ReplayEngine: 走路切换就绪（{( _awaitNeedWalk ? "慢走" : "跑步")}）→ 继续段 {CurrentSegment + 1} 状态校验"
                        : $"ReplayEngine: 状态就绪（{MoveStatusHelper.StateName(_awaitTargetState)}）→ 继续执行段 {CurrentSegment + 1}");
                    ExecuteSegment(CurrentSegment); // 重入：EnsureMoveStatus 此时一致直接放行，走正常流程
                    break;
                }

                // 施放技能（2s 节流重试：施放可能因 GCD/动作锁失败；速行 5s CD、冲刺 60s CD 重试间隔够用）。
                // 走路切换阶段不施放（切回跑步由重入后的 buff 修正处理）。
                if (!_awaitIsWalkSwitch && !_awaitCastDone && now - _awaitLastCastAt > 2000)
                {
                    _awaitLastCastAt = now;
                    if (_awaitTargetState == MoveState.Sprint)
                    {
                        if (MoveStatusHelper.CastSprint())
                        {
                            _awaitCastDone = true;
                            ChatInfo($"段 {CurrentSegment + 1} 需要冲刺状态，已自动施放技能");
                        }
                        else if (!_awaitCastWarned)
                        {
                            _awaitCastWarned = true;
                            Service.ChatGui.PrintError($"段 {CurrentSegment + 1} 冲刺不可用（CD/未解锁），自动重试中");
                        }
                    }
                    else // SlowBuff 目标：优先速行（同速即生效），无速行职业 → 冲刺（等 20s 变慢跑）
                    {
                        if (MoveStatusHelper.CastForSlowBuff(out var usedSprint))
                        {
                            _awaitCastDone = true;
                            ChatInfo(usedSprint
                                ? $"段 {CurrentSegment + 1} 需要慢跑/速行状态，已自动施放冲刺，等变慢跑后自动继续"
                                : $"段 {CurrentSegment + 1} 需要慢跑/速行状态，已自动施放技能");
                        }
                        else if (!_awaitCastWarned)
                        {
                            _awaitCastWarned = true;
                            Service.ChatGui.PrintError($"段 {CurrentSegment + 1} 技能不可用（速行未解锁或冲刺 CD），自动重试中");
                        }
                    }
                }

                if (now - _stateEnteredAt > _awaitTimeoutMs)
                {
                    Service.ChatGui.PrintError($"段 {CurrentSegment + 1} 状态修正超时，回放失败");
                    PluginLog.Error($"ReplayEngine: 段 {CurrentSegment + 1} 状态修正超时（目标 {MoveStatusHelper.StateName(_awaitTargetState)}）→ 回放失败");
                    Fail();
                }
                break;
            }

            case ReplayState.PausedForStatus:
            {
                // 常速段冲突暂停：等待玩家处理状态后点悬浮窗「继续」（Resume 恢复）或「终止」（Stop）。
                // 期间不注入任何输入，玩家可自由行动。
                break;
            }
        }
    }

    /// <summary>
    /// 走位摔落/平台不符检测（三个走位状态共用）：走位中玩家高度与目标高度差持续超阈值 =
    /// 玩家已摔落或被障碍推下目标平台（实测段21 走位对齐中从平台摔落，Y 65→37 掉 27.6m，
    /// 插件仍持续注入把玩家拉向落点 → 与玩家手动走回互相拉扯（抢控制），且 XZ 到位后
    /// 从错误高度执行下一段起跳）。检测到立即释放控制并失败，不再继续拉扯。
    /// </summary>
    private bool CheckWalkFell(Vector3 playerPos, Vector3 targetPos, long now)
    {
        var yDiff = MathF.Abs(playerPos.Y - targetPos.Y);
        if (yDiff > WalkFellHeightDiff)
        {
            if (_walkFellSince == 0)
                _walkFellSince = now;
            if (now - _walkFellSince > WalkFellConfirmMs)
            {
                PluginLog.Info($"ReplayEngine: 走位中玩家高度与目标偏差 {yDiff:F2}m（玩家 {playerPos.Y:F2} vs 目标 {targetPos.Y:F2}）——" +
                           $"疑似摔落/平台不符，释放控制并回放失败");
                _movement.ReleaseAll();
                Fail();
                return true;
            }
        }
        else
        {
            _walkFellSince = 0;
        }
        return false;
    }

    private void Fail()
    {
        _movement.TimelineInput = null;
        _movement.WorldMoveYaw = null;
        _jump.SetHeld(false);
        _movement.ReleaseAll();
        _jump.Stop();
        // 恢复回放前的走路模式（与 Stop 一致——否则失败后走路模式残留，下次回放 _walkBeforeReplay 带旧值）
        if (_walkBeforeReplay is { } walkBefore)
            MoveStatusHelper.SetWalking(walkBefore);
        _walkBeforeReplay = null;
        CurrentRoute = null; // 失败结束：同样清空回放路线引用（RouteOverlay 回退当前录制路线）
        State = ReplayState.Failed;
    }

    private float TakeoffYaw()
    {
        var d = _landPos - _takeoffPos;
        d.Y = 0;
        return MathF.Atan2(d.X, d.Z);
    }

    /// <summary>
    /// 起跳判定（投影法）：玩家沿冲刺方向的投影已到达起跳点（沿向 ≤0.01m 或已越过），
    /// 且横向偏差 &lt;0.05m。沿向精确到 0.01（"已越过"兜底保证不会漏判，起跳位置误差 = 单帧步进）；
    /// 横向 0.05 是冲刺线到起跳点的垂直偏差（冲刺起点 0.01 精确 + 朝向精确时理论值≈0，
    /// 0.05 仅为浮点抖动保险）。
    /// </summary>
    private bool IsPastTakeoff(Vector3 pos, Vector3 takeoff)
    {
        var to = takeoff - pos;
        to.Y = 0;
        var dist = to.Length();
        if (dist > 2f)
            return false;

        // 高度条件：起跳点必须是同层平台（高度差过大 = 角色不在起跳平台上，禁止起跳）
        if (MathF.Abs(pos.Y - takeoff.Y) > TakeoffHeightTolerance)
            return false;

        var forward = new Vector3(MathF.Sin(_takeoffYaw), 0, MathF.Cos(_takeoffYaw));
        var along = Vector3.Dot(to, forward); // >0 = 起跳点还在前方；≤0 = 已越过
        var lateral = MathF.Sqrt(MathF.Max(0f, dist * dist - along * along));
        // 横向判定保持严格（配置 TakeoffLateralTolerance=0.01）——跳跳乐平台小，0.3m 横向偏差就是掉下去。
        // 精确到达由机制保证：冲刺方向 = 录制起跳方向（直线对准，横向≈0）+ 播完未到起跳点强制冲刺。
        return along <= Service.Config.TakeoffAlongTolerance && lateral < Service.Config.TakeoffLateralTolerance;
    }

    // ===== 工具 =====

    private static bool IsNear(Vector3 a, Vector3 b, float tolerance)
    {
        var d = a - b;
        d.Y = 0; // 对齐按水平（高度由地形决定；高度校验在起跳/落地判定单独做）
        return d.LengthSquared() <= tolerance * tolerance;
    }

    private static float XZDistance(Vector3 a, Vector3 b)
    {
        var d = a - b;
        d.Y = 0;
        return d.Length();
    }

    private static bool IsFacing(float currentYaw, float targetYaw)
        => MathF.Abs(NormalizeAngle(currentYaw - targetYaw)) < Service.Config.FacingToleranceRad;

    private static float NormalizeAngle(float rad)
    {
        while (rad > MathF.PI) rad -= 2 * MathF.PI;
        while (rad < -MathF.PI) rad += 2 * MathF.PI;
        return rad;
    }

    /// <summary>朝向插值（走短路径，绕 2π 归一，避免跨 π 边界时反向绕大圈）。</summary>
    private static float LerpYaw(float a, float b, float t)
    {
        var d = NormalizeAngle(b - a);
        return NormalizeAngle(a + d * t);
    }

    public void Dispose()
    {
        Stop();
        Service.Framework.Update -= Tick;
    }
}
