using Dalamud.Configuration;
using JumpHelper.Models;

namespace JumpHelper;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    /// <summary>
    /// 回放时"落地成功"的判定阈值（米）：落地后角色与目标节点的水平距离（XZ 平面）小于该值即视为这一跳成功。
    /// 注意这不是平台尺寸——小平台场景下阈值需小于平台宽度（如 0.5m），
    /// 否则会把"落在平台边缘"误判为成功，导致下一跳起跳点不准。
    /// </summary>
    public float LandTolerance { get; set; } = 0.5f;

    /// <summary>
    /// 路线文件存放目录；留空（null/空白）时使用插件配置目录下的 Routes/。
    /// 支持自定义路径便于观测与共享路线文件。
    /// </summary>
    public string? RouteDirectory { get; set; }

    // ===== 容差/判定参数（UI「容差设置」页可调，即时生效） =====

    /// <summary>对齐容差（米）：起点标记/冲刺起点/重试回起点/落点走位对齐共用。
    /// 与游戏位置精度（0.001m）同量级——v1 起从 0.01 收紧到 0.001。</summary>
    public float AlignTolerance { get; set; } = 0.001f;

    /// <summary>
    /// Y 轴对齐容差（米，默认 0.3）：回放时玩家高度与目标高度差在此范围内视为"同平台/已对齐"——
    /// 用于读档起点高度匹配、等待玩家到位判定、重录起点校验。
    /// 跳跳乐路不平的地图（地面高度波动）严格 0.01m 对齐会误拒/卡死，放宽到此值；平整地图可调小。
    /// 注：这是"平台判定"不是位置精度——游戏位置精度 0.001m，但路不平地图 Y 必须放宽（用户主动要求）。
    /// </summary>
    public float YAlignTolerance { get; set; } = 0.3f;

    /// <summary>起跳判定·沿冲刺方向窗口（米）：距起跳点 ≤ 此值或已越过即起跳。v1 起 0.01→0.001。</summary>
    public float TakeoffAlongTolerance { get; set; } = 0.001f;

    /// <summary>起跳判定·横向偏差上限（米）：冲刺线到起跳点的垂直距离须小于此值。v1 起 0.01→0.001。</summary>
    public float TakeoffLateralTolerance { get; set; } = 0.001f;

    /// <summary>转向对准精度（弧度）：朝向偏差小于此值视为已对准。v1 起 0.01→0.001（≈0.057°）。</summary>
    public float FacingToleranceRad { get; set; } = 0.001f;

    /// <summary>落地偏离但在平台上的走位对齐上限（米）：超过则判定重试。</summary>
    public float LandWalkDist { get; set; } = 2.5f;

    /// <summary>起跳检测阈值（米）：累计上升超此值视为起跳。</summary>
    public float TakeoffDeltaY { get; set; } = 0.2f;

    /// <summary>下落确认阈值（米）：累计下降超此值视为确认下落。</summary>
    public float DescendAccumY { get; set; } = 0.15f;

    /// <summary>落地回稳阈值（米）：下落中单帧下降小于此值视为落地。</summary>
    public float DescendEndDeltaY { get; set; } = 0.02f;

    // ===== 时间线预助跑（起点速度对齐：时间戳起跳位置轨迹对齐） =====

    /// <summary>预助跑阈值（m/s）：时间线起点速度低于此值视为原地跳/微调跳，不预助跑直接重放。</summary>
    public float PreRunSpeedMin { get; set; } = 0.3f;

    /// <summary>预助跑起点后移系数：d = 系数 × v²（米）。实测加速曲线 d≈0.02v²（0.16m 助跑→3.04m/s）。</summary>
    public float PreRunDistFactor { get; set; } = 0.02f;

    /// <summary>预助跑达标容差（×录制起点速度）：回放速度 ≥ 录制速度×此值即开始时间线。
    /// 0.95 + 单帧达标：原 0.92+连续2帧 因速度采样抖动（2.2~2.9 区间 streak 断）实际 2.96 才触发，
    /// 加上时间线 0~1ms 助跑输入再加速 → jump 帧 3.35 超调 39%（实测段 1 跳过头）。收紧+放宽时机更贴近目标。</summary>
    public float SpeedMatchTolerance { get; set; } = 0.95f;

    /// <summary>预助跑兜底（米）：沿起始朝向越过原时间线起点此距离仍未达标 → 强制开始时间线（防卡死）。</summary>
    public float PreRunOvershoot { get; set; } = 0.5f;

    // ===== 世界标记绘制 =====

    /// <summary>是否在游戏内绘制路线节点/段路径（世界标记，SPlatoon 风格）。</summary>
    public bool ShowRouteOverlay { get; set; } = true;

    // ===== 悬浮窗（主操作面） =====

    /// <summary>是否显示悬浮窗（主操作面；关闭后可用主窗口「设置」页重新打开）。</summary>
    public bool ShowFloatingPanel { get; set; } = true;

    /// <summary>
    /// 自动保存间隔（跳数）：每采集 N 段自动保存一次路线；0 = 关闭自动保存（仅手动「保存路线」落盘）。
    /// 切换路线/新建/读档/卸载前的"关键时刻"防丢保存跟随此开关（0 时同样关闭）。
    /// </summary>
    public int AutoSaveEvery { get; set; } = 5;

    // ===== 采集过滤 =====

    /// <summary>跌落段自动丢弃开关：开启后，落点比起跳点低超过 FellDropHeight 的段不记录
    /// （跳跳乐场景"从高处往低处跳"是正常跳跃，故默认关闭——只在明确不需要下落跳的路线开启）。</summary>
    public bool DropFellSegments { get; set; }

    /// <summary>跌落段判定高度差（米）：落点 Y - 起跳点 Y < -此值 视为跌落段（配合 DropFellSegments）。</summary>
    public float FellDropHeight { get; set; } = 2.0f;

    /// <summary>长路径分流阈值（米）：上一段落点 → 本段起跳点的水平位移超过此值 = 长路径段
    /// （段间需长距离行走/拐弯/换平台）→ 默认（扩展关闭）回放等待玩家手动走到位（半自动）；
    /// 扩展时间线开启时该段改为录制行走完整复现。</summary>
    public float LongWalkDist { get; set; } = 3.0f;

    /// <summary>
    /// 等待玩家到位稳定时间（毫秒，默认 500）：玩家到达下一段起跳点附近后需**基本静止**持续此时间
    /// 才确认到位并自动继续读档——防止"路过起跳点"误触发抢控制（玩家只是经过也会短暂进入范围）。
    /// </summary>
    public float AwaitStableMs { get; set; } = 500f;

    /// <summary>
    /// 扩展时间线开关（默认关闭）：开启后，段间长距离（位移 > LongWalkDist）的段在录制时把
    /// "上一段落点 → 起跳前"的行走输入也录进时间线，回放完整复现（含拐弯/机关时序）——
    /// 适合机制确定、可精确复现的自装修跳跳乐等高精度场景。
    /// 默认关闭：短时间线高效 + 段间长距离交玩家手动走（半自动，跳跳乐地图机制复杂时更可靠）。
    /// </summary>
    public bool ExtendedTimeline { get; set; }

    // ===== 移动状态（冲刺/慢跑/速行——起跳速度必须与录制一致，否则跳过头/跳不够） =====

    /// <summary>
    /// 自动释放冲刺开关（默认关闭）：回放段需要冲刺而玩家当前没有时，自动施放冲刺技能
    /// （CD 前置检查，施放后校验 buff 并聊天提醒）。关闭后：仅聊天提醒，不施放任何技能
    /// （玩家手动处理；未处理则回放因起跳速度不匹配失败）。
    /// 注意：**速行（Peloton）永不自动施放**——跳跳乐副本内禁用速行，且慢跑持续时间无限；
    /// 速行只作为"需要检测并提醒玩家"的影响速度状态（玩家手动施放，或施放冲刺等其结束变慢跑）。
    /// </summary>
    public bool AutoCastMoveBuffs { get; set; }

    /// <summary>上次云端上传使用的上传人昵称（上传弹窗预填，免每次重输）。</summary>
    public string CloudUploaderNickname { get; set; } = "";

    /// <summary>已上传路线的删除令牌（路线名→del_token，仅本机保存——删除自己上传的云端路线用，
    /// 由云函数用主密钥 HMAC(路线名) 生成并随上传下发，玩家无法伪造；不存云端防公开泄露）。</summary>
    public Dictionary<string, string> CloudDelTokens { get; set; } = new();

    // ===== 段落记录方式（碎片/线性，二者参数互相独立） =====

    /// <summary>
    /// 段落记录方式：线性（按序号顺序依次跳跃，默认）/ 碎片（段落有序号但仅作"名字"，不依赖序号；
    /// 落地后按"落点→起跳点"距离与高度自动衔接下一步，岔路让玩家选，找不出可衔接下阶段即终止）。
    /// 详见 <see cref="SegmentMode"/>。
    /// </summary>
    public SegmentMode SegmentMode { get; set; } = SegmentMode.Linear;

    /// <summary>碎片模式·Y 对齐容差（米）：与线性独立。碎片多起跳点近场易混淆，须收紧（默认 0.2，线性 0.3）。</summary>
    public float FragYAlignTolerance { get; set; } = 0.2f;

    /// <summary>碎片模式·水平衔接距离（米）：本段落点 → 下一起跳点水平距离 ≤ 此值才自动衔接（默认 3m）。</summary>
    public float FragLinkDistXZ { get; set; } = 3f;

    /// <summary>碎片模式·垂直衔接距离（米）：本段落点 → 下一起跳点 |ΔY| ≤ 此值才自动衔接（Y 绝不忽略）。</summary>
    public float FragLinkDistY { get; set; } = 1f;

    /// <summary>线性模式·插入段高度差（米）：「插入新段」找"就近落点段"时，|当前位置 Y − 落点 Y| ≤ 此值才视为候选。
    /// 默认 0.2（比线性 Y 对齐 0.3 更紧——插入基准必须精确锁定到目标平台，近场多个落点不宜混淆）。</summary>
    public float InsertSegmentYAlign { get; set; } = 0.2f;

    public void Save()
    {
        Service.PluginInterface.SavePluginConfig(this);
    }
}
