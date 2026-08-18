using System.Numerics;

namespace JumpHelper.Models;

/// <summary>
/// 移动速度状态（录制起跳瞬间检测并写入段数据；回放需状态一致才能复现起跳速度）。
/// 用户实测：带移速加成跳"常速录制"的段会跳过头（起跳速度偏高），状态必须完全一致。
/// </summary>
public enum MoveState
{
    /// <summary>无移速加成（常速）。</summary>
    None = 0,

    /// <summary>冲刺（Sprint：Action 3 / Status 50 系，非战斗 20s、60s CD，全职业，移速最高）。</summary>
    Sprint,

    /// <summary>慢跑/速行（Jog Status 4209 与 Peloton Status 1199/1985 同速合并；慢跑=冲刺结束后永久附加，
    /// 速行=弓/诗/机/舞职能技能 30s/5sCD）。回放缺状态时优先补速行（同速即生效）。</summary>
    SlowBuff,

    /// <summary>慢走（Walk 走路模式——输入限速，与移速 buff 正交；起跳速度由走路限速主导，跳距更短
    /// 如 0.74m 级超短跳）。与 buff 不同，走路模式是客户端本地状态（Control.IsWalking），
    /// 插件可自动切换（SetWalking），无需玩家手动。录制起跳瞬间检测，优先级高于 buff。</summary>
    Walk
}

/// <summary>
/// 一个跳跃段：起跳点 → 落点（录制时由插件自动采集玩家真实的跳跃过程）。
/// 回放时：走位到起跳点 → 转向 → 起跳 → 落地 → 微调对齐落点。
/// 起跳点/落点均为录制时的真实世界坐标，回放忠实重现玩家路径。
/// </summary>
public class JumpSegment
{
    /// <summary>起跳点坐标 X。</summary>
    public float TakeoffX { get; set; }

    /// <summary>起跳点坐标 Y。</summary>
    public float TakeoffY { get; set; }

    /// <summary>起跳点坐标 Z。</summary>
    public float TakeoffZ { get; set; }

    /// <summary>落点坐标 X。</summary>
    public float LandX { get; set; }

    /// <summary>落点坐标 Y。</summary>
    public float LandY { get; set; }

    /// <summary>落点坐标 Z。</summary>
    public float LandZ { get; set; }

    /// <summary>落地瞬间的朝向（弧度）——下一段起跳方向的参考。</summary>
    public float LandYaw { get; set; }

    /// <summary>冲刺起点坐标 X（起跳前最后一段直线冲刺的起点；原地跳时 ≈ 起跳点）。</summary>
    public float RunStartX { get; set; }

    /// <summary>冲刺起点坐标 Y。</summary>
    public float RunStartY { get; set; }

    /// <summary>冲刺起点坐标 Z。</summary>
    public float RunStartZ { get; set; }

    /// <summary>
    /// 起跳瞬间角色朝向（弧度）。回放面朝它直线冲刺——忠实复现"起跳瞬间速度矢量"，
    /// 不猜测"落点方向"（玩家斜向起跳/曲线助跑也能复现）。
    /// </summary>
    public float TakeoffYaw { get; set; }

    /// <summary>
    /// 起跳瞬间水平速度（m/s，XZ 平面）——验证"跳距 = 起跳速度 × 滞空时间"模型的关键数据。
    /// 连续跳/按住前进的起跳速度可能很高（与助跑距离无关），回放需按此匹配速度。
    /// </summary>
    public float TakeoffSpeed { get; set; }

    /// <summary>移动类型（保留扩展：Walk/原地跳等特殊段）。</summary>
    public MoveType Type { get; set; } = MoveType.RunJump;

    /// <summary>起跳瞬间的移速状态（录制自动检测写入；旧路线文件缺字段 = 0 = None，完全兼容）。
    /// 回放段前校验：状态不一致时自动补（冲刺/速行可施放）或提醒（详见 ReplayEngine.AwaitStatus）。</summary>
    public MoveState MoveState { get; set; }

    /// <summary>起跳点便捷属性。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Vector3 Takeoff => new(TakeoffX, TakeoffY, TakeoffZ);

    /// <summary>落点便捷属性。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Vector3 Land => new(LandX, LandY, LandZ);

    /// <summary>冲刺起点便捷属性。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Vector3 RunStart => new(RunStartX, RunStartY, RunStartZ);

    /// <summary>是否包含冲刺起点数据（旧版路线文件无此字段，全 0）。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasRunStart => RunStartX != 0f || RunStartY != 0f || RunStartZ != 0f;

    /// <summary>时间线起点坐标 X（录制时起跳前 InputLeadMs 的玩家位置；回放先对齐此点再重放输入）。</summary>
    public float StartX { get; set; }

    /// <summary>时间线起点坐标 Y。</summary>
    public float StartY { get; set; }

    /// <summary>时间线起点坐标 Z。</summary>
    public float StartZ { get; set; }

    /// <summary>
    /// 时间线起点朝向（弧度）——回放对齐时间线起点后先转向到此朝向再重放输入。
    /// 时间线里的 sumTurnLeft 是"相对当前朝向"的转向输入，初始朝向必须与录制一致，
    /// 否则转向从错误起点累加（方向偏甚至转反）。
    /// </summary>
    public float StartYaw { get; set; }

    /// <summary>
    /// 时间线起点时刻的水平速度（m/s，XZ 平面）。
    /// 回放先预助跑到该速度再开始重放时间线——录制时玩家落地速度连续（直接助跑），
    /// 回放若从静止起步，同样时间线走过的路程更短 → jump 帧触发时位置偏后（实测 0.7~0.9m），
    /// 极限跳跃距离不够。速度对齐后位置轨迹与录制一致。
    /// 0 = 原地跳/微调跳（起点静止），回放不预助跑直接重放。
    /// </summary>
    public float StartSpeed { get; set; }

    /// <summary>
    /// 全量输入时间线（相对 Start 时刻）：微调/助跑/起跳/空中/落地的完整输入序列。
    /// 空 = 旧数据，回放走冲刺起点/落点方向逻辑。
    /// </summary>
    public List<InputFrame> Inputs { get; set; } = new();

    /// <summary>
    /// 扩展时间线标记（null = 未设定/自动）：true = 该段时间线覆盖"上一段落点 → 起跳前"的行走
    /// （完整复现段间路径，适合机制确定的自装修跳跳乐；需扩展时间线开关开启）；
    /// false = 短时间线（起跳前 600ms，段间走半自动/玩家手动）。
    /// 录制自动采集时按段间位移自动判断写入；段落编辑可手动切换（重录生效）。
    /// </summary>
    public bool? Extended { get; set; }

    /// <summary>时间线起点便捷属性。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Vector3 StartPos => new(StartX, StartY, StartZ);

    /// <summary>是否包含输入时间线（新版路线）。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasTimeline => Inputs.Count > 0;
}
