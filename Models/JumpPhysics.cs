namespace JumpHelper.Models;

/// <summary>
/// FF14 跳跃物理常量（经游戏内实测标定）：
/// 跳跃距离由起跳瞬间水平速度决定（空中前进无效）；助跑 ≥2m 即达最大跑速，
/// 饱和跳跃水平距离 ≈ 4m；滞空时间 ≈ 687ms 固定。
/// 总前进 = 助跑距离 + 饱和跳距。
/// </summary>
public static class JumpPhysics
{
    /// <summary>饱和跳跃水平距离（米）。</summary>
    public const float MaxJumpDistance = 4.0f;
}
