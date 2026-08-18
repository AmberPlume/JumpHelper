using System.Numerics;

namespace JumpHelper.Models;

/// <summary>
/// 输入时间线帧：某一时刻玩家的完整输入状态（相对段起始的毫秒）。
/// 采集自 RMIWalk hook 的 Original 返回值（玩家真实输入）+ 空格键盘缓冲。
/// 回放时按时间戳逐帧重放——忠实复现录制时的微调/助跑/跳跃/空中全部操作。
/// </summary>
public class InputFrame
{
    /// <summary>相对段起始时刻的毫秒数。</summary>
    public long TimeMs { get; set; }

    /// <summary>水平输入（sumLeft，-1~1）。</summary>
    public float Left { get; set; }

    /// <summary>前进输入（sumForward，-1~1）。</summary>
    public float Forward { get; set; }

    /// <summary>转向输入（sumTurnLeft，-1~1）。旧数据（无 Yaw）时回放用它做相对转向。</summary>
    public float Turn { get; set; }

    /// <summary>空格（跳跃）是否按住。</summary>
    public bool Jump { get; set; }

    /// <summary>
    /// 本帧玩家真实朝向（弧度）——绝对朝向时间线。
    /// 回放逐帧把角色朝向修正到录制朝向（不依赖相对转向累积，无逐段角度漂移；
    /// 同时复现录制时鼠标转向——鼠标转向不产生 sumTurnLeft 输入，旧方案会丢失）。
    /// null = 旧路线数据，回放回退用 Turn 相对转向。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public float? Yaw { get; set; }
}
