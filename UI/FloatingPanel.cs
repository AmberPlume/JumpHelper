using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using JumpHelper.Models;
using JumpHelper.Services;
using JumpHelper.Utils;

namespace JumpHelper.UI;

/// <summary>
/// 悬浮窗（主操作面）：只放最重要的按钮，小窗可拖拽。
///   - 开始记录 / 暂停记录 / 继续记录（三态合一的大按钮）+ 设置（右侧短按钮）
///   - 保存路线（存到当前路线）/ 路线列表（弹窗：列表/新建/加载/删除/导出）
///   - 起点 ▾ / 终点 ▾（段选择下拉）
///   - 执行 / 终止（同一按钮，回放中自动切换）
///   - 快速读档（自动找最近同 Y 落点 → 跳到最远进度）/ 快速删除（删最后一段）
/// 世界标记的起终点图标由本窗的段选择驱动（RouteOverlay.StartSegment/EndSegment）。
/// </summary>
public sealed class FloatingPanel : Window
{
    private readonly RouteStore _routeStore;
    private readonly RecorderService _recorder;
    private readonly ReplayEngine _replay;
    private readonly MovementController _movement;
    private readonly JumpExecutor _jump;
    private readonly RouteOverlay _overlay;
    private readonly MainWindow _mainWindow;

    /// <summary>起点段选择：-2 = 就近（自动取最近的段落起点，默认）；-1 = 无选择；≥0 = 具体段索引——驱动世界标记「起点」图标。</summary>
    public int StartSegSel { get; private set; } = -2;

    /// <summary>终点段选择：-2 = 最远（最后一段，默认）；-1 = 无选择；≥0 = 具体段索引——驱动世界标记「终点」图标。</summary>
    public int EndSegSel { get; private set; } = -2;

    private RouteFile? _lastRouteRef;      // 路线切换检测（加载/新建/切换后重置起点/终点为默认就近/最远）
    private float _calibDist = 0.4f;       // 标定跳助跑距离（悬浮窗 << < > >> 步进 + 直接输入，米）

    // 路线列表 / 新建 / 段落编辑 窗口状态
    private bool _routeListOpen;
    private bool _newRouteOpen;
    private bool _segEditOpen;
    private string _newRouteName = "";
    private string[] _routeNames = Array.Empty<string>();
    private string[] _routeMapLabels = Array.Empty<string>(); // 与 _routeNames 对应的地图名缓存（每秒刷新一次）
    private int _selectedRoute;
    private long _lastRouteRefresh;
    private string _pendingOverwriteName = "";

    /// <summary>悬浮窗初始尺寸（px）：插件启动后第一帧强制一次（覆盖窗口系统可能保留的旧尺寸），
    /// 之后零每帧调用，由用户拖拽/缩放自由调整。宽度 200 高 230 接近早期 AlwaysAutoResize 内容自适应的大小。</summary>
    private const float PanelWidth = 200f;
    private const float PanelHeight = 230f;

    /// <summary>地图名缓存（TerritoryId → 名称）：路线列表动态查表时避免重复查询。</summary>
    private static readonly Dictionary<uint, string> TerritoryNameCache = new();

    /// <summary>首帧尺寸是否已应用。</summary>
    private bool _sizeApplied;

    public FloatingPanel(RouteStore routeStore, RecorderService recorder, ReplayEngine replay,
                         MovementController movement, JumpExecutor jump, RouteOverlay overlay,
                         MainWindow mainWindow)
        // NoTitleBar 而非 NoDecoration：NoDecoration 隐含 NoResize（无法缩放），
        // NoTitleBar 保留 resize 能力（右下角拖拽柄），窗口移动由 Draw 内手动拖动实现（无标题栏）。
        : base("跳跳乐助手·悬浮", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoSavedSettings)
    {
        _routeStore = routeStore;
        _recorder = recorder;
        _replay = replay;
        _movement = movement;
        _jump = jump;
        _overlay = overlay;
        _mainWindow = mainWindow;
        IsOpen = Service.Config.ShowFloatingPanel;
        RespectCloseHotkey = false;
    }

