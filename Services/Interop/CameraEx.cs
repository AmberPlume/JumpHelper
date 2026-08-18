using System.Runtime.InteropServices;

namespace JumpHelper.Services.Interop;

/// <summary>
/// 游戏相机结构体扩展（显式布局，仅读取水平朝向）。
/// legacy 移动模式（MoveMode=1）下，角色移动方向跟随相机而非角色朝向，
/// 需要读取相机方位作为参考系。偏移量参考 ffxiv_navmesh（同一游戏版本）。
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 0x2B0)]
public struct CameraEx
{
    [FieldOffset(0x140)]
    public float DirH;
}
