using System.Numerics;

namespace JumpHelper.Utils;

/// <summary>
/// 角度工具（弧度制）。自研实现，参考 ffxiv_navmesh 的 Angle 但独立编写。
/// FF14 坐标系：X 东向、Z 南向，角色朝向 yaw 为弧度。
/// </summary>
public struct Angle
{
    public const float RAD_TO_DEG = 180f / MathF.PI;
    public const float DEG_TO_RAD = MathF.PI / 180f;

    public float Rad;

    public float Deg => Rad * RAD_TO_DEG;

    public Angle(float radians)
    {
        Rad = radians;
    }

    /// <summary>从 XZ 平面向量计算方位角（世界坐标系）。</summary>
    public static Angle FromDirectionXZ(Vector3 dir) => new(MathF.Atan2(dir.X, dir.Z));

    /// <summary>从 X/Y 向量计算角度。</summary>
    public static Angle FromDirection(Vector2 dir) => new(MathF.Atan2(dir.X, dir.Y));

    /// <summary>角度转单位方向向量（X=左分量, Y=前分量）。</summary>
    public Vector2 ToDirection() => new(Sin(), Cos());

    public float Sin() => MathF.Sin(Rad);

    public float Cos() => MathF.Cos(Rad);

    /// <summary>归一化到 [-π, π]。</summary>
    public Angle Normalized()
    {
        var r = Rad;
        while (r < -MathF.PI)
            r += 2 * MathF.PI;
        while (r > MathF.PI)
            r -= 2 * MathF.PI;
        return new Angle(r);
    }

    public static Angle operator +(Angle a, Angle b) => new(a.Rad + b.Rad);

    public static Angle operator -(Angle a, Angle b) => new(a.Rad - b.Rad);

    public static Angle operator -(Angle a) => new(-a.Rad);

    public static Angle operator *(Angle a, float b) => new(a.Rad * b);

    public static Angle operator *(float a, Angle b) => new(a * b.Rad);

    public static Angle operator /(Angle a, float b) => new(a.Rad / b);
}
