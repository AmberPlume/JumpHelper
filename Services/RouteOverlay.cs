using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using JumpHelper.Models;

namespace JumpHelper.Services;

/// <summary>
/// 世界标记绘制（参考 SPlatoon ImGuiLegacy 方案）：
/// 在游戏内以世界坐标绘制当前路线的段路径（起跳点→落点）与起终点标记，
/// 帮助玩家观察路线结构、确认回放起终点（起点/终点图标醒目，告诉玩家该去哪）、判断当前回放位置。
/// 实现：IGameGui.WorldToScreen 世界坐标 → 屏幕坐标，ImGui.GetBackgroundDrawList() 绘制
/// （全屏覆盖层，不拦截输入）。
/// 绘制内容：
///   - 段路径：起跳点(蓝) → 落点(白) 连线，回放中当前段高亮（黄粗线）；
///   - 起点段起跳点：绿色大圆 + 「起点」文字；终点段落点：红色大圆 + 「终点」文字（仅当前地图绘制）。
/// </summary>
public sealed class RouteOverlay : IDisposable
{
    private readonly ReplayEngine _replay;
    private readonly RecorderService _recorder;
    private readonly IPluginLog _log;

    /// <summary>要绘制的路线（MainWindow 每帧同步：非录制时 = 选中的加载路线）。录制中/回放中由本类直接读取服务，不依赖窗口可见。</summary>
    public RouteFile? Route { get; set; }

    /// <summary>起点段索引（-1 = 无选择）——该段起跳点绘制绿色「起点」标记。</summary>
    public int StartSegment { get; set; } = -1;

    /// <summary>终点段索引（-1 = 无选择）——该段落点绘制红色「终点」标记。</summary>
    public int EndSegment { get; set; } = -1;

    public RouteOverlay(ReplayEngine replay, RecorderService recorder)
    {
        _replay = replay;
        _recorder = recorder;
        _log = Service.Log;
        Service.PluginInterface.UiBuilder.Draw += Draw;
        PluginLog.Info("RouteOverlay: 世界标记绘制已注册");
    }

