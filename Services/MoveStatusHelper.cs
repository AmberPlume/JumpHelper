using FFXIVClientStructs.FFXIV.Client.Game;
using JumpHelper.Models;
using Control = FFXIVClientStructs.FFXIV.Client.Game.Control.Control;

namespace JumpHelper.Services;

/// <summary>
/// 移速状态工具（冲刺/慢跑/速行）：检测玩家当前移速 buff、自动施放冲刺/速行技能。
///
/// 背景（用户实测结论，勿违背）：FF14 跳跃距离由起跳瞬间水平速度决定，移速 buff 直接改变起跳速度——
/// 带移速加成跳"常速录制"的段会跳过头（小平台必跌）；状态必须完全一致才能确保回放成功率。
///
/// 三种状态（FFCAFE/XIVAPI 查证，2026-07 数据）：
///   冲刺 Sprint：Action 3 / Status 50 系（50/481/1342/1938），非战斗 20s、战斗 10s，CD 60s，全职业，移速最高。
///   慢跑 Jog：  Status 4209，冲刺效果结束后永久附加（非战斗常驻，进战斗消失），无法主动取消（CanStatusOff=false）。
///   速行 Peloton：Action 7557（Recast100ms=50 → 5s CD，30s 持续，非战斗生效），弓/诗/机/舞职能技能 Lv20，
///                  Status 1199/1985。与慢跑同速（可互换）。
/// 慢跑与速行在插件中合并为 MoveState.SlowBuff（同速处理）。
///
/// 施放走游戏内部 ActionManager.UseAction（等价玩家按技能，零输入延迟、无需热键栏配置）——
/// 参考 OmenTools UseActionManager 的 Hook.Original 调用方式；本机 FFXIVClientStructs 的
/// MemberFunctionPointers.UseAction 直接暴露为函数指针字段，直接调用（零 hook 零开销）。
/// </summary>
public static unsafe class MoveStatusHelper
{
    // ===== 常量（游戏数据，注释保留溯源） =====

    /// <summary>冲刺技能 ID（Action 3）。</summary>
    public const uint SprintActionId = 3;

    /// <summary>速行技能 ID（Action 7557，弓/诗/机/舞职能技能）。</summary>
    public const uint PelotonActionId = 7557;

    /// <summary>冲刺状态 ID 集合（Status 50 系，多个场景变体；任一存在即视为冲刺中）。</summary>
    private static readonly ushort[] SprintStatusIds = { 50, 481, 1342, 1938 };

    /// <summary>慢跑状态 ID（Status 4209，冲刺结束后永久附加）。</summary>
    private const ushort JogStatusId = 4209;

    /// <summary>速行状态 ID 集合（Status 1199 战斗消失版 / 1985）。</summary>
    private static readonly ushort[] PelotonStatusIds = { 1199, 1985 };

    // ===== UseAction 调用（MemberFunctionPointers.UseAction 是函数指针字段，直接调用；零 hook 零开销） =====

    /// <summary>UseAction 原函数（等价玩家按技能，零输入延迟、无需热键栏配置）。
    /// 参考 OmenTools UseActionManager 的 Hook.Original 调用方式；本机 FFXIVClientStructs
    /// 的 MemberFunctionPointers.UseAction 直接暴露为函数指针，可调用。</summary>
    private static bool UseActionRaw(
        ActionManager* manager, ActionType actionType, uint actionID, ulong targetID,
        uint extraParam, ActionManager.UseActionMode queueState, uint comboRouteID, bool* outOptAreaTargeted)
        => ActionManager.MemberFunctionPointers.UseAction(manager, actionType, actionID, targetID,
            extraParam, queueState, comboRouteID, outOptAreaTargeted);

    /// <summary>目标 ID：玩家自身（技能施放目标参数惯例 0xE0000000 = 自身）。</summary>
    private const ulong SelfTargetId = 0xE000_0000;

    // ===== 走路模式（Walk——与移速 buff 正交的输入模式，客户端本地状态可自动切换） =====

    /// <summary>玩家当前是否处于走路模式（Control.IsWalking 字段，FFXIVClientStructs 直接可读）。
    /// 走路限速 → 起跳速度低 → 跳距短（用户实测 0.74m 级超短跳，用于极小平台间距）。</summary>
    public static bool IsWalking => Control.Instance()->IsWalking;

    /// <summary>切换走路/跑步模式：直接写 Control.IsWalking（本地状态，零延迟零成本，
    /// 等价玩家按 Walk 键切换；回放结束由 ReplayEngine.Stop 恢复进入时状态）。</summary>
    public static void SetWalking(bool walk) => Control.Instance()->IsWalking = walk;