    public override void Draw()
    {
        // 与配置同步：主窗口调试面板勾选「显示悬浮窗」即时开/关
        if (IsOpen != Service.Config.ShowFloatingPanel)
            IsOpen = Service.Config.ShowFloatingPanel;

        // 首帧强制一次初始尺寸（Always 覆盖旧尺寸；显式高度防高度塌陷），之后交还用户拖拽/缩放
        if (!_sizeApplied)
        {
            ImGui.SetNextWindowSize(new Vector2(PanelWidth, PanelHeight), ImGuiCond.Always);
            _sizeApplied = true;
        }

        // 手动拖动移动（NoTitleBar 无标题栏，ImGui 不提供拖动）：窗口悬停且未悬停任何控件时按住左键拖动
        if (ImGui.IsWindowHovered() && !ImGui.IsAnyItemHovered() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            ImGui.SetWindowPos(ImGui.GetWindowPos() + ImGui.GetIO().MouseDelta);
        }

        // 同步世界标记起终点图标
        _overlay.StartSegment = StartSegSel;
        _overlay.EndSegment = EndSegSel;

        var route = _recorder.CurrentRoute;
        var hasRoute = route != null;
        var segCount = route?.Segments.Count ?? 0;
        // 路线切换检测：加载/新建/切换路线后起点/终点重置为默认（就近 -2 / 最远 -2）。
        // （根因：无路线时下方会置 -1"未选"，加载新路线后旧值残留不恢复——用户反馈"加载后默认全部未选"）
        if (!ReferenceEquals(_lastRouteRef, route))
        {
            _lastRouteRef = route;
            StartSegSel = route is { Segments.Count: > 0 } ? -2 : -1;
            EndSegSel = route is { Segments.Count: > 0 } ? -2 : -1;
        }
        // 段选择合法性收敛（-2 = 就近/最远哨兵值，-1 = 无选择，≥0 = 具体段）
        if (segCount > 0)
        {
            if (StartSegSel >= segCount) StartSegSel = -2;
            if (EndSegSel >= segCount) EndSegSel = -2;
            if (StartSegSel >= 0 && EndSegSel >= 0 && EndSegSel < StartSegSel) EndSegSel = StartSegSel;
        }
        else
        {
            StartSegSel = -1;
            EndSegSel = -1;
        }

        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var w = ImGui.GetContentRegionAvail().X; // 固定宽度窗口下稳定

        // 行1：开始记录（长，占剩余）+ 设置（短，右侧）
        var setW = ImGui.CalcTextSize("设置").X + ImGui.GetStyle().FramePadding.X * 2;
        DrawRecordButton(w - setW - spacing);
        ImGui.SameLine();
        if (ImGui.Button("设置", new Vector2(setW, 30)))
            _mainWindow.Toggle();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("打开设置窗口（主面板/容差参数/移动状态）");
        ImGui.Spacing();

        // 行2：保存路线 + 路线列表（等宽填满；路线列表始终可点——弹窗含新建/导入，无路线也能用）
        var halfW = (w - spacing) * 0.5f;
        if (!hasRoute) ImGui.BeginDisabled();
        if (ImGui.Button("保存路线", new Vector2(halfW, 0)))
            _recorder.SaveCurrent();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("保存当前路线到文件");
        if (!hasRoute) ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("路线列表", new Vector2(halfW, 0)))
            _routeListOpen = true;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("管理路线：新建/加载/删除/导出/改目录，双击直接加载");
        ImGui.Spacing();

