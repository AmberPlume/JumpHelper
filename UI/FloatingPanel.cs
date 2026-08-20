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
///   - 起点 ▾ / 终点 ▾（段选择下拉，仅线性模式）
///   - 执行 / 终止（同一按钮，回放中自动切换；「执行」= 读档跳回）
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

    // 路线列表 / 新建 / 段落编辑 / 云端上传 窗口状态
    private bool _routeListOpen;
    private bool _newRouteOpen;
    private bool _segEditOpen;
    private bool _cloudUploadOpen;
    private string _cloudUploadName = "";   // 待上传的路线名（本地列表「上传」按钮置入）
    private string _cloudNickname = "";     // 上传人昵称
    private string _cloudDescription = "";  // 路线描述
    private string _cloudSearch = "";       // 云端列表搜索词（路线名/上传人）
    private string _cloudMapFilter = "";    // 云端列表地图筛选（空=全部地图）
    private readonly List<string> _cloudMapOptions = new(); // 地图筛选项（云端清单填充）
    private List<CloudRouteInfo> _cloudInfos = new(); // 云端清单缓存（异步后台拉取，UI 不阻塞）
    private List<CloudRouteInfo>? _lastCloudInfos;    // 上次引用的清单（检测更新重建地图选项）
    private volatile bool _cloudLoading;              // 后台拉取中标志（防并发重复请求）
    private volatile bool _cloudLoadError;            // 云端清单最近一次拉取失败（UI 显示重试提示）
    private long _cloudLastRefresh;                   // 云端清单上次触发刷新时刻
    private bool _cloudFeedbackOpen;        // 路线反馈弹窗
    private string _cloudFeedbackName = ""; // 被反馈的路线名
    private string _cloudFeedbackText = ""; // 反馈内容
    private int _cloudPage;                 // 云端列表当前页（每页 5 条）
    private string _newRouteName = "";
    private string[] _routeNames = Array.Empty<string>();
    private string[] _routeMapLabels = Array.Empty<string>(); // 与 _routeNames 对应的地图名缓存（分列表格第二列，每秒刷新一次）
    private string[] _routeModeLabels = Array.Empty<string>(); // 与 _routeNames 对应的段落模式缓存（分列表格第三列，每秒刷新一次）
    private string[] _routeStatusLabels = Array.Empty<string>(); // 与 _routeNames 对应的移速状态汇总缓存（分列表格第四列，每秒刷新一次）
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
        // 段选择合法性收敛（-2 = 就近/最远哨兵值，-1 = 无选择，≥0 = 具体段）。
        // 取消"未选"态：只要路线已有段，起点/终点一律落到默认（就近 / 最远）。
        // 关键：新建空路线后给同一条路线加首段（_lastRouteRef 相同，上方路线切换检测不触发），
        // 旧 -1"未选"会残留——这里兜底把 -1 收敛回默认值。
        if (segCount > 0)
        {
            if (StartSegSel == -1) StartSegSel = -2;
            if (EndSegSel == -1) EndSegSel = -2;
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

        // 行0：段落记录方式切换（线性 / 碎片）——仅「当前」按钮着淡色高亮（淡蓝=线性、淡橙=碎片）用以区分，
        // 未选中态与普通按钮一致（含悬停变亮）。两个按钮并排占满整行、高度与其他行统一。
        // 切换模式后强制「重新选择或新建路线」：卸载当前路线（自动保存），防止旧模式录的路线被新模式接着录。
        // ⚠️ Push/Pop 必须用「点击前」的模式快照——按钮回调 SwitchMode 会改 Config.SegmentMode，
        // 若点击后重新判断（如碎片→线性：点击前不 Push、点击后 if 为真却 Pop）→ 样式栈下溢 → cimgui 崩溃
        // （用户实测：先点碎片再切回线性必崩，AccessViolation in cimgui.dll，根因即此）。
        var modeIsLinear = Service.Config.SegmentMode == SegmentMode.Linear;
        var modeIsFragment = Service.Config.SegmentMode == SegmentMode.Fragment;
        var modeW = (w - spacing) * 0.5f;
        if (modeIsLinear)
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.62f, 0.72f, 0.87f, 1f)); // 淡蓝
        if (ImGui.Button("线性", new Vector2(modeW, 0)))
            SwitchMode(SegmentMode.Linear, "线性：按段序号顺序依次跳跃，落点不自动衔接");
        if (modeIsLinear)
            ImGui.PopStyleColor();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("线性：按段序号依次跳跃——连续不分段的常规地图（切换需重选/新建路线）");
        ImGui.SameLine();
        if (modeIsFragment)
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.96f, 0.76f, 0.48f, 1f)); // 淡橙
        if (ImGui.Button("碎片", new Vector2(modeW, 0)))
            SwitchMode(SegmentMode.Fragment, "碎片：数字仅作段名，落地后按距离/高度自动衔接，岔路需手动选择");
        if (modeIsFragment)
            ImGui.PopStyleColor();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("碎片：先去左边跳一小段、再去右边跳一小段的分散地图——按落点→起跳点距离/高度自动衔接，岔路暂停选择（切换需重选/新建路线）");
        ImGui.Spacing();

        // 行0.5：当前路线信息（名称 + 移速状态）+ 路线列表按钮（右侧）——模式切换下方、记录按钮上方。
        // 速度信息 = 路线的移速状态汇总（常速/冲刺/慢跑·速行/慢走 组合），与路线列表「速度」列一致。
        // 路线列表按钮始终可点（弹窗含新建/导入，无路线也能用）；设置按钮在下方与保存路线同排。
        var setW = ImGui.CalcTextSize("路线列表").X + ImGui.GetStyle().FramePadding.X * 2;
        var speedText = route != null ? route.MoveStateSummary() : "";
        var speedW = speedText.Length > 0 ? ImGui.CalcTextSize(speedText).X : 0f;
        var infoNameW = w - setW - speedW - spacing * 3;
        ImGui.TextUnformatted(route != null ? TruncateText(route.Name, infoNameW) : "未加载路线");
        if (route != null && speedText.Length > 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(speedText);
        }
        ImGui.SameLine();
        if (ImGui.Button("路线列表", new Vector2(setW, 0)))
            _routeListOpen = true;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("管理路线：新建/加载/删除/导出/改目录，双击直接加载");
        ImGui.Spacing();

        // 行1：开始记录（独占一行；按钮加高 1.5 倍便于点击）
        DrawRecordButton(w);
        ImGui.Spacing();

        // 行2：保存路线 + 设置（等宽填满；设置始终可点）
        var halfW = (w - spacing) * 0.5f;
        if (!hasRoute) ImGui.BeginDisabled();
        if (ImGui.Button("保存路线", new Vector2(halfW, 0)))
            _recorder.SaveCurrent();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("保存当前路线到文件");
        if (!hasRoute) ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("设置", new Vector2(halfW, 0)))
            _mainWindow.Toggle();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("打开设置窗口（基础设置/参数设置）");
        ImGui.Spacing();

        // 行3：起点/终点下拉（无 label 并排——悬浮窗窄，preview 文案带"起点/终点"）
        // 起点：就近（-2，默认）= 自动取最近的段落起点；终点：最远（-2，默认）= 最后一段。
        // 仅在【线性】模式显示——碎片模式无起点/终点概念（哪个起跳点近自动去哪个）。
        if (segCount > 0 && Service.Config.SegmentMode == SegmentMode.Linear)
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

        // 行4：路线回放 / 继续 / 终止（占满整行）。「路线回放」即读档跳回：对当前已加载路线从就近段跳到最远段。
        // 线性模式按起点/终点下拉；碎片模式无起点/终点，就近起跳点自动衔接。常速冲突暂停显示「继续」。
        var pausedForStatus = _replay.State == ReplayState.PausedForStatus;
        var pausedForBranch = _replay.State == ReplayState.PausedForBranch;
        var replaying = _replay.State != ReplayState.Idle && _replay.State != ReplayState.Failed;
        var isLinear = Service.Config.SegmentMode == SegmentMode.Linear;
        var canRun = isLinear
            ? hasRoute && segCount > 0 && StartSegSel != -1 && EndSegSel != -1
            : hasRoute && segCount > 0; // 碎片模式无需起点/终点下拉
        if (!replaying && !canRun && !pausedForStatus) ImGui.BeginDisabled();
        var runLabel = pausedForStatus ? "继 续" : replaying ? "终 止" : "路线回放";
        if (ImGui.Button(runLabel, new Vector2(w, 51)))
        {
            if (pausedForStatus)
                _replay.Resume();
            else if (replaying)
                StopAll();
            else if (isLinear)
                RunSelection();
            else
                LoadAndJumpBack();
        }
        if (ImGui.IsItemHovered())
        {
            if (pausedForStatus)
                ImGui.SetTooltip("处理移速状态后继续回放");
            else if (replaying)
                ImGui.SetTooltip("终止当前回放");
            else if (isLinear)
                ImGui.SetTooltip("路线回放（读档跳回：按起点/终点选择回放）");
            else
                ImGui.SetTooltip("碎片模式：从最近的起跳点开始路线回放（读档跳回；水平≤XZ 且 |ΔY|≤Y）");
        }
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

        // 行6：插入新段（仅线性模式；碎片段落由距离衔接，插入无意义）+ 段落编辑（同一行并排）
        var isLinearNow = Service.Config.SegmentMode == SegmentMode.Linear;
        if (isLinearNow)
        {
            if (ImGui.Button("插入新段", new Vector2(halfW, 0)))
            {
                if (_recorder.InsertNewSegment())
                    _segEditOpen = false; // 插入开始，关闭段落编辑窗（玩家直接跳一跳补录）
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("以就近（同高度 |Y差|≤插入容差、XZ最近）落点段为基准，在其后插入新段——下一跳记录为该段后一位（后续段顺延），解决\"删段后无法补录原段序号\"问题");
            ImGui.SameLine();
        }
        if (ImGui.Button("段落编辑", new Vector2(isLinearNow ? halfW : w, 0)))
            _segEditOpen = true;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("管理段落：重录/删除/扩展标记" + (isLinearNow ? "（线性模式另有截断/插入）" : "（碎片模式仅重录/删除）"));
        ImGui.Spacing();

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
        else if (pausedForBranch)
        {
            ImGui.TextColored(new Vector4(0.9f, 0.45f, 0.1f, 1f),
                $"碎片岔路：段 {_replay.CurrentSegment + 1} 附近 {_replay.BranchCandidates.Count} 个候选——请在弹窗选择下一个段落");
        }

        // 碎片岔路选择弹窗（普通非模态）：列出候选段起跳点（段号+坐标），玩家点选即继续
        if (pausedForBranch && _replay.BranchCandidates.Count > 0)
        {
            CenterNextPopup(new Vector2(340, 0), ImGuiCond.Always);
            if (ImGui.Begin("碎片岔路——选择下段", ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize))
            {
                ImGui.TextColored(new Vector4(0.9f, 0.45f, 0.1f, 1f),
                    $"当前在段 {_replay.CurrentSegment + 1} 落点，附近有 {_replay.BranchCandidates.Count} 个可衔接起跳点：");
                ImGui.Spacing();
                foreach (var c in _replay.BranchCandidates)
                {
                    var t = _replay.CurrentRoute.Segments[c].Takeoff;
                    var row = $"段 {c + 1}  ({t.X:F0}, {t.Y:F0}, {t.Z:F0})";
                    if (ImGui.Button(row, new Vector2(320, 26)))
                        _replay.ChooseBranch(c);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("跳到该段的起跳点并执行——选中后按此段继续");
                }
                ImGui.Spacing();
                if (ImGui.Button("取消（终止）", new Vector2(320, 26)))
                    StopAll();
                ImGui.End();
            }
        }

        // 段落编辑窗口（普通非模态）：段列表 + 重录/截断/删除
        if (_segEditOpen)
        {
            CenterNextPopup(new Vector2(380, 0), ImGuiCond.FirstUseEver);
            if (ImGui.Begin("段落编辑", ref _segEditOpen))
            {
                DrawSegmentEditor();
                ImGui.End();
            }
        }

        // 路线列表窗口（普通非模态窗口——不拦截其他输入，可与游戏/悬浮窗/主窗口同时交互）。
        // 分「本地 / 云端」两个分区：本地=现有路线管理；云端=玩家分享路线。
        if (_routeListOpen)
        {
            CenterNextPopup(new Vector2(1380, 0), ImGuiCond.FirstUseEver);
            if (ImGui.Begin("路线列表", ref _routeListOpen))
            {
                // Tab 按钮加大（FramePadding 撑大，方便点击辨识）；整段 push/pop 保证配对
                ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(16f, 7f));
                if (ImGui.BeginTabBar("##route_tabs"))
                {
                    if (ImGui.BeginTabItem("本地"))
                    {
                        DrawRouteList();
                        ImGui.EndTabItem();
                    }
                    if (ImGui.BeginTabItem("云端"))
                    {
                        DrawCloudRouteList();
                        ImGui.EndTabItem();
                    }
                    ImGui.EndTabBar();
                }
                ImGui.PopStyleVar();
                ImGui.End();
            }
        }

        // 新建路线小窗口（路线列表点「新建」打开）
        if (_newRouteOpen)
        {
            CenterNextPopup(new Vector2(320, 0), ImGuiCond.FirstUseEver);
            if (ImGui.Begin("新建路线", ref _newRouteOpen))
            {
                DrawNewRoute();
                ImGui.End();
            }
        }

        // 云端上传小窗口（本地路线「上传」按钮打开：填昵称 + 描述后确认上传）
        if (_cloudUploadOpen)
        {
            CenterNextPopup(new Vector2(360, 0), ImGuiCond.FirstUseEver);
            if (ImGui.Begin($"上传路线「{_cloudUploadName}」", ref _cloudUploadOpen))
            {
                DrawCloudUpload();
                ImGui.End();
            }
        }

        // 云端路线反馈弹窗（云端列表「反馈」打开：填内容提交 → 记入 report.json，维护者查阅）
        if (_cloudFeedbackOpen)
        {
            CenterNextPopup(new Vector2(360, 0), ImGuiCond.FirstUseEver);
            if (ImGui.Begin($"反馈「{_cloudFeedbackName}」", ref _cloudFeedbackOpen))
            {
                ImGui.Text("对该路线的反馈/问题");
                ImGui.SetNextItemWidth(300f);
                ImGui.InputTextMultiline("##fb_text", ref _cloudFeedbackText, 500, new Vector2(300, 80));
                ImGui.Spacing();
                var w2 = ImGui.GetContentRegionAvail().X;
                var halfW2 = (w2 - ImGui.GetStyle().ItemSpacing.X) * 0.5f;
                var canGo = _cloudFeedbackText.Trim().Length > 0;
                if (!canGo) ImGui.BeginDisabled();
                if (ImGui.Button("提交", new Vector2(halfW2, 0)))
                {
                    if (CloudRouteService.SendRouteFeedback(_cloudFeedbackName, _cloudFeedbackText.Trim()))
                    {
                        Service.ChatGui.Print($"已反馈「{_cloudFeedbackName}」");
                        _cloudFeedbackOpen = false;
                    }
                    else
                        Service.ChatGui.PrintError("提交失败（查看日志）");
                }
                if (!canGo) ImGui.EndDisabled();
                ImGui.SameLine();
                if (ImGui.Button("取消", new Vector2(halfW2, 0)))
                    _cloudFeedbackOpen = false;
                ImGui.End();
            }
        }
    }

    /// <summary>路线回放：对当前（已加载）路线从「就近段」跳到「最远段」。起点「就近」= 自动取最近的段落起点
    /// （XZ 最近且同 Y）；终点「最远」= 最后一段。线性 / 碎片模式通用（悬浮窗「路线回放」按钮用）。</summary>
    private void LoadAndJumpBack()
    {
        var route = _recorder.CurrentRoute;
        if (route == null || route.Segments.Count == 0)
        {
            Service.ChatGui.PrintError("无路线或尚无段");
            return;
        }
        var start = FindNearestTakeoffSegment(route);
        if (start < 0)
        {
            Service.ChatGui.PrintError("附近无段落起点，请先走到路线起点附近");
            return;
        }
        var end = route.Segments.Count - 1;
        if (start > end) start = end; // 防御收敛：就近起点不可能超过最后一段（正常不可达），防死代码提示
        _replay.StartRouteSegments(route, start, end);
    }

    /// <summary>路线回放：按悬浮窗下拉选择的起点段 → 终点段 回放当前已加载路线（线性模式用）。
    /// 起点「就近」= 自动取最近的段落起点；终点「最远」= 最后一段。</summary>
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

        var start = StartSegSel;
        if (start == -2)
        {
            start = FindNearestTakeoffSegment(route);
            if (start < 0)
            {
                Service.ChatGui.PrintError("附近无段落起点，请先走到路线起点附近或手动选段");
                return;
            }
        }
        var end = EndSegSel;
        if (end == -2)
            end = route.Segments.Count - 1;

        if (start > end)
        {
            // 就近起点已越过所选终点（玩家实际站在终点段之后）：终点自动收敛到起点段，
            // 杜绝"起点>终点"的无意义区间（选择上已尽量约束，就近是动态的，此处兜底静默收敛）
            PluginLog.Info($"RunSelection: 就近起点段 {start + 1} 已越过所选终点段，终点自动收敛为起点段");
            end = start;
        }
        if (end >= route.Segments.Count)
        {
            Service.ChatGui.PrintError("终点段超出范围");
            return;
        }
        _replay.StartRouteSegments(route, start, end);
    }

    /// <summary>就近起点：遍历段起跳点，找 XZ 最近且 |Y差| ≤ 当前模式 Y 容差的段（同平台）；无匹配返回 -1。</summary>
    private int FindNearestTakeoffSegment(RouteFile route)
    {
        var player = Service.ObjectTable.LocalPlayer;
        if (player == null)
            return -1;
        int best = -1;
        var bestD = float.MaxValue;
        var yTol = Service.Config.SegmentMode == SegmentMode.Fragment
            ? Service.Config.FragYAlignTolerance
            : Service.Config.YAlignTolerance;
        for (int i = 0; i < route.Segments.Count; i++)
        {
            var t = route.Segments[i].Takeoff;
            if (MathF.Abs(t.Y - player.Position.Y) > yTol)
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
            Service.ChatGui.PrintError("当前位置与任一段起跳点距离过远，无法重录，请走到要重录的段起跳点附近");
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
        // 「插入新段」按钮已移至悬浮窗（线性模式独有）；碎片模式段落无序号、由距离衔接，插入无意义。
        var extEnabled = Service.Config.ExtendedTimeline;
        var isLinear = Service.Config.SegmentMode == SegmentMode.Linear;
        if (extEnabled)
            ImGui.TextDisabled("扩展时间线已开启，新的段落的操作将会被更完整的记录。");
        if (Service.Config.SegmentMode == SegmentMode.Fragment)
            ImGui.TextDisabled("碎片模式：段落仅按距离/高度衔接，无「截断」——删除多余段用「删除」");
        ImGui.Spacing();

        // 列数随扩展开关与模式变化：扩展列（开启时）+ 截断列（仅线性）。基础列 = 段/重录/删除。
        var colCount = 3 + (extEnabled ? 1 : 0) + (isLinear ? 1 : 0);
        var headers = new[] { "段", "重录", "截断", "删除" }
            .Where(h => h != "截断" || isLinear)
            .Concat(extEnabled ? new[] { "扩展" } : Array.Empty<string>())
            .ToArray();
        if (ImGui.BeginTable("##segedit", colCount, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            foreach (var h in headers)
            {
                ImGui.TableSetupColumn(h);
            }
            ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
            foreach (var h in headers)
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

                if (isLinear)
                {
                    ImGui.TableNextColumn();
                    CenterWidget(ImGui.GetFrameHeight() * 2.6f);
                    if (ImGui.Button($"截断##c{i}"))
                    {
                        if (_recorder.CutFrom(i))
                            break; // 段数变化，结束本帧（下一帧重绘）
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("删除该段及之后所有段（后半段重录用）");
                }

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

    /// <summary>弹窗居中：设置下次窗口尺寸，并在屏幕中心弹出（Appearing=每次打开都居中，防止弹在角落被漏看）。</summary>
    private static void CenterNextPopup(Vector2 size, ImGuiCond sizeCond)
    {
        ImGui.SetNextWindowSize(size, sizeCond);
        ImGui.SetNextWindowPos(ImGui.GetMainViewport().GetCenter(), ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
    }

    /// <summary>按像素宽度截断文本（超出加 …，供信息行路线名显示）。</summary>
    private static string TruncateText(string s, float maxW)
    {
        if (maxW <= 8 || string.IsNullOrEmpty(s))
            return "";
        if (ImGui.CalcTextSize(s).X <= maxW)
            return s;
        const string ell = "…";
        var res = s;
        while (res.Length > 1 && ImGui.CalcTextSize(res + ell).X > maxW)
            res = res[..^1];
        return res + ell;
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
            _routeModeLabels = new string[_routeNames.Length];
            _routeStatusLabels = new string[_routeNames.Length];
            for (int i = 0; i < _routeNames.Length; i++)
                DescribeRoute(_routeNames[i], out _routeMapLabels[i], out _routeModeLabels[i], out _routeStatusLabels[i]);
            if (_selectedRoute >= _routeNames.Length)
                _selectedRoute = Math.Max(0, _routeNames.Length - 1);
        }

        var w = ImGui.GetContentRegionAvail().X;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var btnW = (w - spacing * 3) * 0.25f;

        // 操作行：新建 / 加载 / 删除（未按 Ctrl 灰暗不可点）/ 导出（「目录」已移至基础设置）
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

        ImGui.Separator();
        ImGui.TextDisabled("双击路线名直接加载");

        if (_routeNames.Length == 0)
        {
            ImGui.TextDisabled("（暂无路线）");
            return;
        }

        // 路线列表（分列表格）：名称 | 地图 | 模式 | 速度 | 上传。缓存数组每秒刷新时一次性计算——
        // 避免每帧 Load JSON（DescribeRoute 内部读文件+解析，路线多时掉帧）。
        if (ImGui.BeginTable("##routelist", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(w, 0)))
        {
            ImGui.TableSetupColumn("名称", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("地图", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("模式", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("速度", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("上传", ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
            foreach (var h in new[] { "名称", "地图", "模式", "速度", "上传" })
            {
                ImGui.TableNextColumn();
                CenterText(h);
            }

            for (int i = 0; i < _routeNames.Length; i++)
            {
                var name = _routeNames[i];
                var mapLabel = i < _routeMapLabels.Length ? _routeMapLabels[i] : "未知";
                var modeLabel = i < _routeModeLabels.Length ? _routeModeLabels[i] : "未知";
                var statusLabel = i < _routeStatusLabels.Length ? _routeStatusLabels[i] : "";
                bool loadRequested = false;

                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                // 不用 SpanAllColumns：Selectable 先画会抢占整行 hover/点击（ImGui 先画者优先），
                // 导致第 4 列「上传」按钮永远点不到——Selectable 只占名称列，上传按钮独立可点。
                var rowCellPos = ImGui.GetCursorPos();
                var col0W = ImGui.GetColumnWidth();
                if (ImGui.Selectable($"##row{i}", i == _selectedRoute))
                    _selectedRoute = i;
                if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                    loadRequested = true;
                var nameW = ImGui.CalcTextSize(name).X;
                ImGui.SetCursorPos(rowCellPos);
                ImGui.SetCursorPosX(rowCellPos.X + MathF.Max(0f, (col0W - nameW) * 0.5f));
                ImGui.TextUnformatted(name);
                ImGui.TableSetColumnIndex(1);
                CenterText(mapLabel);
                ImGui.TableSetColumnIndex(2);
                CenterText(modeLabel);
                ImGui.TableSetColumnIndex(3);
                CenterText(statusLabel);
                ImGui.TableSetColumnIndex(4);
                CenterWidget(ImGui.GetFrameHeight() * 2.6f);
                if (ImGui.Button($"上传##up{i}"))
                {
                    _cloudUploadName = name;
                    _cloudNickname = Service.Config.CloudUploaderNickname; // 记忆上次昵称
                    _cloudDescription = "";
                    _cloudUploadOpen = true;
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("上传该路线到云端分享（需先在基础设置配置云端桶与上传凭证）");

                if (loadRequested)
                {
                    if (_recorder.LoadRouteForRecord(name))
                        _routeListOpen = false;
                    else
                        Service.ChatGui.PrintError("加载失败（查看日志）");
                }
            }
            ImGui.EndTable();
        }
    }

    /// <summary>路线描述（分列）：「地图」= 录制地图名（旧路线按 TerritoryId 查表）；「模式」= 路线段落方式；
    /// 「状态」= 移速状态汇总（常速 / 冲刺 / 慢跑·速行 / 慢走 组合——起跳速度一致性提示）。加载失败时取"未知"/空。</summary>
    private void DescribeRoute(string name, out string mapLabel, out string modeLabel, out string statusLabel)
    {
        mapLabel = "未知";
        modeLabel = "未知";
        statusLabel = "";
        try
        {
            var route = _routeStore.Load(name);
            if (route == null)
                return;
            mapLabel = !string.IsNullOrWhiteSpace(route.TerritoryName)
                ? route.TerritoryName
                : ResolveMapName(route.TerritoryId);
            modeLabel = route.SegmentMode == SegmentMode.Linear ? "线性" : "碎片";
            statusLabel = route.MoveStateSummary();
        }
        catch
        {
            // 保持 未知/空
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

    /// <summary>云端路线 tab：搜索 + 地图筛选 + 云端列表（名称/地图/模式/上传人/描述/下载）。
    /// 上传/下载配置已硬编码（CloudRouteService），玩家零配置直接使用。
    /// ⚠️ 云端清单必须缓存（每秒刷新）——每帧 ListRouteInfos() 是同步 HTTP，UI 线程阻塞 → 帧数爆降（实测 17）。</summary>
    private void DrawCloudRouteList()
    {
        ImGui.Spacing();

        // 云端清单缓存：每秒触发一次【后台线程】拉取（Task.Run），UI 线程不阻塞——
        // 同步 HTTP 即使每秒一次也会卡帧（实测帧数掉），异步后帧率完全不受影响。
        if (Environment.TickCount64 - _cloudLastRefresh > 1000 && !_cloudLoading)
        {
            _cloudLastRefresh = Environment.TickCount64;
            _cloudLoading = true;
            Task.Run(() =>
            {
                try
                {
                    var r = CloudRouteService.ListRouteInfos();
                    if (r != null) { _cloudInfos = r; _cloudLoadError = false; }
                    else _cloudLoadError = true; // 失败：保留旧缓存，显示重试提示
                }
                catch { _cloudLoadError = true; }
                _cloudLoading = false;
            });
        }
        // 清单更新后（引用变化）在主线程重建地图筛选项（List 不能跨线程并发写）
        if (!ReferenceEquals(_lastCloudInfos, _cloudInfos))
        {
            _lastCloudInfos = _cloudInfos;
            _cloudMapOptions.Clear();
            foreach (var c in _cloudInfos)
                if (!string.IsNullOrWhiteSpace(c.MapName) && !_cloudMapOptions.Contains(c.MapName))
                    _cloudMapOptions.Add(c.MapName);
        }

        // 搜索 + 地图筛选 + 刷新按钮
        var w = ImGui.GetContentRegionAvail().X;
        var searchW = (w - ImGui.GetStyle().ItemSpacing.X) * 0.5f;
        ImGui.SetNextItemWidth(searchW);
        ImGui.InputTextWithHint("##cloud_search", "搜索路线名/上传人…", ref _cloudSearch, 64);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(searchW - ImGui.GetFrameHeight() * 2.4f - ImGui.GetStyle().ItemSpacing.X);
        var mapFilter = _cloudMapFilter.Length == 0 ? "全部地图" : _cloudMapFilter;
        if (ImGui.BeginCombo("##cloud_map", mapFilter))
        {
            if (ImGui.Selectable("全部地图", _cloudMapFilter.Length == 0))
                _cloudMapFilter = "";
            foreach (var m in _cloudMapOptions)
            {
                if (ImGui.Selectable(m, _cloudMapFilter == m))
                    _cloudMapFilter = m;
            }
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("按地图筛选（从云端清单的地图列提取）");
        ImGui.SameLine();
        if (ImGui.Button("刷新", new Vector2(ImGui.GetFrameHeight() * 2.4f, 0)))
            _cloudLastRefresh = 0; // 立即重新拉取
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("立即刷新云端路线列表");

        ImGui.Spacing();
        var info = _cloudInfos;
        if (info.Count == 0)
        {
            if (_cloudLoadError)
            {
                ImGui.TextColored(new Vector4(1f, 0.4f, 0.3f, 1f), "云端清单加载失败（网络/代理问题）");
                ImGui.TextDisabled("点右上角「刷新」重试");
            }
            else if (_cloudLoading)
            {
                ImGui.TextDisabled("云端清单加载中…");
            }
            else
            {
                ImGui.TextColored(new Vector4(0.9f, 0.45f, 0.1f, 1f), "云端暂没有路线");
                ImGui.TextDisabled("（上传自己的第一条路线吧）");
            }
            return;
        }

        // 过滤 + 分页（每页 5 条）
        const int PageSize = 5;
        var filtered = info
            .Where(c => _cloudSearch.Length == 0
                || c.Name.Contains(_cloudSearch, StringComparison.OrdinalIgnoreCase)
                || c.Uploader.Contains(_cloudSearch, StringComparison.OrdinalIgnoreCase))
            .Where(c => _cloudMapFilter.Length == 0 || c.MapName == _cloudMapFilter)
            .ToList();
        var totalPages = Math.Max(1, (filtered.Count + PageSize - 1) / PageSize);
        if (_cloudPage >= totalPages) _cloudPage = totalPages - 1;
        if (_cloudPage < 0) _cloudPage = 0;
        var pageItems = filtered.Skip(_cloudPage * PageSize).Take(PageSize).ToList();

        // 翻页栏（置于表格上方——时刻可见，无需滚动窗口）
        var barW = ImGui.GetContentRegionAvail().X;
        if (_cloudPage > 0)
        {
            if (ImGui.Button("上一页", new Vector2(80, 0)))
                _cloudPage--;
        }
        else
        {
            ImGui.BeginDisabled();
            ImGui.Button("上一页", new Vector2(80, 0));
            ImGui.EndDisabled();
        }
        ImGui.SameLine();
        ImGui.TextDisabled($"第 {_cloudPage + 1}/{totalPages} 页（共 {filtered.Count} 条）");
        ImGui.SameLine(barW - 80f);
        if (_cloudPage < totalPages - 1)
        {
            if (ImGui.Button("下一页", new Vector2(80, 0)))
                _cloudPage++;
        }
        else
        {
            ImGui.BeginDisabled();
            ImGui.Button("下一页", new Vector2(80, 0));
            ImGui.EndDisabled();
        }
        ImGui.Spacing();

        // 云端列表：名称 | 地图 | 模式 | 上传人 | 描述 | 操作（下载/反馈横排）——固定高度防把翻页栏挤出视口
        if (ImGui.BeginTable("##cloudlist", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(w, 300)))
        {
            ImGui.TableSetupColumn("名称", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("地图", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("模式", ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableSetupColumn("上传人", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("描述", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.WidthFixed, 120f);
            ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
            foreach (var h in new[] { "名称", "地图", "模式", "上传人", "描述", "操作" })
            {
                ImGui.TableNextColumn();
                CenterText(h);
            }

            foreach (var c in pageItems)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                CenterText(c.Name);
                ImGui.TableSetColumnIndex(1);
                CenterText(c.MapName);
                ImGui.TableSetColumnIndex(2);
                CenterText(c.ModeLabel);
                ImGui.TableSetColumnIndex(3);
                CenterText(c.Uploader);
                ImGui.TableSetColumnIndex(4);
                // 描述：截断显示，悬停显示完整内容（不换行也能看全）
                var descText = c.Description.Length > 12 ? c.Description[..12] + "…" : c.Description;
                CenterText(descText);
                if (c.Description.Length > 12 && ImGui.IsItemHovered())
                    ImGui.SetTooltip(c.Description);
                ImGui.TableSetColumnIndex(5);
                // 操作列：下载 + 反馈 横排
                var opW = (ImGui.GetColumnWidth() - ImGui.GetStyle().ItemSpacing.X) * 0.5f;
                if (ImGui.Button($"下载##cd{c.Name}", new Vector2(opW, 0)))
                {
                    var route = CloudRouteService.DownloadRoute(c.Name);
                    if (route != null && _routeStore.Save(route))
                    {
                        _lastRouteRefresh = 0; // 刷新本地列表（新下载的路线立即可见）
                        Service.ChatGui.Print($"云端路线「{c.Name}」已下载到本地");
                    }
                    else
                        Service.ChatGui.PrintError("下载失败（查看日志）");
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("下载该路线到本地");
                ImGui.SameLine();
                if (ImGui.Button($"反馈##fb{c.Name}", new Vector2(opW, 0)))
                {
                    _cloudFeedbackName = c.Name;
                    _cloudFeedbackText = "";
                    _cloudFeedbackOpen = true;
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("反馈该路线的问题/建议");
                // 删除（仅本机上传过的路线有 del_token 才显示——上传者只能删自己的）
                if (Service.Config.CloudDelTokens.TryGetValue(c.Name, out var delTok) && !string.IsNullOrEmpty(delTok))
                {
                    ImGui.SameLine();
                    if (ImGui.Button($"删除##del{c.Name}", new Vector2(opW, 0)))
                    {
                        if (CloudRouteService.DeleteRoute(c.Name, delTok))
                        {
                            Service.ChatGui.Print($"已删除云端路线「{c.Name}」");
                            Service.Config.CloudDelTokens.Remove(c.Name);
                            Service.Config.Save();
                            _cloudLastRefresh = 0; // 刷新列表
                        }
                        else
                            Service.ChatGui.PrintError("删除失败（查看日志）");
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("删除自己上传的云端路线");
                }
            }
            ImGui.EndTable();
        }
    }

    /// <summary>云端上传弹窗：填上传人昵称 + 路线描述 → 确认上传（函数校验后写云端）。</summary>
    private void DrawCloudUpload()
    {
        ImGui.Text($"路线：{_cloudUploadName}");
        ImGui.Spacing();

        ImGui.Text("上传人昵称");
        ImGui.SetNextItemWidth(300f);
        ImGui.InputText("##cloud_nick", ref _cloudNickname, 32);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("显示在云端列表的「上传人」列（分享者署名）");

        ImGui.Text("路线描述");
        ImGui.SetNextItemWidth(300f);
        ImGui.InputText("##cloud_desc", ref _cloudDescription, 120);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("给其他玩家看的路线说明（地图/路线要点等）");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var w = ImGui.GetContentRegionAvail().X;
        var halfW = (w - ImGui.GetStyle().ItemSpacing.X) * 0.5f;
        var canGo = _cloudNickname.Trim().Length > 0; // 上传凭证已硬编码，只需昵称
        if (!canGo) ImGui.BeginDisabled();
        if (ImGui.Button("确认上传", new Vector2(halfW, 0)))
        {
            var route = _routeStore.Load(_cloudUploadName);
            if (route == null)
            {
                Service.ChatGui.PrintError("路线加载失败，无法上传");
            }
            else
            {
                var (ok, delToken) = CloudRouteService.UploadRoute(route, _cloudNickname.Trim(), _cloudDescription.Trim());
                if (ok)
                {
                    Service.Config.CloudUploaderNickname = _cloudNickname.Trim(); // 记忆昵称
                    if (!string.IsNullOrEmpty(delToken))
                    {
                        Service.Config.CloudDelTokens[route.Name] = delToken; // 保存删除令牌（仅本机）
                        Service.ChatGui.Print($"路线「{_cloudUploadName}」已上传到云端（可删除自己的此路线）");
                    }
                    else
                    {
                        Service.ChatGui.Print($"路线「{_cloudUploadName}」已上传到云端");
                    }
                    Service.Config.Save();
                    _cloudLastRefresh = 0; // 上传成功立即刷新云端列表（缓存）
                    _cloudUploadOpen = false;
                }
                else
                {
                    Service.ChatGui.PrintError("上传失败（查看日志）");
                }
            }
        }
        if (!canGo) ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(canGo ? "上传到云端路线库" : "需填写上传人昵称");
        ImGui.SameLine();
        if (ImGui.Button("取消", new Vector2(halfW, 0)))
            _cloudUploadOpen = false;
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
    // ===== 文件对话框（WinForms 需 STA 线程——统一实现见 Utils/Dialogs.cs） =====

    /// <summary>切换段落记录方式（线性 / 碎片）。切换后强制「重新选择或新建路线」：先停回放，
    /// 再卸载当前路线（自动保存），回归无路线状态——避免旧模式下录的路线被新模式直接接着录
    /// （线性按序号跳、碎片按距离/高度衔接，衔接方式本质不同，绝不可混用）。</summary>
    private void SwitchMode(SegmentMode target, string hint)
    {
        if (Service.Config.SegmentMode == target)
            return;
        var hadRoute = _recorder.CurrentRoute != null;
        StopAll();
        if (hadRoute)
            _recorder.UnloadRoute();
        _lastRouteRef = null; // 强制下一帧重置起点/终点选择
        StartSegSel = -1;
        EndSegSel = -1;
        Service.Config.SegmentMode = target;
        Service.Config.Save();
        ChatInfo($"已切换到{hint}" + (hadRoute
            ? "——原路线已卸载，请重新选择或新建路线"
            : "——请新建或选择路线"));
    }

    /// <summary>聊天栏提示。</summary>
    private static void ChatInfo(string msg) => Service.ChatGui.Print(msg);

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
        if (ImGui.Button(label, new Vector2(width, 45)))
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
