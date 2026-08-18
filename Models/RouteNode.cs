using System.Numerics;

namespace JumpHelper.Models;

/// <summary>
/// 跳跳乐路线中的一个节点：一个确定的落脚点（世界坐标 + 朝向），
/// 以及"从本节点移动到下一节点"的移动方式。
/// </summary>
public class RouteNode
{
    /// <summary>节点名称（默认自动编号，可自定义）。</summary>
    public string Name { get; set; } = "";

    /// <summary>世界坐标 X（东向）。</summary>
    public float X { get; set; }

    /// <summary>世界坐标 Y（高度）。</summary>
    public float Y { get; set; }

    /// <summary>世界坐标 Z（南向）。</summary>
    public float Z { get; set; }

    /// <summary>朝向（弧度，yaw）。起跳前角色会对准该朝向。</summary>
    public float Yaw { get; set; }

    /// <summary>标记对应的段索引：该标记是该段（Segments[SegmentIndex]）的落点；-1 = 路线起点（还没跳过）。</summary>
    public int SegmentIndex { get; set; } = -1;

    /// <summary>前往下一节点的移动方式。末节点此项无意义（无下一节点）。</summary>
    public MoveMode? MoveToNext { get; set; }

    /// <summary>世界坐标便捷属性。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Vector3 Position => new(X, Y, Z);
}