    // ===== 状态检测 =====

    /// <summary>
    /// 检测玩家当前移动状态（优先级：慢走 > 冲刺 > 慢跑/速行 > 无）。
    /// 慢走（走路限速主导起跳速度）优先于移速 buff；冲刺与慢跑/速行同挂时取冲刺（移速最高者主导）。
    /// </summary>
    public static MoveState DetectCurrentState()
    {
        if (IsWalking)
            return MoveState.Walk;
        var player = Service.ObjectTable.LocalPlayer;
        if (player == null)
            return MoveState.None;

        bool hasSprint = false, hasSlow = false;
        foreach (var st in player.StatusList)
        {
            var id = (ushort)st.StatusId;
            if (id == 0) continue;
            if (!hasSprint && Array.IndexOf(SprintStatusIds, id) >= 0) hasSprint = true;
            if (!hasSlow && (id == JogStatusId || Array.IndexOf(PelotonStatusIds, id) >= 0)) hasSlow = true;
            if (hasSprint) return MoveState.Sprint; // 冲刺最高优先
        }
        return hasSlow ? MoveState.SlowBuff : MoveState.None;
    }

    /// <summary>当前是否带移速加成（录制开始提示用）。</summary>
    public static bool HasMoveBuff => DetectCurrentState() != MoveState.None;

    /// <summary>
    /// 速行状态剩余时间（秒）；当前无速行状态返回 float.MaxValue（视为充足，无需续）。
    /// 慢跑是永久状态（无剩余概念）——仅速行会到期，续期逻辑只处理速行。
    /// </summary>
    public static float PelotonRemainingSeconds()
    {
        var player = Service.ObjectTable.LocalPlayer;
        if (player == null)
            return float.MaxValue;
        foreach (var st in player.StatusList)
        {
            if (Array.IndexOf(PelotonStatusIds, (ushort)st.StatusId) >= 0)
                return st.RemainingTime; // Dalamud Status.RemainingTime 为 float 秒
        }
        return float.MaxValue;
    }

    /// <summary>状态中文名（聊天提示/日志用）。</summary>
    public static string StateName(MoveState state) => state switch
    {
        MoveState.Sprint => "冲刺",
        MoveState.SlowBuff => "慢跑/速行",
        MoveState.Walk => "慢走",
        _ => "常速"
    };

    // ===== 施放能力 =====

    /// <summary>技能当前是否可用：GetActionStatus == 0 即可用（未解锁/CD/动作锁均返回非 0，
    /// 本机 FFXIVClientStructs 无 IsActionUnlocked，统一用此判断——未解锁时不会误施放）。</summary>
    private static bool IsActionAvailable(uint actionId)
        => ActionManager.Instance()->GetActionStatus(ActionType.Action, actionId) == 0;

    /// <summary>冲刺技能当前是否可用。</summary>
    public static bool CanCastSprint() => IsActionAvailable(SprintActionId);

    /// <summary>速行技能当前是否可用（未解锁=当前职业无速行（非弓/诗/机/舞）或等级不足，返回 false）。</summary>
    public static bool CanCastPeloton() => IsActionAvailable(PelotonActionId);

    /// <summary>施放冲刺（成功返回 true；CD/未解锁返回 false）。</summary>
    public static bool CastSprint()
        => CanCastSprint() && UseActionRaw(ActionManager.Instance(), ActionType.Action,
            SprintActionId, SelfTargetId, 0, 0, 0, null);

    /// <summary>施放速行（成功返回 true；CD/未解锁返回 false）。</summary>
    public static bool CastPeloton()
        => CanCastPeloton() && UseActionRaw(ActionManager.Instance(), ActionType.Action,
            PelotonActionId, SelfTargetId, 0, 0, 0, null);

    /// <summary>
    /// 为慢跑/速行目标段补状态：优先速行（同速即生效，30s 持续 5s CD 可续）；无速行职业 → 冲刺
    /// （移速更高但可接受——注意：冲刺跳慢跑段会过头，调用方必须在冲刺结束后（变慢跑）再继续，
    /// 见 ReplayEngine.AwaitStatus 的等待逻辑）。
    /// </summary>
    public static bool CastForSlowBuff(out bool usedSprint)
    {
        if (CanCastPeloton())
        {
            usedSprint = false;
            return CastPeloton();
        }
        usedSprint = true;
        return CastSprint();
    }
}