        // 行3：起点/终点下拉（无 label 并排——悬浮窗窄，preview 文案带"起点/终点"）
        // 起点：就近（-2，默认）= 自动取最近的段落起点；终点：最远（-2，默认）= 最后一段。
        if (segCount > 0)
        {
            ImGui.SetNextItemWidth(halfW);
            var startLabel = StartSegSel == -2 ? "起点·就近" : StartSegSel >= 0 ? $"起点·段{StartSegSel + 1}" : "起点·未选";
            if (ImGui.BeginCombo("##start", startLabel))
            {
                if (ImGui.Selectable("就近（最近段起点）", StartSegSel == -2))
                    StartSegSel = -2;
                for (int i = 0; i < segCount; i++)
                {
                    if (ImGui.Selectable($"段 {i + 1}", StartSegSel == i))
                    {
                        StartSegSel = i;
                        if (EndSegSel >= 0 && EndSegSel < i) EndSegSel = i; // 终点自动跟随
                    }
                }
                ImGui.EndCombo();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("回放起点：就近=自动取最近的段落起点（默认）");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(halfW);
            var endLabel = EndSegSel == -2 ? "终点·最远" : EndSegSel >= 0 ? $"终点·段{EndSegSel + 1}" : "终点·未选";
            if (ImGui.BeginCombo("##end", endLabel))
            {
                if (ImGui.Selectable("最远（最后一段）", EndSegSel == -2))
                    EndSegSel = -2;
                for (int i = 0; i < segCount; i++)
                {
                    if (StartSegSel >= 0 && i < StartSegSel)
                    {
                        ImGui.BeginDisabled();
                        ImGui.Selectable($"段 {i + 1}", false);
                        ImGui.EndDisabled();
                        continue;
                    }
                    if (ImGui.Selectable($"段 {i + 1}", EndSegSel == i))
                        EndSegSel = i;
                }
                ImGui.EndCombo();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("回放终点：最远=最后一段（默认）");
            ImGui.Spacing();
        }

        // 行4：执行 / 继续 / 终止 合一（占满整行）：
        //   空闲显示「执行」（起点→终点回放）；常速冲突暂停显示「继续」（处理状态后恢复）；回放中显示「终止」
        var pausedForStatus = _replay.State == ReplayState.PausedForStatus;
        var replaying = _replay.State != ReplayState.Idle && _replay.State != ReplayState.Failed;
        var canRun = hasRoute && segCount > 0 && StartSegSel != -1 && EndSegSel != -1;
        if (!replaying && !canRun && !pausedForStatus) ImGui.BeginDisabled();
        var runLabel = pausedForStatus ? "继 续" : replaying ? "终 止" : "执 行";
        if (ImGui.Button(runLabel, new Vector2(w, 34)))
        {
            if (pausedForStatus)
                _replay.Resume();
            else if (replaying)
                StopAll();
            else
                RunSelection();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(pausedForStatus
                ? "处理移速状态后继续回放（未处理则带状态硬跳，自担跳过头风险）"
                : replaying ? "终止当前回放" : "从起点段回放到终点段");
        if (!replaying && !canRun && !pausedForStatus) ImGui.EndDisabled();
        ImGui.Spacing();

        // 行5：快速重录 + 快速删除（等宽填满）
        if (ImGui.Button("快速重录", new Vector2(halfW, 0)))
            QuickRerecord();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("重录就近的段落（自动匹配同平台最近的起跳点）");
        ImGui.SameLine();
        if (ImGui.Button("快速删除", new Vector2(halfW, 0)))
        {
            if (!_recorder.UndoLastSegment())
                Service.ChatGui.PrintError("无段可删除");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("删除最后一段（误跳/录歪时）");

        // 行6：段落编辑（整行，自适应宽度）
        if (ImGui.Button("段落编辑", new Vector2(w, 0)))
            _segEditOpen = true;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("管理段落：重录/截断/删除/扩展标记");
        ImGui.Spacing();

        // 行7：标定跳跃（调试：<< >> 步进 0.1，< > 步进 0.01，中间距离可直输，右侧跳跃按钮）
        var calTinyW = ImGui.CalcTextSize(">>").X + ImGui.GetStyle().FramePadding.X * 2 + 2;
        var calSmallW = ImGui.CalcTextSize("<").X + ImGui.GetStyle().FramePadding.X * 2 + 2;
        var calJumpW = ImGui.CalcTextSize("跳 跃").X + ImGui.GetStyle().FramePadding.X * 2 + 8;
        var calInputW = w - calTinyW * 2 - calSmallW * 2 - calJumpW - spacing * 4;
        if (ImGui.Button("<<##cal", new Vector2(calTinyW, 0)))
            _calibDist = MathF.Max(0f, _calibDist - 0.1f);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("助跑距离 -0.1m");
        ImGui.SameLine();
        if (ImGui.Button("<##cal", new Vector2(calSmallW, 0)))
            _calibDist = MathF.Max(0f, _calibDist - 0.01f);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("助跑距离 -0.01m");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(calInputW);
        var calDist = _calibDist;
        // step=0 隐藏 InputFloat 自带的 -/+ 按钮（步进已由 << < > >> 提供）
        if (ImGui.InputFloat("##caldist", ref calDist, 0f, 0f, "%.3f"))
            _calibDist = Math.Clamp(calDist, 0f, 20f);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("助跑距离（米）——可点击直接输入");
        ImGui.SameLine();
        if (ImGui.Button(">##cal", new Vector2(calSmallW, 0)))
            _calibDist = MathF.Min(20f, _calibDist + 0.01f);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("助跑距离 +0.01m");
        ImGui.SameLine();
        if (ImGui.Button(">>##cal", new Vector2(calTinyW, 0)))
            _calibDist = MathF.Min(20f, _calibDist + 0.1f);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("助跑距离 +0.1m");
        ImGui.SameLine();
        if (ImGui.Button("跳 跃", new Vector2(calJumpW, 0)))
            _replay.StartCalibration(_calibDist);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("按照指定的助跑距离跳跃一次，常速时，预计0.35m将达到最大跳跃距离");

        // 状态提示：等待玩家手动走到下一段起跳点（段间长距离半自动）/ 常速冲突暂停
        if (_replay.State == ReplayState.AwaitPlayer)
        {
            ImGui.TextColored(new Vector4(1f, 0.85f, 0.3f, 1f),
                $"等待：请走到段 {_replay.CurrentSegment + 2} 起跳点");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("段间距离较远——请手动走到下一段起跳点（同高度），到位后自动继续跳");
        }
        else if (pausedForStatus)
        {
            ImGui.TextColored(new Vector4(1f, 0.6f, 0.3f, 1f),
                $"段 {_replay.CurrentSegment + 1} 为常速但当前带移速状态——点「继续」或「终止」");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("录制常速、当前带冲刺/慢跑/速行会跳过头——清除移速状态后点「继续」（进战斗/切职业可清除；大缓冲平台可带状态硬跳）");
        }

        // 段落编辑窗口（普通非模态）：段列表 + 重录/截断/删除
        if (_segEditOpen)
        {
            ImGui.SetNextWindowSize(new Vector2(380, 0), ImGuiCond.FirstUseEver);
            if (ImGui.Begin("段落编辑", ref _segEditOpen))
            {
                DrawSegmentEditor();
                ImGui.End();
            }
        }

        // 路线列表窗口（普通非模态窗口——不拦截其他输入，可与游戏/悬浮窗/主窗口同时交互）
        if (_routeListOpen)
        {
            ImGui.SetNextWindowSize(new Vector2(380, 0), ImGuiCond.FirstUseEver);
            if (ImGui.Begin("路线列表", ref _routeListOpen))
            {
                DrawRouteList();
                ImGui.End();
            }
        }

        // 新建路线小窗口（路线列表点「新建」打开）
        if (_newRouteOpen)
        {
            ImGui.SetNextWindowSize(new Vector2(320, 0), ImGuiCond.FirstUseEver);
            if (ImGui.Begin("新建路线", ref _newRouteOpen))
            {
                DrawNewRoute();
                ImGui.End();
            }
        }
    }

    /// <summary>执行：起点段 → 终点段 回放（纯跳回，不丢弃段）。
    /// 起点「就近」= 自动取最近的段落起点（XZ 最近且同平台）；终点「最远」= 最后一段。</summary>
    private void RunSelection()
    {
        var route = _recorder.CurrentRoute;
        if (route == null || route.Segments.Count == 0)
        {
            Service.ChatGui.PrintError("无路线或尚无段");
            return;
        }
        if (StartSegSel == -1 || EndSegSel == -1)
        {
            Service.ChatGui.PrintError("请选择起点和终点");
            return;
        }

        // 解析起点：就近 = 自动找最近的段落起点
        var start = StartSegSel;
        if (start == -2)
        {
            start = FindNearestTakeoffSegment(route);
            if (start < 0)
            {
                Service.ChatGui.PrintError("就近失败：当前位置附近（同平台）无段落起点，请先走到路线起点附近或手动选段");
                return;
            }
        }
        // 解析终点：最远 = 最后一段
        var end = EndSegSel;
        if (end == -2)
            end = route.Segments.Count - 1;

        if (start > end)
        {
            Service.ChatGui.PrintError($"就近起点（段 {start + 1}）已在终点（段 {end + 1}）之后——请调整终点或手动选起点段");
            return;
        }
        if (end >= route.Segments.Count)
        {
            Service.ChatGui.PrintError("终点段超出范围");
            return;
        }
        _replay.StartRouteSegments(route, start, end);
    }

    /// <summary>就近起点：遍历段起跳点，找 XZ 最近且 |Y差| ≤ YAlignTolerance 的段（同平台）；无匹配返回 -1。</summary>
    private int FindNearestTakeoffSegment(RouteFile route)
    {
        var player = Service.ObjectTable.LocalPlayer;
        if (player == null)
            return -1;
        int best = -1;
        var bestD = float.MaxValue;
        for (int i = 0; i < route.Segments.Count; i++)
        {
            var t = route.Segments[i].Takeoff;
            if (MathF.Abs(t.Y - player.Position.Y) > Service.Config.YAlignTolerance)
                continue;
            var dx = t.X - player.Position.X;
            var dz = t.Z - player.Position.Z;
            var d = dx * dx + dz * dz;
            if (d < bestD)
            {
                bestD = d;
                best = i;
            }
        }
        return best;
    }

    /// <summary>快速重录：自动匹配就近段落（同平台最近起跳点）并进入重录（成功时 RecorderService 已提示「段 X 重录开始」）。</summary>
    private void QuickRerecord()
    {
        var route = _recorder.CurrentRoute;
        if (route == null || route.Segments.Count == 0)
        {
            Service.ChatGui.PrintError("无路线或尚无段");
            return;
        }
        var idx = FindNearestTakeoffSegment(route);
        if (idx < 0)
        {
            Service.ChatGui.PrintError("就近无段落起点（同平台）——请靠近要重录的段");
            return;
        }
        _recorder.RerecordFrom(idx);
    }

    /// <summary>段落编辑窗口：段列表（序号 + 扩展标记 + 每段操作：重录 / 截断 / 删除）。
    /// 重录 = 从该段起跳点（或行走起点，扩展段）重新记录；截断 = 删除该段及之后；删除 = 只删该段。
    /// 扩展 = 段间行走录进时间线完整复现（需设置页「扩展时间线」开关开启），切换后重录生效。</summary>
    private void DrawSegmentEditor()
    {
        var route = _recorder.CurrentRoute;
        if (route == null || route.Segments.Count == 0)
        {
            ImGui.TextDisabled("无路线或尚无段（先「新建」+「开始记录」跳几跳）");
            return;
        }

        ImGui.Text($"当前路线 [{route.Name}]：{route.Segments.Count} 段——段落是玩家参考的基本单位（序号对应世界标记）");
        var extEnabled = Service.Config.ExtendedTimeline;
        if (extEnabled)
            ImGui.TextDisabled("扩展时间线已开启：勾选某段「扩展」并重录 = 段间行走完整复现（自装修跳跳乐高精度场景）");
        ImGui.Spacing();

        // 列数随扩展开关变化：扩展关闭时隐藏「扩展」列
        var colCount = extEnabled ? 5 : 4;
        if (ImGui.BeginTable("##segedit", colCount, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("段");
            if (extEnabled)
                ImGui.TableSetupColumn("扩展");
            ImGui.TableSetupColumn("重录");
            ImGui.TableSetupColumn("截断");
            ImGui.TableSetupColumn("删除");
            ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
            foreach (var h in extEnabled
                         ? new[] { "段", "扩展", "重录", "截断", "删除" }
                         : new[] { "段", "重录", "截断", "删除" })
            {
                ImGui.TableNextColumn();
                CenterText(h);
            }

            for (int i = 0; i < route.Segments.Count; i++)
            {
                var seg = route.Segments[i];
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                CenterText($"{i + 1}");

                // 扩展复选框（仅扩展时间线开启时显示；切换后需重录生效）
                if (extEnabled)
                {
                    ImGui.TableNextColumn();
                    var ext = seg.Extended == true;
                    CenterWidget(ImGui.GetFrameHeight());
                    if (ImGui.Checkbox($"##x{i}", ref ext))
                    {
                        seg.Extended = ext; // true/false 强制标记；重录时按此录制
                        Service.ChatGui.Print($"段 {i + 1} 扩展标记已设为 {(ext ? "开启" : "关闭")}——需「重录」该段生效");
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("开启：该段重录时把段间行走也录进时间线（完整复现）；需重录生效");
                }

                ImGui.TableNextColumn();
                CenterWidget(ImGui.GetFrameHeight() * 2.6f);
                if (ImGui.Button($"重录##r{i}"))
                {
                    if (_recorder.RerecordFrom(i))
                        _segEditOpen = false; // 重录开始，关闭编辑窗（玩家直接开跳）
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(seg.Extended == true
                        ? "从该段行走起点重新走+跳（扩展时间线）"
                        : "从该段起跳点重新跳（需与该段起跳点同一 Y 轴）");

                ImGui.TableNextColumn();
                CenterWidget(ImGui.GetFrameHeight() * 2.6f);
                if (ImGui.Button($"截断##c{i}"))
                {
                    if (_recorder.CutFrom(i))
                        break; // 段数变化，结束本帧（下一帧重绘）
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("删除该段及之后所有段（后半段重录用）");

                ImGui.TableNextColumn();
                CenterWidget(ImGui.GetFrameHeight() * 2.6f);
                if (ImGui.Button($"删除##d{i}"))
                {
                    if (_recorder.DeleteSegmentAt(i))
                        break; // 段数变化，结束本帧（下一帧重绘）——防同帧循环段索引越界
                }
            }
            ImGui.EndTable();
        }
        ImGui.Spacing();
        ImGui.TextDisabled("重录=替换该段；截断=清掉该段及之后；删除=只删该段；扩展=段间行走完整复现（重录生效）。");
    }

    /// <summary>段落编辑窗内表格单元格居中文本。</summary>
    private static void CenterText(string text)
    {
        var avail = ImGui.GetContentRegionAvail().X;
        var w = ImGui.CalcTextSize(text).X;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0f, (avail - w) * 0.5f));
        ImGui.Text(text);
    }

    /// <summary>段落编辑窗内表格单元格居中指定宽度的控件。</summary>
    private static void CenterWidget(float width)
    {
        var avail = ImGui.GetContentRegionAvail().X;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0f, (avail - width) * 0.5f));
    }

    /// <summary>路线列表窗口：操作行（新建/加载/删除/导出）+ 路线列表（名称 + 录制地图名）。</summary>
    private void DrawRouteList()
    {
        // 路线列表（每秒刷新）。注意：地图名计算必须在这里一次完成——DescribeRoute 内部要
        // Load 完整路线 JSON（读文件+解析），若放 Draw 循环里每帧每行执行，路线多时掉帧
        // （实测：打开路线列表窗帧数骤降）。
        if (Environment.TickCount64 - _lastRouteRefresh > 1000)
        {
            _lastRouteRefresh = Environment.TickCount64;
            _routeNames = _routeStore.ListRouteNames().ToArray();
            _routeMapLabels = new string[_routeNames.Length];
            for (int i = 0; i < _routeNames.Length; i++)
                _routeMapLabels[i] = DescribeRoute(_routeNames[i]);
            if (_selectedRoute >= _routeNames.Length)
                _selectedRoute = Math.Max(0, _routeNames.Length - 1);
        }

        var w = ImGui.GetContentRegionAvail().X;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var btnW = (w - spacing * 4) * 0.2f;

        // 操作行：新建 / 加载 / 删除（未按 Ctrl 灰暗不可点，按下亮起）/ 导出 / 目录
        if (ImGui.Button("新建", new Vector2(btnW, 0)))
            _newRouteOpen = true;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("创建新路线或从剪贴板/本地文件导入");

        ImGui.SameLine();
        if (_routeNames.Length == 0) ImGui.BeginDisabled();
        if (ImGui.Button("加载", new Vector2(btnW, 0)))
        {
            if (_recorder.LoadRouteForRecord(_routeNames[_selectedRoute]))
                _routeListOpen = false;
            else
                Service.ChatGui.PrintError("加载失败（查看日志）");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("加载选中路线（或双击列表项直接加载）");
        ImGui.SameLine();
        var ctrlDown = ImGui.IsKeyDown(ImGuiKey.LeftCtrl) || ImGui.IsKeyDown(ImGuiKey.RightCtrl);
        if (!ctrlDown) ImGui.BeginDisabled(); // 未按 Ctrl：灰暗不可点
        if (ImGui.Button("删除", new Vector2(btnW, 0)))
        {
            _routeStore.Delete(_routeNames[_selectedRoute]);
            _lastRouteRefresh = 0; // 立即刷新列表
        }
        if (!ctrlDown) ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("删除选中路线——需按住 Ctrl 才可点（防误删）");
        ImGui.SameLine();
        if (ImGui.Button("导出", new Vector2(btnW, 0)))
            ExportSelectedRoute();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("导出选中路线为 JSON 文件");
        if (_routeNames.Length == 0) ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("目录", new Vector2(btnW, 0)))
            ChangeRouteDirectory();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("修改路线文件目录（选择文件夹）");

        ImGui.Separator();
        ImGui.TextDisabled("双击路线名直接加载");

        if (_routeNames.Length == 0)
        {
            ImGui.TextDisabled("（暂无路线）");
            return;
        }

        // 路线列表：名称 + 录制地图名（缓存数组，每秒刷新时一次性计算——避免每帧 Load JSON）
        for (int i = 0; i < _routeNames.Length; i++)
        {
            var mapLabel = i < _routeMapLabels.Length ? _routeMapLabels[i] : "未知";
            if (ImGui.Selectable($"{_routeNames[i]}  [{mapLabel}]", i == _selectedRoute))
                _selectedRoute = i;
            // 双击 = 直接加载（切换时自动保存当前路线）
            if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            {
                if (_recorder.LoadRouteForRecord(_routeNames[i]))
                    _routeListOpen = false;
                else
                    Service.ChatGui.PrintError("加载失败（查看日志）");
            }
        }
    }

    /// <summary>路线描述：录制地图名 + 移速状态汇总（常速/冲刺/慢跑速行组合）。
    /// 旧路线无 TerritoryName 字段 → 按 TerritoryId 动态查表（缓存）。</summary>
    private string DescribeRoute(string name)
    {
        try
        {
            var route = _routeStore.Load(name);
            if (route == null)
                return "未知";
            var map = !string.IsNullOrWhiteSpace(route.TerritoryName)
                ? route.TerritoryName
                : ResolveMapName(route.TerritoryId);
            return $"{map}·{route.MoveStateSummary()}"; // 状态：常速 / 常速+冲刺+慢跑/速行（起跳速度一致性提示）
        }
        catch
        {
            return "未知";
        }
    }

    /// <summary>按区域 ID 查地图名（带缓存）；查询失败返回 "地图 {id}" 回退。旧路线无 TerritoryName 时用。</summary>
    private static string ResolveMapName(uint territoryId)
    {
        if (TerritoryNameCache.TryGetValue(territoryId, out var cached))
            return cached;
        try
        {
            var row = Service.DataManager.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>()?.GetRow(territoryId);
            var name = row?.PlaceName.ValueNullable?.Name.ToString();
            if (!string.IsNullOrWhiteSpace(name))
            {
                TerritoryNameCache[territoryId] = name;
                return name;
            }
        }
        catch (Exception ex)
        {
            Service.Log.Error(ex, $"地图名查询失败 Territory={territoryId}");
        }
        return $"地图 {territoryId}";
    }

    /// <summary>新建路线小窗口：名称创建 / 剪贴板导入 / 选择本地文件导入。</summary>
    private void DrawNewRoute()
    {
        ImGui.Text("名称创建：");
        ImGui.SetNextItemWidth(180f);
        ImGui.InputText("##name", ref _newRouteName, 64);
        ImGui.SameLine();
        if (ImGui.Button("创建"))
        {
            var name = string.IsNullOrWhiteSpace(_newRouteName) ? $"路线{DateTime.Now:HHmmss}" : _newRouteName.Trim();
            if (_routeStore.Exists(name))
            {
                _pendingOverwriteName = name;
            }
            else if (_recorder.StartRecording(name))
            {
                _newRouteName = "";
                _newRouteOpen = false;
                _routeListOpen = false;
            }
        }
        if (_pendingOverwriteName.Length > 0)
        {
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), $"「{_pendingOverwriteName}」已存在——覆盖将删除旧文件");
            if (ImGui.Button("确认覆盖"))
            {
                _routeStore.Delete(_pendingOverwriteName);
                _recorder.StartRecording(_pendingOverwriteName);
                _pendingOverwriteName = "";
                _newRouteOpen = false;
                _routeListOpen = false;
            }
            ImGui.SameLine();
            if (ImGui.Button("取消"))
                _pendingOverwriteName = "";
        }

        ImGui.Separator();
        ImGui.Text("导入已有路线：");
        if (ImGui.Button("从剪贴板导入", new Vector2(150, 0)))
        {
            var text = ImGui.GetClipboardText();
            if (string.IsNullOrWhiteSpace(text))
            {
                Service.ChatGui.PrintError("剪贴板为空");
            }
            else if (_routeStore.ImportFromText(text) != null)
            {
                _newRouteOpen = false;
                _lastRouteRefresh = 0;
            }
            else
            {
                Service.ChatGui.PrintError("导入失败（查看日志；剪贴板需包含路线 JSON 文本）");
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("选择本地文件", new Vector2(150, 0)))
        {
            var path = Dialogs.PickFile("选择路线文件", "路线文件 (*.json)|*.json");
            if (path != null && _routeStore.ImportFile(path) != null)
            {
                _newRouteOpen = false;
                _lastRouteRefresh = 0;
            }
            else if (path != null)
            {
                Service.ChatGui.PrintError("导入失败（查看日志）");
            }
        }
        ImGui.TextDisabled("支持导入：别人分享的路线 JSON 文本 / 本地路线文件。");
    }

    /// <summary>导出选中路线：保存对话框选择位置，写入路线 JSON。</summary>
    private void ExportSelectedRoute()
    {
        if (_routeNames.Length == 0)
            return;
        var name = _routeNames[_selectedRoute];
        var route = _routeStore.Load(name);
        var text = route != null ? _routeStore.ExportToText(route) : null;
        if (text == null)
        {
            Service.ChatGui.PrintError("导出失败（查看日志）");
            return;
        }

        var path = Dialogs.SaveFile($"保存路线 {name}", "路线文件 (*.json)|*.json", name + ".json");
        if (path == null)
            return;
        try
        {
            System.IO.File.WriteAllText(path, text);
            Service.ChatGui.Print($"路线「{name}」已导出到 {path}");
        }
        catch (Exception ex)
        {
            Service.Log.Error(ex, $"路线导出写入失败: {path}");
            Service.ChatGui.PrintError("导出写入失败（查看日志）");
        }
    }

    /// <summary>修改路线目录：弹文件夹选择框，改目录并刷新路线列表（RouteStore 保存配置）。</summary>
    private void ChangeRouteDirectory()
    {
        var dir = Dialogs.PickFolder("选择路线目录");
        if (dir == null)
            return; // 用户取消
        if (_routeStore.ChangeDirectory(dir))
        {
            _selectedRoute = 0;
            _lastRouteRefresh = 0;
            Service.ChatGui.Print($"路线目录已修改: {dir}");
        }
        else
        {
            Service.ChatGui.PrintError("目录修改失败（查看日志）");
        }
    }

    // ===== 文件对话框（WinForms 需 STA 线程——统一实现见 Utils/Dialogs.cs） =====

    /// <summary>开始记录/暂停/继续 三态按钮（宽度由调用方传入，占满第一行剩余空间）。</summary>
    private void DrawRecordButton(float width)
    {
        var hasRoute = _recorder.IsRecording;
        var recording = _recorder.IsRecordingActive;
        var paused = _recorder.IsPaused;

        string label;
        if (!recording && !paused)
            label = "开始记录";
        else if (recording && !paused)
            label = "暂停记录";
        else
            label = "继续记录";

        if (!hasRoute) ImGui.BeginDisabled();
        if (ImGui.Button(label, new Vector2(width, 30)))
        {
            if (recording || paused)
                _recorder.TogglePause();
            else
                _recorder.BeginRecording();
        }
        if (!hasRoute) ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(!recording && !paused
                ? "开始采集跳跃段（后续跳跃自动成为段落）"
                : paused ? "恢复采集" : "暂停采集（回程/离开跳跳乐时用）");
    }

    private void StopAll()
    {
        _replay.Stop();
        _movement.ReleaseAll();
        _jump.Stop();
    }
}
