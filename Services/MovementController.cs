using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Game.Config;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using JumpHelper.Services.Interop;
using JumpHelper.Utils;

namespace JumpHelper.Services;

/// <summary>飞行移动输入结构（RMIFly 输出）。</summary>
[StructLayout(LayoutKind.Explicit, Size = 0x18)]
public struct PlayerMoveControllerFlyInput
{
    [FieldOffset(0x0)]
    public float Forward;

    [FieldOffset(0x4)]
    public float Left;

    [FieldOffset(0x8)]
    public float Up;

    [FieldOffset(0xC)]
    public float Turn;

    [FieldOffset(0x10)]
    public float u10;

    [FieldOffset(0x14)]
    public byte DirMode;

    [FieldOffset(0x15)]
    public byte HaveBackwardOrStrafe;
}

/// <summary>
/// 移动控制：Hook 游戏 RMIWalk/RMIFly 输入聚合函数，直接注入移动/转向输入。
/// 自研实现（签名与偏移参考 ffxiv_navmesh，同游戏版本）。
/// 职责：短路径直线移动到目标点（起点对齐）、转向对准、助跑/空中前进、释放全部输入。
/// </summary>
public unsafe sealed class MovementController : IDisposable
{
    /// <summary>满转向输入对应的角度差（弧度）。</summary>
    /// <summary>期望朝向转向输入满值对应的角度差（弧度，20°）：delta ≥ 此值 → turn=±1 全速转向。
    /// 原 45° 满值：20°~45° 区间仅比例转向（turn<1），转向慢——标准模式读档慢的主因之一。
    /// 缩小满值角让常见转向量更早全速（全速仍是游戏键盘角速度上限，不会过冲）。</summary>
    private const float FULL_TURN_INPUT_ANGLE = MathF.PI / 9;

    /// <summary>低速逼近起始距离（距目标小于此值开始线性减速）。</summary>
    /// <summary>低速逼近起始距离（米）：距目标 ≥ 此值全速，< 此值线性减速停点。
    /// 0.5→0.3：落地对齐等短距走位（≤1m）减速段占比大、平均速度低（标准模式读档慢主因之一），
    /// 缩短减速段加速走位（MinApproachSpeed 步进 0.003m/帧，仍可精确停在 0.01m 容差内）。</summary>
    private const float ApproachSlowDist = 0.3f;

    /// <summary>低速逼近的最小速度因子（防止注入过小被游戏死区忽略）。</summary>
    private const float MinApproachSpeed = 0.03f;
    private const float NearDistSq = 4f; // 距目标 2m 内才启用"最近点锁定"（远处距离增大=撞墙/抖动，不算到达）

    /// <summary>是否正在注入输入。</summary>
    public bool Active { get; private set; }

    /// <summary>移动目标点（MoveTo 设置）。</summary>
    public Vector3? TargetPosition { get; private set; }

    /// <summary>到达判定容差（米，水平距离）。</summary>
    public float TargetTolerance { get; set; } = 0.1f;

    /// <summary>期望朝向（弧度，SetDesiredFacing 设置）。</summary>
    public float? DesiredFacing { get; private set; }

    /// <summary>朝向对齐精度（弧度）。</summary>
    public float FacingPrecisionRad { get; set; } = 0.01f;
    /// <summary>到达锁定：一旦进入容差范围即锁定停止，防止边界距离抖动导致输入时断时续。</summary>
    private bool _arrived;
    private float _lastTargetDist;  // DirectionToDestination 用（距离平方）
    private float _stuckLastDist;   // CheckStuck 用（距离，米）——独立字段防单位冲突（见 CheckStuck 注释）

    // ===== 真实输入采集（录制用） =====
    /// <summary>最近一帧玩家真实输入（RMIWalk Original 返回值，插件未注入时即玩家操作）。</summary>
    public float LastRealLeft { get; private set; }
    public float LastRealForward { get; private set; }
    public float LastRealTurn { get; private set; }

    /// <summary>最近一帧游戏移动输入是否可用（RMIWalk 输入启用检测：落地硬直/跳跃/特殊状态为 false）——
    /// 回放引擎用：输入不可用时走位/预助跑注入会被游戏忽略（速度 0/位置不动），须等待恢复再推进。
    /// 时间线注入不受此门控（见 RMIWalkDetour 分支顺序）。</summary>
    public bool LastInputAllowed { get; private set; } = true;

