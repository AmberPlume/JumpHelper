using System.Text.Json.Serialization;

namespace JumpHelper.Models;

/// <summary>节点间移动方式的类型。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MoveType
{
    /// <summary>直接走到下一节点。</summary>
    Walk,

    /// <summary>原地或短距离起跳。</summary>
    Jump,

    /// <summary>助跑后起跳（先沿朝向跑 runDist 米再起跳）。</summary>
    RunJump
}

/// <summary>
/// 从当前节点移动到下一节点的结构化参数。
/// 依据 FF14 确定性跳跃物理设计：起跳高度/滞空时间固定、无空中转向，
/// 空中唯一可变量是前进输入；因此本模型只需表达"朝向(由节点 Yaw 决定) + 助跑 + 前进输入时长"。
/// </summary>
public class MoveMode
{
    /// <summary>移动方式类型。</summary>
    public MoveType Type { get; set; } = MoveType.Jump;

    /// <summary>助跑距离（米），仅 <see cref="MoveType.RunJump"/> 有效。</summary>
    public float RunDist { get; set; }

    /// <summary>
    /// 空中按住前进输入的毫秒数；0 表示全程按住直到落地（落点最远、确定性最强）。
    /// 非 0 用于需要"跳近一点"的小平台场景。
    /// </summary>
    public int HoldForwardMs { get; set; }

    /// <summary>
    /// 到达本节点后延迟起跳的毫秒数（连跳节奏控制）；0 = 立即起跳。
    /// 落地检测与输入释放由回放引擎即时处理，此参数只控制"何时再跳"。
    /// </summary>
    public int JumpDelayMs { get; set; }
}
