namespace JumpHelper.Utils;

public static class AngleExtensions
{
    public static Angle Radians(this float radians) => new(radians);

    public static Angle Degrees(this float degrees) => new(degrees * Angle.DEG_TO_RAD);

    public static Angle Degrees(this int degrees) => new(degrees * Angle.DEG_TO_RAD);
}