    // ===== 输入门控诊断（LastInputAllowed 误判排查：站定 vs 硬直时各分量值） =====
    private byte _lastAdditiveUnk;
    private bool _lastEnable1;
    private bool _lastEnable2;

    /// <summary>最近一帧输入门控分量：(bAdditiveUnk, rmiWalkIsInputEnabled1, rmiWalkIsInputEnabled2)——诊断用。</summary>
    public (byte AdditiveUnk, bool Enable1, bool Enable2) LastInputGate
        => (_lastAdditiveUnk, _lastEnable1, _lastEnable2);

    // ===== 时间线注入（回放用） =====
    /// <summary>非 null 时覆盖移动输入（全量输入时间线重放），优先于 MoveTo/SetForward 逻辑。</summary>
    public (float Left, float Forward, float Turn)? TimelineInput { get; set; }

    /// <summary>
    /// 非 null 时（配合 TimelineInput）：移动方向 = 该世界朝向，幅度取 TimelineInput 输入模长——
    /// 移动方向与角色朝向解耦，不受转向速度限制（录制鼠标转向快、回放键盘转向慢是方向误差根源，
    /// 世界方向注入使起跳速度矢量精确等于录制移动方向）。转向（TimelineInput.Turn）仅负责外观。
    /// </summary>
    public float? WorldMoveYaw { get; set; }

    private bool _legacyMode;

    /// <summary>当前是否传统（legacy）操作模式：输入（sumLeft/sumForward）相对相机方位（不可复现），
    /// 标准模式相对角色朝向（可复现）。录制/回放模式不一致 → 方向偏差，读档检测用。</summary>
    public bool IsLegacyMode => _legacyMode;
    private bool _forwardHeld;

    // ===== 卡住检测（MoveTo 撞墙/受阻/蹭墙时自动停止） =====
    private DateTime _lastStuckCheck = DateTime.MinValue;
    private Vector3 _lastStuckPos;
    private int _stuckAccumMs;

    private const int StuckCheckIntervalMs = 500;
    private const float StuckMoveThresholdSq = 0.05f * 0.05f; // 无目标时：500ms 位移 < 5cm 视为停滞
    private const float StuckProgressThreshold = 0.05f;       // 有目标时：500ms 距离缩短 < 5cm 视为无进展
    private const int StuckStopMs = 2000;                     // 持续停滞 2s 自动停止

    // ===== RMIWalk 输入启用检测 =====
    private delegate bool RMIWalkIsInputEnabled(void* self);

    [Signature("E8 ?? ?? ?? ?? 84 C0 75 10 38 43 3C")]
    private readonly RMIWalkIsInputEnabled rmiWalkIsInputEnabled1 = null!;

    [Signature("E8 ?? ?? ?? ?? 84 C0 75 03 88 47 3F")]
    private readonly RMIWalkIsInputEnabled rmiWalkIsInputEnabled2 = null!;

    // ===== RMIWalk（地面移动输入聚合） =====
    private delegate void RMIWalkDelegate
    (
        void* self,
        float* sumLeft,
        float* sumForward,
        float* sumTurnLeft,
        byte* haveBackwardOrStrafe,
        byte* a6,
        byte bAdditiveUnk
    );

    [Signature("E8 ?? ?? ?? ?? 80 7B 3E 00 48 8D 3D", DetourName = nameof(RMIWalkDetour))]
    private Hook<RMIWalkDelegate> rmiWalkHook = null!;

    // ===== RMIFly（飞行移动输入聚合） =====
    private delegate void RMIFlyDelegate(void* self, PlayerMoveControllerFlyInput* result);

    [Signature("E8 ?? ?? ?? ?? 0F B6 0D ?? ?? ?? ?? B8", DetourName = nameof(RMIFlyDetour))]
    private Hook<RMIFlyDelegate> rmiFlyHook = null!;

    public MovementController()
    {
        Service.Hook.InitializeFromAttributes(this);
        Service.GameConfig.UiControlChanged += OnConfigChanged;
        UpdateLegacyMode();
        rmiWalkHook.Enable();
        rmiFlyHook.Enable();
        PluginLog.Info($"MovementController: RMIWalk=0x{rmiWalkHook.Address:X} RMIFly=0x{rmiFlyHook.Address:X}");
    }

    // ===== 对外接口 =====

    /// <summary>直线移动到目标点（水平距离 &lt;= tolerance 视为到达并锁定停止）。</summary>
    public void MoveTo(Vector3 target, float tolerance = 0.1f)
    {
        TargetPosition = target;
        TargetTolerance = tolerance;
        _arrived = false;
        _lastTargetDist = -1f;
        _stuckLastDist = -1f;
        Active = true;
    }

