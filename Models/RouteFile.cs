using System.Numerics;

namespace JumpHelper.Models;

/// <summary>
/// 段落记录方式（回放如何决定"下一步跳哪一段"）：
/// 分散地图（先去左边跳一小段、再去右边跳一小段）的段落不构成线性递增路径，需按距离衔接。
/// </summary>
public enum SegmentMode
{
    /// <summary>线性（当前）：按序号顺序依次跳跃（段列表索引 +1 为下一步）。</summary>
    Linear = 0,

    /// <summary>碎片：段落有序号但仅作"名字"，不依赖序号；落地后按"落点→某段起跳点"的水平距离
    /// （≤ FragLinkDistXZ）与高度（|ΔY| ≤ FragLinkDistY）自动衔接下一步；
    /// 岔路（多候选）时暂停让玩家选；找不出"可衔接"的下阶段即终止。Y 高度绝不忽略。</summary>
    Fragment = 1
}

/// <summary>
/// 一个路线文件：绑定到指定地图（TerritoryId）的跳跃段集合。
/// 录制时插件自动采集玩家真实跳跃的"起跳点→落点"段（Segments），
/// 玩家用 rec node 在关键落点"打标"（Markers）以便选择回放起终点。
/// 一路线一 JSON 文件，段数不限。
/// </summary>
public class RouteFile
{
    /// <summary>路线名称（同时是 JSON 文件名）。</summary>
    public string Name { get; set; } = "";

    /// <summary>区域 ID（TerritoryType），用于校验玩家是否处于正确地图。</summary>
    public uint TerritoryId { get; set; }

    /// <summary>录制地图名（列表显示用；旧路线无此字段则显示 TerritoryId 数字）。</summary>
    public string? TerritoryName { get; set; }

    /// <summary>
    /// 路线使用的段落记录方式（线性/碎片）。旧路线（无此字段）默认 = 线性，完全兼容。
    /// 碎片路线回放时按距离/高度自动衔接段落，线性路线按序号顺序跳。
    /// </summary>
    public SegmentMode SegmentMode { get; set; } = SegmentMode.Linear;

    /// <summary>录制时的操作模式（0=标准 modern，1=传统 legacy）——读档一致性检测用。
    /// 输入（sumLeft/sumForward）语义依赖模式：标准=相对角色朝向（回放可复现）；传统=相对相机方位
    /// （相机朝向不可复现——传统模式录的路线回放方向必偏）。混合模式录制的路线会污染，
    /// 读档时检测当前模式与录制不一致即警告（建议统一标准模式录制）。旧路线无此字段 = 0。</summary>
    public int ControlMode { get; set; }

    /// <summary>跳跃段列表（按录制顺序）。</summary>
    public List<JumpSegment> Segments { get; set; } = new();

    /// <summary>打标节点（关键落点，供选择回放起终点）。</summary>
    public List<RouteNode> Markers { get; set; } = new();

    /// <summary>上次回放使用的起点标记索引。</summary>
    public int LastStartIndex { get; set; }

    /// <summary>上次回放使用的终点标记索引（-1 表示最后一个标记）。</summary>
    public int LastEndIndex { get; set; } = -1;

    /// <summary>
    /// 路线移速状态汇总（列表显示用）：全部常速 → "常速"；混合 → 按出现状态组合
    /// （如 "常速+冲刺+慢跑/速行+慢走"）。旧路线（全部默认 None）显示"常速"。
    /// </summary>
    public string MoveStateSummary()
    {
        var hasSprint = Segments.Any(s => s.MoveState == MoveState.Sprint);
        var hasSlow = Segments.Any(s => s.MoveState == MoveState.SlowBuff);
        var hasWalk = Segments.Any(s => s.MoveState == MoveState.Walk);
        var hasNone = Segments.Any(s => s.MoveState == MoveState.None);
        var parts = new List<string>();
        if (hasNone) parts.Add("常速");
        if (hasSprint) parts.Add("冲刺");
        if (hasSlow) parts.Add("慢跑/速行");
        if (hasWalk) parts.Add("慢走");
        return parts.Count == 0 ? "常速" : string.Join("+", parts);
    }

    /// <summary>
    /// 是否存在"移速 buff 段之后还有常速段"（读取时提醒：慢跑常驻后接常速段会跳过头，
    /// 玩家可能需要手动取消移速状态）。仅统计 buff 维度（冲刺/慢跑/速行）——慢走是输入模式，
    /// 回放可自动切回跑步，无需手动取消。
    /// </summary>
    public bool HasStateBeforeNone()
    {
        var seenState = false;
        foreach (var s in Segments)
        {
            if (s.MoveState is MoveState.Sprint or MoveState.SlowBuff)
                seenState = true;
            else if (s.MoveState == MoveState.None && seenState)
                return true;
        }
        return false;
    }
}
