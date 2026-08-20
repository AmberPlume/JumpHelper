using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using JumpHelper.Models;
using JumpHelper.Services;
using JumpHelper.Utils;

namespace JumpHelper.UI;

/// <summary>
/// 主窗口（设置页）：主面板（显示/采集/移动状态开关）+ 参数设置（全部判定与物理参数）。
/// 日常操作（记录/保存/路线列表/段落编辑/路线回放）全在悬浮窗 FloatingPanel。
/// 说明文本全部改为 hover 显示（鼠标移到控件上出现 tooltip），保持界面简洁。
/// </summary>
public sealed class MainWindow : Window
{
    private readonly RouteStore _routeStore;
    private bool _suggestOpen;    // 提出建议弹窗
    private string _suggestText = ""; // 建议内容

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
        // 每次打开设置窗口都弹到屏幕中间（避免弹出在角落被漏看）
        ImGui.SetNextWindowPos(ImGui.GetMainViewport().GetCenter(), ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));

        if (ImGui.BeginTabBar("##ja_main"))
        {
            if (ImGui.BeginTabItem("基础设置"))
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

        // 提出建议弹窗（基础设置「提出建议」打开，文字不限）
        if (_suggestOpen)
        {
            ImGui.SetNextWindowPos(ImGui.GetMainViewport().GetCenter(), ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
            ImGui.SetNextWindowSize(new Vector2(420, 0), ImGuiCond.FirstUseEver);
            if (ImGui.Begin("提出建议", ref _suggestOpen))
            {
                ImGui.Text("建议/反馈内容（文字不限）");
                ImGui.SetNextItemWidth(380f);
                ImGui.InputTextMultiline("##suggest_text", ref _suggestText, 2000, new Vector2(380, 120));
                ImGui.Spacing();
                var w2 = ImGui.GetContentRegionAvail().X;
                var halfW2 = (w2 - ImGui.GetStyle().ItemSpacing.X) * 0.5f;
                var canGo = _suggestText.Trim().Length > 0;
                if (!canGo) ImGui.BeginDisabled();
                if (ImGui.Button("提交", new Vector2(halfW2, 0)))
                {
                    if (CloudRouteService.SendSuggestion(_suggestText.Trim()))
                    {
                        Service.ChatGui.Print("建议已提交，感谢！");
                        _suggestText = "";
                        _suggestOpen = false;
                    }
                    else
                        Service.ChatGui.PrintError("提交失败（查看日志）");
                }
                if (!canGo) ImGui.EndDisabled();
                ImGui.SameLine();
                if (ImGui.Button("取消", new Vector2(halfW2, 0)))
                    _suggestOpen = false;
                ImGui.End();
            }
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
            ImGui.SetTooltip("主操作面：记录/保存/路线/段落/路线回放");

        var autoSave = Service.Config.AutoSaveEvery;
        if (ImGui.DragInt("自动保存间隔（跳）", ref autoSave, 1f, 0, 100, autoSave > 0 ? $"{autoSave} 跳保存一次" : "关闭"))
        {
            Service.Config.AutoSaveEvery = autoSave;
            Service.Config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("每采集 N 段自动保存一次，0=关闭；切换/读档/卸载前的防丢保存跟随此开关");

        var extTimeline = Service.Config.ExtendedTimeline;
        if (ImGui.Checkbox("扩展时间线（默认关）##exttimeline", ref extTimeline))
        {
            Service.Config.ExtendedTimeline = extTimeline;
            Service.Config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("段间长距离行走录进时间线完整复现（含拐弯/机关时序）——适合机制确定的自装修跳跳乐；段落编辑可逐段勾选「扩展」");

        ImGui.Separator();

        // 角色操作模式：直接写游戏配置 MoveMode（0=标准 1=传统），立即生效；加载路线时会自动切到路线录制模式
        ImGui.Text("角色操作模式");
        var curLegacy = Service.GameConfig.UiControl.TryGetUInt("MoveMode", out var moveMode) && moveMode == 1;
        var modeBtnW = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) * 0.5f;
        if (!curLegacy) ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.62f, 0.72f, 0.87f, 1f)); // 当前=标准 淡蓝
        if (ImGui.Button("标准模式", new Vector2(modeBtnW, 0)))
            SwitchMoveMode(0);
        if (!curLegacy) ImGui.PopStyleColor();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("移动相对角色朝向——跳跳乐助手路线均按标准模式录制/回放（推荐）");
        ImGui.SameLine();
        if (curLegacy) ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.96f, 0.76f, 0.48f, 1f)); // 当前=传统 淡橙
        if (ImGui.Button("传统模式", new Vector2(modeBtnW, 0)))
            SwitchMoveMode(1);
        if (curLegacy) ImGui.PopStyleColor();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("移动相对相机方位——回放方向不可复现，传统模式录的路线回放必偏");

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
        if (ImGui.Checkbox("自动释放冲刺（默认关）##autocast", ref autoCast))
        {
            Service.Config.AutoCastMoveBuffs = autoCast;
            Service.Config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("回放段需要冲刺而当前没有时自动施放冲刺（默认关闭=仅提醒）。速行永不自动施放——副本内禁用且慢跑无限，只做检测提醒（玩家手动施放，或施放冲刺等其结束变慢跑）");

        // 提出建议（基础设置最下面）：向维护者提建议/反馈问题，文字不限
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        if (ImGui.Button("提出建议", new Vector2(ImGui.GetContentRegionAvail().X, 36)))
            _suggestOpen = true;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("向维护者提出建议/反馈问题（文字不限）");
    }

    // ===== 参数设置页：全部判定/物理参数 =====

    private void DrawParamsTab()
    {
        ImGui.TextDisabled("参数拖动即保存生效，回放中可实时调整观察");
        ImGui.Separator();

        // ===== 核心功能（线性/碎片共用）：这一跳能否成功 / 起点速度对齐 =====
        ImGui.Text("核心功能");
        ImGui.Separator();
        DrawFloat("对齐容差 m", "##align",
            () => Service.Config.AlignTolerance, v => Service.Config.AlignTolerance = v, 0.001f, 0f, 0.2f,
            "起点/冲刺起点/落点走位对齐的判定容差（游戏位置精度 0.001m）");
        DrawFloat("起跳沿向窗口 m", "##along",
            () => Service.Config.TakeoffAlongTolerance, v => Service.Config.TakeoffAlongTolerance = v, 0.001f, 0f, 0.2f,
            "距起跳点 ≤ 此值或已越过即起跳（沿助跑方向）");
        DrawFloat("起跳横向偏差 m", "##lateral",
            () => Service.Config.TakeoffLateralTolerance, v => Service.Config.TakeoffLateralTolerance = v, 0.001f, 0f, 0.2f,
            "冲刺线到起跳点的垂直距离上限");
        DrawFloat("转向对准 rad", "##facing",
            () => Service.Config.FacingToleranceRad, v => Service.Config.FacingToleranceRad = v, 0.001f, 0f, 0.1f,
            "朝向偏差小于此值视为已对准（0.001 ≈ 0.057°；键盘转向过冲达不到时放宽）");
        DrawFloat("落地成功判定 m", "##landtol",
            () => Service.Config.LandTolerance, v => Service.Config.LandTolerance = v, 0.01f, 0f, 2f,
            "落点距目标 ≤ 此值=成功，直接进下一段");
        DrawFloat("落地走位对齐上限 m", "##landwalk",
            () => Service.Config.LandWalkDist, v => Service.Config.LandWalkDist = v, 0.05f, 0.5f, 5f,
            "落点偏差 ≤ 此值自动走位对齐；超出=掉出平台，直接失败（不回起点重试，回跳无意义）");
        DrawFloat("起跳累计上升 m", "##takedy",
            () => Service.Config.TakeoffDeltaY, v => Service.Config.TakeoffDeltaY = v, 0.01f, 0.01f, 1f,
            "跳跃判定：离地高度累计超过此值视为起跳");
        DrawFloat("下落累计下降 m", "##descacc",
            () => Service.Config.DescendAccumY, v => Service.Config.DescendAccumY = v, 0.01f, 0.01f, 1f,
            "落地判定前置：下降量累计超过此值进入下落阶段");
        DrawFloat("落地回稳单帧下降 m", "##descsend",
            () => Service.Config.DescendEndDeltaY, v => Service.Config.DescendEndDeltaY = v, 0.001f, 0f, 0.2f,
            "下落阶段单帧下降 ≤ 此值=已落地（结束滞空进落地校验）");

        ImGui.Spacing();
        DrawFloat("段间行走分流阈值 m", "##longwalk",
            () => Service.Config.LongWalkDist, v => Service.Config.LongWalkDist = v, 0.1f, 0.5f, 20f,
            "落点→下一起跳点位移超过此值=长路径段（交玩家手动走，到位后自动继续）；短路径段自动衔接");
        DrawFloat("到位稳定确认 ms", "##awaitstable",
            () => Service.Config.AwaitStableMs, v => Service.Config.AwaitStableMs = v, 10f, 0f, 5000f,
            "到达起跳点附近后基本静止持续此时间才继续（防路过误触发）");

        ImGui.Spacing();
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

        ImGui.Separator();

        // ===== 当前模式设置（线性/碎片互相独立，显示当前模式） =====
        ImGui.Text($"模式专用设置（当前：{Service.Config.SegmentMode switch
        {
            SegmentMode.Linear => "线性",
            SegmentMode.Fragment => "碎片",
            _ => "线性"
        }}）");
        if (Service.Config.SegmentMode == SegmentMode.Linear)
        {
            DrawFloat("线性·Y 对齐容差 m", "##yalign_lin",
                () => Service.Config.YAlignTolerance, v => Service.Config.YAlignTolerance = v, 0.01f, 0f, 1f,
                "读档起点高度匹配/到位判定/重录校验——线性下一段是一定的，可容忍较大容差（默认 0.3，路不平放宽，平整调小）");
            DrawFloat("线性·插入段高度差 m", "##insegalign",
                () => Service.Config.InsertSegmentYAlign, v => Service.Config.InsertSegmentYAlign = v, 0.01f, 0f, 1f,
                "「插入新段」找就近起跳点时，|当前位置 Y − 起跳点 Y| ≤ 此值才视为候选");
        }
        else
        {
            DrawFloat("碎片·Y 对齐容差 m", "##yalign_frag",
                () => Service.Config.FragYAlignTolerance, v => Service.Config.FragYAlignTolerance = v, 0.01f, 0f, 1f,
                "碎片多起跳点近场易混淆，必须收紧以免多个点混淆距离计算（默认 0.2，与线性独立）");
            DrawFloat("碎片·水平衔接距离 m", "##fragxz",
                () => Service.Config.FragLinkDistXZ, v => Service.Config.FragLinkDistXZ = v, 0.5f, 0f, 30f,
                "本段落点 → 下一起跳点水平距离 ≤ 此值才自动衔接；超过即认为附近无可衔接段，自动终止等手动读档");
            DrawFloat("碎片·垂直衔接距离 m", "##fragy",
                () => Service.Config.FragLinkDistY, v => Service.Config.FragLinkDistY = v, 0.1f, 0f, 5f,
                "本段落点 → 下一起跳点 |ΔY| ≤ 此值才自动衔接——Y 高度绝不忽略（默认 1m）");
        }
    }

    /// <summary>切换游戏操作模式（MoveMode：0=标准 1=传统），写游戏配置立即生效。</summary>
    private static void SwitchMoveMode(uint want)
    {
        Service.GameConfig.UiControl.Set("MoveMode", want);
        Service.ChatGui.Print(want == 1 ? "已切换为传统操作模式" : "已切换为标准操作模式");
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