    /// <summary>转向到指定世界朝向（弧度）。</summary>
    public void SetDesiredFacing(float yaw)
    {
        DesiredFacing = yaw;
        Active = true;
    }

    /// <summary>当前是否有活跃的移动目标且已到达锁定（引擎层到达判定，替代依赖容差的 IsNear）。</summary>
    public bool IsMoveArrived => Active && _arrived && TargetPosition != null;

    /// <summary>按住/松开前进输入（助跑与空中控制共用；沿参考方向前进）。</summary>
    public void SetForward(bool held)
    {
        _forwardHeld = held;
        Active = true;
    }

    /// <summary>释放全部注入输入并回到非激活状态（落地刹停 / 紧急停止）。</summary>
    public void ReleaseAll()
    {
        TargetPosition = null;
        DesiredFacing = null;
        WorldMoveYaw = null;
        _forwardHeld = false;
        _arrived = false;
        Active = false;
        _stuckAccumMs = 0;
        _lastStuckCheck = DateTime.MinValue;
        _lastTargetDist = -1f;
        _stuckLastDist = -1f;
    }

    // ===== Detour =====

    private void RMIWalkDetour
    (
        void* self,
        float* sumLeft,
        float* sumForward,
        float* sumTurnLeft,
        byte* haveBackwardOrStrafe,
        byte* a6,
        byte bAdditiveUnk
    )
    {
        rmiWalkHook.Original(self, sumLeft, sumForward, sumTurnLeft, haveBackwardOrStrafe, a6, bAdditiveUnk);

        // 记录玩家真实输入（录制采集用；插件不注入时即玩家操作）
        LastRealLeft = *sumLeft;
        LastRealForward = *sumForward;
        LastRealTurn = *sumTurnLeft;

        // 时间线注入优先：全量输入回放时直接覆盖（不参与 MoveTo/SetForward 逻辑）
        if (TimelineInput is { } tl)
        {
            if (WorldMoveYaw is { } worldYaw)
            {
                // 世界方向移动：移动方向 = 录制移动朝向（不受角色转向速度限制），幅度 = 录制输入模长。
                // 公式与 MoveTo 一致（rel = 目标方向 - 参考方向；sumLeft=sin, sumForward=cos）。
                var refAngle = GetMoveReferenceDirection().Rad;
                var rel = NormalizeAngleRad(worldYaw - refAngle);
                var mag = MathF.Min(1.4f, MathF.Sqrt(tl.Left * tl.Left + tl.Forward * tl.Forward));
                *sumLeft = MathF.Sin(rel) * mag;
                *sumForward = MathF.Cos(rel) * mag;
            }
            else
            {
                *sumLeft = tl.Left;
                *sumForward = tl.Forward;
            }
            *sumTurnLeft = tl.Turn;
            // 侧移标志：游戏用 haveBackwardOrStrafe 标记"有后退/侧移输入"，侧移（Q/E、按住右键 A/D）
            // 时该标志置位 sumLeft 才被解析为平移。注入侧移分量（sumLeft≠0）时必须同步置位，
            // 否则游戏忽略侧移 → 回放侧移段玩家只沿 Forward 方向移动（实测段18 位移方向偏 24° 坠落）。
            *haveBackwardOrStrafe = MathF.Abs(*sumLeft) > 0.001f ? (byte)1 : (byte)0;
            return;
        }

        if (!Active)
            return;

        CheckStuck();

        // 输入通道可用性检查：rmiWalkIsInputEnabled = 玩家输入通道是否可用（UI 锁定/菜单时为 false）。
        // 注意：bAdditiveUnk 不能参与门控——实测它反映"玩家当前是否在移动"（0=移动中 1=静止），
        // 站定后为 1 且持续（段 0 合并段后 9s+）。若用它做门控：玩家停 → 注入被跳过 → 玩家不动
        // → additive 保持 1 → 死锁（实测段 1：走位等待 9s 超时）。注入本身安全（时间线注入不受
        // 门控、在 additive=1 期间正常播完两跳），bAdditiveUnk 仅保留记录供诊断。
        _lastAdditiveUnk = bAdditiveUnk;
        _lastEnable1 = rmiWalkIsInputEnabled1(self);
        _lastEnable2 = rmiWalkIsInputEnabled2(self);
        var movementAllowed = _lastEnable1 && _lastEnable2;
        LastInputAllowed = movementAllowed;
        if (!movementAllowed)
            return;

        // 前进/移动注入：优先向目标点移动；无目标时按强制前进
        if (DirectionToDestination() is { } relDir)
        {
            var dir = relDir.ToDirection();
            // 低速逼近：接近目标时按距离线性减速，保证停在容差内（全速冲过最近点会停在 0.05~0.1m 处）
            var speed = ApproachSpeedFactor();
            *sumLeft = dir.X * speed;
            *sumForward = dir.Y * speed;
        }
        else if (_forwardHeld)
        {
            *sumLeft = 0;
            *sumForward = 1;
        }
        // 侧移标志（走位同样需要）：目标方向与面向夹角大时 sumLeft 分量显著，置位标志使其生效
        *haveBackwardOrStrafe = MathF.Abs(*sumLeft) > 0.001f ? (byte)1 : (byte)0;

        // 转向注入
        if (ResolveFacingTurnInput() is { } turnInput)
            *sumTurnLeft = turnInput;
    }