    private void Draw()
    {
        if (!Service.Config.ShowRouteOverlay)
            return;
        if (Service.ObjectTable.LocalPlayer == null)
            return;

        // 路线来源优先级：回放中 = 回放路线；录制中 = 当前录制路线（新建路线即时反映，不依赖主窗口打开）；
        // 否则 = UI 选中的已保存路线（MainWindow 每帧同步）。
        var route = _replay.CurrentRoute ?? _recorder.CurrentRoute ?? Route;
        if (route == null || route.Segments.Count == 0)
            return;
        // 仅当前地图绘制（路线绑定 TerritoryId）
        if (route.TerritoryId != Service.ClientState.TerritoryType)
            return;

        var dl = ImGui.GetBackgroundDrawList();
        var font = ImGui.GetFont(); // 移出循环（GetFont 每帧每段调用是纯浪费）
        var replayActive = _replay.State != ReplayState.Idle && _replay.State != ReplayState.Failed;
        // 距离粗筛：玩家与段中点水平距离超过此值直接跳过（WorldToScreen 对远处物体也有矩阵运算开销，
        // 跳跳乐段间距通常 <10m，80m 覆盖视野内所有段——远处段不画也看不见）
        const float MaxOverlayDist = 80f;
        const float MaxOverlayDistSq = MaxOverlayDist * MaxOverlayDist;
        var playerPos = Service.ObjectTable.LocalPlayer.Position;

        // 1. 段路径：起跳点(模式色) → 落点(白/模式色) 连线；回放当前段黄粗线；起点段绿、终点段红。
        // 模式着色区分（避免"忘记录的是哪个模式"混淆）：线性 = 淡蓝（藏青浅化）、碎片 = 淡橙（亮橙浅化）。
        // 线本体也按模式染色（需求9：染色对象是 Overlay 的线）——半透明保持不抢眼、起终点/当前段高亮不变。
        var isFragment = Service.Config.SegmentMode == SegmentMode.Fragment;
        var accentCol = isFragment ? 0xFFEBB064u : 0xFF7F9CD9;   // 模式主色：碎片淡橙 / 线性淡蓝
        var accentHover = isFragment ? 0xFFF3CF9E : 0xFFB4C7ED;  // 模式浅色（序号文字等）
        var lineCol = isFragment
            ? (replayActive ? 0x90EBB064u : 0x70EBB064u)   // 碎片淡橙（半透明）
            : (replayActive ? 0x907F9CD9u : 0x707F9CD9u);  // 线性淡蓝（半透明）
        for (int i = 0; i < route.Segments.Count; i++)
        {
            var seg = route.Segments[i];
            var isCurrent = replayActive && i == _replay.CurrentSegment;
            // 起终点段高亮（优先于普通色，回放当前段最高优先）
            uint segCol = isCurrent ? 0xFFFFD040u
                        : i == StartSegment ? 0xFF40FF60u
                        : i == EndSegment ? 0xFFFF5040u
                        : lineCol;
            var thick = isCurrent ? 3f : (i == StartSegment || i == EndSegment ? 2.5f : 1.5f);

            // 距离粗筛（在 WorldToScreen 之前，省掉远处段的全部绘制开销）
            var mid = (seg.Takeoff + seg.Land) * 0.5f;
            var dToPlayer = playerPos - mid;
            dToPlayer.Y = 0;
            if (dToPlayer.LengthSquared() > MaxOverlayDistSq)
                continue;

            // 每个端点只调一次 WorldToScreen（原实现 takeoff/land 各调 2 次，浪费一半）
            var takeoffVisible = TryWorldToScreen(seg.Takeoff, out var takeoffScr);
            var landVisible = TryWorldToScreen(seg.Land, out var landScr);
            if (takeoffVisible && landVisible)
            {
                dl.AddLine(takeoffScr, landScr, segCol, thick);
            }
            if (takeoffVisible)
                dl.AddCircleFilled(takeoffScr, isCurrent ? 5f : 3f, isCurrent ? 0xFFFFD040u : accentCol, 16);
            if (landVisible)
                dl.AddCircleFilled(landScr, isCurrent ? 5f : 3f, isCurrent ? 0xFFFFD040u : (isFragment ? accentCol : 0xFFFFFFFFu), 16);

            // 段序号（两种模式都画，作为"段名"；用不同色区分模式——线性藏青、碎片亮橙）：
            // 段中点沿垂线（水平面内垂直于线段的方向）正方向偏移 0.05m——序号贴在线段一侧。
            // 放大 + 黑描边（用户反馈序号不明显——段落是玩家参考的基本单位，序号要大而清晰）。
            var segDir = seg.Land - seg.Takeoff;
            segDir.Y = 0;
            var segDirLen = segDir.Length();
            if (segDirLen > 0.001f)
            {
                var normal = new Vector3(-segDir.Z, 0f, segDir.X) / segDirLen; // 水平法线（固定一侧）
                mid += normal * 0.05f;
            }
            if (TryWorldToScreen(mid, out var midScr))
            {
                const float numSize = 20f;
                var numCol = isCurrent ? 0xFFFFD040u : accentHover;
                var text = (i + 1).ToString(); // 段号从 1 开始（固定编号避免每帧字符串插值分配）
                // 黑描边（4 方向偏移）提高对比度
                foreach (var off in new[] { new Vector2(-1, 0), new Vector2(1, 0), new Vector2(0, -1), new Vector2(0, 1) })
                    dl.AddText(font, numSize, midScr + off, 0xFF000000u, text);
                dl.AddText(font, numSize, midScr, numCol, text);
            }
        }

        // 2. 起终点标记（醒目图标 + 文字，告诉玩家该去哪）：
        //    起点段起跳点 = 绿色大圆 + 「起点」；终点段落点 = 红色大圆 + 「终点」
        if (StartSegment >= 0 && StartSegment < route.Segments.Count)
        {
            var startPos = route.Segments[StartSegment].Takeoff;
            startPos.Y += 1.2f; // 抬高避免与段端点圆点重叠
            if (TryWorldToScreen(startPos, out var startScr))
            {
                dl.AddCircleFilled(startScr, 9f, 0xFF40FF60u, 24);
                dl.AddCircle(startScr, 12f, 0xCC000000u, 24, 2f);
                dl.AddText(startScr + new Vector2(14f, -12f), 0xFF40FF60u, "起点");
            }
        }
        if (EndSegment >= 0 && EndSegment < route.Segments.Count)
        {
            var endPos = route.Segments[EndSegment].Land;
            endPos.Y += 1.2f;
            if (TryWorldToScreen(endPos, out var endScr))
            {
                dl.AddCircleFilled(endScr, 9f, 0xFFFF5040u, 24);
                dl.AddCircle(endScr, 12f, 0xCC000000u, 24, 2f);
                dl.AddText(endScr + new Vector2(14f, -12f), 0xFFFF5040u, "终点");
            }
        }
    }

    private static bool TryWorldToScreen(Vector3 world, out Vector2 screen)
        => Service.GameGui.WorldToScreen(world, out screen);

    public void Dispose()
    {
        Service.PluginInterface.UiBuilder.Draw -= Draw;
    }
}
