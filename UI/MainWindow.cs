using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using JumpHelper.Services;
using JumpHelper.Utils;

namespace JumpHelper.UI;

/// <summary>
/// 主窗口（设置页）：主面板（显示/采集/移动状态开关）+ 参数设置（全部判定与物理参数）。
/// 日常操作（记录/保存/路线列表/段落编辑/执行）全在悬浮窗 FloatingPanel。
/// 说明文本全部改为 hover 显示（鼠标移到控件上出现 tooltip），保持界面简洁。
/// </summary>
public sealed class MainWindow : Window
{
    private readonly RouteStore _routeStore;

    /// <summary>悬浮窗显示开关回调（JumpHelper 挂接：勾选/取消勾选即开/关悬浮窗——悬浮窗关闭后不 Draw，
    /// 无法自同步配置，需由主窗口直接控制其 IsOpen）。</summary>
    public Action<bool>? OnFloatingPanelToggle { get; set; }

    public MainWindow(RouteStore routeStore)
        : base("跳跳乐助手")
    {
        _routeStore = routeStore;
    }

    public override void Draw()
    {
        if (ImGui.BeginTabBar("##ja_main"))
        {
            if (ImGui.BeginTabItem("主面板"))
            {
                DrawMainTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("参数设置"))
            {
                DrawParamsTab();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    // ===== 主面板：显示/采集/移动状态开关 =====

    private void DrawMainTab()
    {
        ImGui.Spacing();

        var showOverlay = Service.Config.ShowRouteOverlay;
        if (ImGui.Checkbox("显示路线标记", ref showOverlay))
        {
            Service.Config.ShowRouteOverlay = showOverlay;
            Service.Config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("世界绘制：段路径连线 + 段序号 + 起/终点图标（由悬浮窗起点/终点选择驱动）");

        var showPanel = Service.Config.ShowFloatingPanel;
        if (ImGui.Checkbox("显示悬浮窗", ref showPanel))
        {
            Service.Config.ShowFloatingPanel = showPanel;
            Service.Config.Save();
            OnFloatingPanelToggle?.Invoke(showPanel);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("主操作面：记录/保存/路线/段落/执行/标定跳");

        var autoSave = Service.Config.AutoSaveEvery;
        if (ImGui.DragInt("自动保存间隔（跳）", ref autoSave, 1f, 0, 100, autoSave > 0 ? $"{autoSave} 跳保存一次" : "关闭"))
        {
            Service.Config.AutoSaveEvery = autoSave;
            Service.Config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("每采集 N 段自动保存一次，0=关闭；切换/读档/卸载前的防丢保存跟随此开关");

        ImGui.Separator();

        var dropFell = Service.Config.DropFellSegments;
        if (ImGui.Checkbox("跌落段不记录", ref dropFell))
        {
            Service.Config.DropFellSegments = dropFell;
            Service.Config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("落点比起跳点低超过阈值即丢弃该段——跳跳乐有正常的下落跳场景，默认关闭，仅明确不需要下落跳的路线开启");
        if (dropFell)
        {
            DrawFloat("跌落判定高度差 m", "##felldrop",
                () => Service.Config.FellDropHeight, v => Service.Config.FellDropHeight = v, 0.1f, 0.5f, 20f,
                "落点比起跳点低超过此值视为跌落段并丢弃");
        }

        DrawFloat("段间行走分流阈值 m", "##longwalk",
            () => Service.Config.LongWalkDist, v => Service.Config.LongWalkDist = v, 0.1f, 0.5f, 20f,
            "落点→下一起跳点位移超过此值=长路径段（交玩家手动走，到位后自动继续）；短路径段自动衔接");
        DrawFloat("到位稳定确认 ms", "##awaitstable",
            () => Service.Config.AwaitStableMs, v => Service.Config.AwaitStableMs = v, 10f, 0f, 5000f,
            "到达起跳点附近后基本静止持续此时间才继续（防路过误触发）");

        var extTimeline = Service.Config.ExtendedTimeline;
        if (ImGui.Checkbox("扩展时间线（默认关）", ref extTimeline))
        {
            Service.Config.ExtendedTimeline = extTimeline;
            Service.Config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("段间长距离行走录进时间线完整复现（含拐弯/机关时序）——适合机制确定的自装修跳跳乐；段落编辑可逐段勾选「扩展」");

        ImGui.Separator();

        // 路线目录：显示当前路径 + 选择按钮（选择后即时生效并保存配置）
        ImGui.Text("路线目录");
        ImGui.SameLine();
        if (ImGui.Button("选择"))
        {
            var picked = Dialogs.PickFolder("选择路线目录");
            if (picked != null)
            {
                if (_routeStore.ChangeDirectory(picked))
                    Service.ChatGui.Print($"路线目录已修改: {picked}");
                else
                    Service.ChatGui.PrintError("路线目录修改失败（查看日志）");
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("选择路线文件存放目录（.json），即时生效并保存");
        ImGui.TextWrapped(_routeStore.RoutesDirectory);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("当前路线目录——录制/保存/加载路线都在此目录下进行");

        ImGui.Separator();
        ImGui.Text("移动状态");

        var autoCast = Service.Config.AutoCastMoveBuffs;
        if (ImGui.Checkbox("自动释放冲刺/速行", ref autoCast))
        {
            Service.Config.AutoCastMoveBuffs = autoCast;
            Service.Config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("录制与回放的移速状态必须一致（否则起跳速度不匹配跳过头/跳不够）。开启：回放缺冲刺/慢跑/速行时自动施放技能补齐并提醒；关闭：仅提醒不施放。常速段带状态会暂停等玩家处理");
    }

    // ===== 参数设置页：全部判定/物理参数 =====

    private void DrawParamsTab()
    {
        ImGui.TextDisabled("参数拖动即保存生效，回放中可实时调整观察");
        ImGui.Separator();

        DrawFloat("对齐容差 m", "##align",
            () => Service.Config.AlignTolerance, v => Service.Config.AlignTolerance = v, 0.001f, 0f, 0.2f,
            "起点/冲刺起点/落点走位对齐的判定容差（游戏位置精度 0.001m）");
        DrawFloat("Y 轴对齐容差 m", "##yalign",
            () => Service.Config.YAlignTolerance, v => Service.Config.YAlignTolerance = v, 0.01f, 0f, 1f,
            "读档起点高度匹配/到位判定/重录校验——路不平的地图放宽到 0.3，平整地图可调小");
        DrawFloat("起跳沿向窗口 m", "##along",
            () => Service.Config.TakeoffAlongTolerance, v => Service.Config.TakeoffAlongTolerance = v, 0.001f, 0f, 0.2f,
            "距起跳点 ≤ 此值或已越过即起跳（沿助跑方向）");
        DrawFloat("起跳横向偏差 m", "##lateral",
            () => Service.Config.TakeoffLateralTolerance, v => Service.Config.TakeoffLateralTolerance = v, 0.001f, 0f, 0.2f,
            "冲刺线到起跳点的垂直距离上限");
        DrawFloat("转向对准 rad", "##facing",
            () => Service.Config.FacingToleranceRad, v => Service.Config.FacingToleranceRad = v, 0.001f, 0f, 0.1f,
            "朝向偏差小于此值视为已对准（0.001 ≈ 0.057°；键盘转向过冲达不到时放宽）");

        ImGui.Separator();
        DrawFloat("落地成功判定 m", "##landtol",
            () => Service.Config.LandTolerance, v => Service.Config.LandTolerance = v, 0.01f, 0f, 2f,
            "落点距目标 ≤ 此值=成功，直接进下一段");
        DrawFloat("落地走位对齐上限 m", "##landwalk",
            () => Service.Config.LandWalkDist, v => Service.Config.LandWalkDist = v, 0.05f, 0.5f, 5f,
            "落点偏差 ≤ 此值自动走位对齐；超出=掉出平台，直接失败（不回起点重试，回跳无意义）");

        ImGui.Separator();
        DrawFloat("起跳累计上升 m", "##takedy",
            () => Service.Config.TakeoffDeltaY, v => Service.Config.TakeoffDeltaY = v, 0.01f, 0.01f, 1f,
            "跳跃判定：离地高度累计超过此值视为起跳");
        DrawFloat("下落累计下降 m", "##descacc",
            () => Service.Config.DescendAccumY, v => Service.Config.DescendAccumY = v, 0.01f, 0.01f, 1f,
            "落地判定前置：下降量累计超过此值进入下落阶段");
        DrawFloat("落地回稳单帧下降 m", "##descsend",
            () => Service.Config.DescendEndDeltaY, v => Service.Config.DescendEndDeltaY = v, 0.001f, 0f, 0.2f,
            "下落阶段单帧下降 ≤ 此值=已落地（结束滞空进落地校验）");

        ImGui.Separator();
        ImGui.Text("时间线预助跑（起点速度对齐）");
        DrawFloat("预助跑速度阈值 m/s", "##prunemin",
            () => Service.Config.PreRunSpeedMin, v => Service.Config.PreRunSpeedMin = v, 0.01f, 0f, 1f,
            "时间线起点速度低于此值=原地跳/微调跳，不预助跑直接重放（主流段，164/166）");
        DrawFloat("起点后移系数 d=系数×v²", "##prunefac",
            () => Service.Config.PreRunDistFactor, v => Service.Config.PreRunDistFactor = v, 0.001f, 0f, 0.1f,
            "预助跑起点 = 时间线起点后方 d 处。实测加速曲线 d≈0.02v²（0.16m→3.04m/s）；起跳帧仍偏时微调");
        DrawFloat("达标容差 ×录制速度", "##speedmatch",
            () => Service.Config.SpeedMatchTolerance, v => Service.Config.SpeedMatchTolerance = v, 0.01f, 0.5f, 1f,
            "回放速度 ≥ 录制起点速度×此值 即开始时间线重放（0.95+单帧达标，残余偏差由落地走位吸收）");
        DrawFloat("达标兜底 m", "##pruneover",
            () => Service.Config.PreRunOvershoot, v => Service.Config.PreRunOvershoot = v, 0.05f, 0f, 2f,
            "越过时间线起点此距离仍未达标→强制开始时间线（防卡死；速度过低会止损失败并诊断）");
    }

    private static void DrawFloat(string label, string id, Func<float> get, Action<float> set,
                                  float speed, float min, float max, string? tooltip = null)
    {
        var v = get();
        if (ImGui.DragFloat(label + id, ref v, speed, min, max, "%.3f"))
        {
            set(v);
            Service.Config.Save();
        }
        if (tooltip != null && ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);
    }
}