    private void RMIFlyDetour(void* self, PlayerMoveControllerFlyInput* result)
    {
        rmiFlyHook.Original(self, result);
        if (!Active)
            return;

        // 时间线注入（飞行场景一致性；跳跳乐跳跃走 RMIWalk，此分支通常不触发）
        if (TimelineInput is { } tl && WorldMoveYaw is { } worldYaw)
        {
            var refAngle = GetMoveReferenceDirection().Rad;
            var rel = NormalizeAngleRad(worldYaw - refAngle);
            var mag = MathF.Min(1.4f, MathF.Sqrt(tl.Left * tl.Left + tl.Forward * tl.Forward));
            result->Forward = MathF.Cos(rel) * mag;
            result->Left = MathF.Sin(rel) * mag;
            result->Up = 0;
            result->Turn = tl.Turn;
            return;
        }

        if (DirectionToDestination() is { } relDir)
        {
            var dir = relDir.ToDirection();
            result->Forward = dir.Y;
            result->Left = dir.X;
            result->Up = 0;
        }
        else if (_forwardHeld)
        {
            result->Forward = 1;
            result->Left = 0;
            result->Up = 0;
        }

        if (ResolveFacingTurnInput() is { } turnInput)
            result->Turn = turnInput;
    }

    // ===== 内部计算 =====

    /// <summary>
    /// 目标点相对玩家参考方向的水平角。无目标/已到达锁定/水平距离在容差内返回 null。
    /// 进入容差范围时置 _arrived 锁定，防止边界抖动导致注入时断时续。
    /// </summary>
    private Angle? DirectionToDestination()
    {
        if (_arrived || TargetPosition is not { } target)
            return null;

        var player = Service.ObjectTable.LocalPlayer;
        if (player == null)
            return null;

        var dist = target - player.Position;
        dist.Y = 0; // 走位到达按 XZ（水平移动，高度由地形决定）——3D 判定会让跨层目标（起点在高处平台）永远走不到
        var distSq = dist.LengthSquared();
        var toleranceSq = TargetTolerance * TargetTolerance;

        // 1) 容差内 = 到达（容差 >0 时生效）
        if (distSq <= toleranceSq)
        {
            _arrived = true;
            return null;
        }

        // 2) 最近点锁定：接近目标（2m 内）时，距离开始增大 = 已越过目标最近点（等效"精确到点"）。
        //    远处不启用——撞墙/地形起伏会让距离短暂增大，误判"已到达"会导致角色停在中途。
        //    配合低速逼近，越过时的偏移只有一帧低速步进（毫米级），不会停远。
        if (distSq <= NearDistSq && _lastTargetDist >= 0f && distSq > _lastTargetDist)
        {
            _arrived = true;
            return null;
        }
        _lastTargetDist = distSq;

        var dirH = Angle.FromDirectionXZ(dist);
        return dirH - GetMoveReferenceDirection();
    }

    /// <summary>
    /// 低速逼近速度因子：距目标 &gt;= ApproachSlowDist 全速；接近时按距离线性减速，
    /// 使角色能以足够小的步进进入容差窗口（全速每帧 0.05~0.1m 会直接跨过 0.01m 容差）。
    /// </summary>
    private float ApproachSpeedFactor()
    {
        var player = Service.ObjectTable.LocalPlayer;
        if (player == null || TargetPosition is not { } target)
            return 1f;

        var dist = target - player.Position;
        dist.Y = 0; // 走位速度按 XZ（水平移动）
        var d = dist.Length();
        if (d >= ApproachSlowDist)
            return 1f;
        if (d <= 0.001f)
            return 0f;
        return Math.Clamp(d / ApproachSlowDist, MinApproachSpeed, 1f);
    }

    /// <summary>
    /// 移动参考方向：modern 模式 = 角色朝向；legacy 模式 = 相机方位 + 180°。
    /// </summary>
    private Angle GetMoveReferenceDirection()
    {
        if (_legacyMode)
            return ((CameraEx*)CameraManager.Instance()->GetActiveCamera())->DirH.Radians() + 180f.Degrees();

        return Service.ObjectTable.LocalPlayer?.Rotation.Radians() ?? default;
    }

    /// <summary>期望朝向与角色当前朝向的转向输入；已对齐返回 0，未设置返回 null。</summary>
    private float? ResolveFacingTurnInput()    {        if (DesiredFacing is not { } facing)
            return null;

        var player = Service.ObjectTable.LocalPlayer;
        if (player == null)
            return null;

        var delta = (facing.Radians() - player.Rotation.Radians()).Normalized().Rad;
        if (MathF.Abs(delta) <= Service.Config.FacingToleranceRad)
            return 0;

        return Math.Clamp(delta / FULL_TURN_INPUT_ANGLE, -1f, 1f);
    }

    /// <summary>角度归一化到 [-π, π]（世界方向注入用）。</summary>
    private static float NormalizeAngleRad(float rad)
    {
        while (rad > MathF.PI) rad -= 2 * MathF.PI;
        while (rad < -MathF.PI) rad += 2 * MathF.PI;
        return rad;
    }

    /// <summary>
    /// 卡住检测：有移动意图时检测"有效前进"是否持续缺失（累计 2s 自动停止）。
    /// 有目标时用"与目标的距离缩短量"判定（蹭墙/横移时距离不缩短 → 判卡住）；
    /// 无目标（强制前进）时退回"位置位移"判定（完全停滞 → 判卡住）。
    /// </summary>
    private void CheckStuck()
    {
        if (_arrived || (TargetPosition == null && !_forwardHeld))
            return;

        var now = DateTime.UtcNow;
        if ((now - _lastStuckCheck).TotalMilliseconds < StuckCheckIntervalMs)
            return;

        _lastStuckCheck = now;

        var player = Service.ObjectTable.LocalPlayer;
        if (player == null)
            return;

        bool noProgress;
        if (TargetPosition is { } target)
        {
            // 目标距离进度：500ms 内距离目标缩短不足阈值 = 无有效前进（水平距离）
            // 注意：用独立字段 _stuckLastDist——不能复用 DirectionToDestination 的 _lastTargetDist
            // （那个存"距离平方"、这个存"距离"，单位冲突会让最近点锁定误判 _arrived=true →
            // 实测段17 走位 9s 位置不动但 IsMoveArrived=True，玩家停在半路）。
            var d = target - player.Position;
            d.Y = 0;
            var curDist = d.Length();
            if (_stuckLastDist < 0)
            {
                // 首次采样仅初始化，不判定
                _stuckLastDist = curDist;
                return;
            }

            noProgress = _stuckLastDist - curDist < StuckProgressThreshold;
            _stuckLastDist = curDist;
        }
        else
        {
            // 位置位移：500ms 内位移不足阈值 = 停滞
            var deltaSq = (player.Position - _lastStuckPos).LengthSquared();
            _lastStuckPos = player.Position;
            noProgress = deltaSq < StuckMoveThresholdSq;
        }

        if (noProgress)
        {
            _stuckAccumMs += StuckCheckIntervalMs;
            if (_stuckAccumMs >= StuckStopMs)
            {
                PluginLog.Error("移动卡住（疑似被障碍物阻挡或路径偏移），已自动停止");
                ReleaseAll();
            }
        }
        else
        {
            _stuckAccumMs = 0;
        }
    }

    private void OnConfigChanged(object? sender, ConfigChangeEvent evt) => UpdateLegacyMode();

    private void UpdateLegacyMode()
        => _legacyMode = Service.GameConfig.UiControl.TryGetUInt("MoveMode", out var mode) && mode == 1;

    public void Dispose()
    {
        ReleaseAll();
        Service.GameConfig.UiControlChanged -= OnConfigChanged;
        rmiFlyHook.Dispose();
        rmiWalkHook.Dispose();
    }
}
