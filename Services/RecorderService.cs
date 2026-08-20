using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using JumpHelper.Models;

namespace JumpHelper.Services;

/// <summary>
/// 录制服务（段采集模型）：进入录制模式后，插件在后台监听玩家的"起跳/落地"，
/// 自动采集每次跳跃的"起跳点 → 落点"段；玩家在关键落点 rec node 打标，
/// 以便回放时选择起终点。
///
/// 录制流程：
///   rec start 开始 → 玩家正常跳跳乐 → rec node（关键落点打标）→ rec undo（撤销误跳的段）→ rec end 保存。
/// 起跳/落地检测 = 双通道：
///   通道1（主）：游戏跳跃状态标志（ConditionFlag.Jumping）上升沿=起跳、下降沿=落地——位置准确、不依赖下降高度阈值
///     （解决"两平台高度差接近跳跃上限、下落段短、累计下降不足 0.15m"导致的落地漏检）；
///   通道2（兜底）：Y 累计上升 0.2m=起跳、累计下降 0.15m+回稳=落地（标志未置位的跳跃/滑落/坠落）。
/// </summary>
public sealed class RecorderService : IDisposable
{
    private readonly RouteStore _store;
    private readonly MovementController _movement;
    private readonly JumpExecutor _jump;
    private readonly ReplayEngine _replay;
    private readonly IPluginLog _log;

    private RouteFile? _current;
    private Vector3? _pendingTakeoff;   // 已采集到起跳点，等待落地
    private float _prevY;
    private long _pendingSince;         // 起跳点挂起时刻（超时丢弃）
    private Vector3 _runStart;          // 当前段冲刺起点（起跳前最后直线冲刺段起点）
    private float _takeoffYaw;          // 当前段起跳瞬间朝向
    private float _takeoffSpeed;        // 当前段起跳瞬间水平速度（XZ，m/s）
    private MoveState _takeoffMoveState; // 当前段起跳瞬间的移速状态（冲刺/慢跑/速行——回放需状态一致）

    // 游戏跳跃状态标志通道（ConditionFlag.Jumping 边沿检测，起跳/落地唯一判据）：
    // 起跳 = 标志上升沿；落地 = 标志下降沿 → 等待站稳（回稳/超时兜底）→ 采样落点。
    // 不再用 Y 累计判据——爬坡/地形上升累计 0.2m 会误判起跳（用户实测遇到）。
    private bool _wasJumping;           // 上一帧跳跃标志状态（边沿检测）
    private bool _flagLandPending;      // 标志已清除，等待站稳后采样落点
    private long _flagLandSince;        // 标志清除时刻（站稳超时兜底）

    // 全量输入时间线采集
    private readonly List<(long Time, float Left, float Fwd, float Turn, bool Jump, float Yaw)> _inputHistory = new();
    private long _segmentInputStart;    // 段时间线起始时刻（起跳前 InputLeadMs，但连续跳时从上次落地后开始）
    private Vector3 _segmentStartPos;   // 段时间线起点位置（回放对齐目标）
    private float _segmentStartYaw;     // 段时间线起点朝向（回放先转向到此朝向再重放输入）
    private float _segmentStartSpeed;   // 段时间线起点水平速度（回放预助跑到此速度再重放——位置轨迹对齐）
    private long _lastLandAt = long.MinValue; // 上次落地时刻（连续跳时间线起点不能跨进上一跳空中）
    private Vector3? _lastLandPos;            // 上次落地位置（扩展时间线：段间位移自动判断）

    /// <summary>重录段的扩展标记（null = 自动）：RerecordFrom 读取被重录段的 Extended——
    /// true 按扩展录制（时间线覆盖行走起点）；false/null 按短时间线。</summary>
    private bool? _pendingExtended;

    /// <summary>当前采集段的扩展状态（OnTakeoffDetected 判定，OnLandDetected 写入段数据）。</summary>
    private bool _lastSegmentExtended;

    // 位置历史（冲刺起点回溯 + 时间线起点朝向）：最近 HistoryMs 毫秒的 (时间, 位置, 朝向)。
    // 注：大量索引访问（FindStartPos/速度回溯），保持 List；RemoveAt(0) O(n) 但窗口仅 ~120 帧
    //（微秒级元组移动），非性能瓶颈（已评估 Queue 需 ToArray 反而不值）。
    private readonly List<(long Time, Vector3 Pos, float Yaw)> _posHistory = new();

    // 采集暂停（无意义路径管理）：
    // - 手动：/ja rec pause 或 UI「暂停记录」按钮（回程/离开跳跳乐时暂停，避免录入无意义跳跃）；
    // - 自动：地图切换（传送/换图）自动暂停——录制中传送到其他地图再回来，回程跳跃不应录入。
    private bool _paused;
    private uint _lastTerritory;

    // 记录状态机：
    // - _current != null = 有路线对象（「新建」或「加载」后即存在，此时仅"已建未录"，不采集跳跃）；
    // - _recordingActive = true = 已点「记录开始」（正式记录：跳跃自动采集为段）。
    // 分离后支持：新建/加载路线 → 读档/跳回（不采集）→ 记录开始 → 跳跳乐 → 暂停记录 → 记录保存。
    private bool _recordingActive;

    /// <summary>重录中的段索引（-1 = 无）：段落编辑「重录」移除该段后，玩家重新跳这一跳，
    /// 落地采集的新段 Insert 回原序号位置（只替换当前段，不丢弃后续段）；-1 = 正常 Append。</summary>
    private int _rerecordIndex = -1;

    /// <summary>插入新段的插入位置（-1 = 无）：段落编辑「插入新段」（仅线性模式）时，以就近落点段
    /// （同高度最近）为基准，其后的段序号（基准段索引 + 1）即插入点；玩家跳一跳，落地新段
    /// Insert 到此位置（后续段顺延），解决"段 12 变段 11 后无法补录段 11"的补段需求。</summary>
    private int _insertIndex = -1;

    // 起跳/落地（全部由游戏跳跃状态标志 ConditionFlag.Jumping 判定；Y 阈值判据已弃——爬坡误判）
    private const long PendingTimeoutMs = 15000; // 起跳点 15s 未落地（标志未清除）= 异常，丢弃。
    // 注：原来 3s——跳跳乐「从高空跳到低平台」机制滞空可达 5~10s，3s 会把好不容易命中的段丢弃
    // （用户实测踩坑）。15s 覆盖高空坠落且保留"标志卡死自愈"安全网（真异常 15s 后自动清除）。
    private const float LandSettleDeltaY = 0.02f; // 标志清除后等待站稳：单帧下降小于此值 = 站稳
    private const long FlagLandSettleMs = 150;  // 标志清除后等待站稳的超时兜底（正常回稳更快）
    private const int HistoryMs = 2000;          // 位置/输入历史窗口（时间线起点查询用，2000ms 覆盖起跳前 600ms 富余）
    private const long InputLeadMs = 600;        // 短路径段时间线覆盖起跳前 600ms（助跑/微调）
    private const long LandBufferMs = 150;       // 连续跳：时间线从"上次落地后 150ms"开始（跳过落地惯性/空中残留帧）
    private const float MoveSpeed = 0.5f;        // m/s：XZ 速度超过即视为移动（走路~1.5 跑步~6；宁低勿高——短助跑加速段速度低）

    public RecorderService(RouteStore store, MovementController movement, JumpExecutor jump, ReplayEngine replay)
    {
        _store = store;
        _movement = movement;
        _jump = jump;
        _replay = replay;
        _log = Service.Log;
        Service.Framework.Update += OnUpdate;
    }

    public bool IsRecording => _current != null;

    /// <summary>是否已点「记录开始」（正在记录：跳跃自动采集为段）。新建/加载后未记录时为 false。</summary>
    public bool IsRecordingActive => _recordingActive;

    /// <summary>采集是否暂停（暂停期间不录入跳跃；录制状态保留）。</summary>
    public bool IsPaused => _paused;

    /// <summary>切换采集暂停/恢复（回程/离开跳跳乐时暂停，避免录入无意义跳跃）。</summary>
    public bool TogglePause()
    {
        if (_current == null)
            return false;
        _paused = !_paused;
        // 暂停/恢复时清空挂起状态，避免把暂停前的跳跃残影记入恢复后的第一段；
        // _prevY 重置为当前 Y——暂停期间玩家移动/传送后恢复，deltaY 突变会误判起跳；
        // _wasJumping 对齐当前跳跃标志——恢复后起跳边沿检测不断档
        _pendingTakeoff = null;
        _flagLandPending = false;
        _prevY = Service.ObjectTable.LocalPlayer?.Position.Y ?? 0f;
        _wasJumping = Service.Condition[ConditionFlag.Jumping];
        Service.ChatGui.Print(_paused
            ? "记录已暂停——暂停期间跳跃不录入（回程/离开时可用），再按一次恢复"
            : "记录已恢复——继续采集跳跃段");
        PluginLog.Info($"记录{( _paused ? "已暂停" : "已恢复")}——{( _paused ? "暂停期间跳跃不录入（回程/离开时可用）" : "继续采集跳跃段")}");
        return true;
    }

    public RouteFile? CurrentRoute => _current;

    /// <summary>
    /// 新建路线（已建未录状态）：同名路线默认拒绝（新不覆盖旧——需 UI 确认覆盖后先删旧文件再调用）。
    /// 若当前已有路线（记录中/已建未录），先自动保存（切换不丢数据），再建立新路线。
    /// 新建后不立即采集跳跃，需点「记录开始」才正式记录（在当前位置记录第一个节点并开始采集）。
    /// </summary>
    public bool StartRecording(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        // 同名拒绝：默认保护旧文件（确认覆盖由 UI 处理：先 Delete 旧文件再 StartRecording）
        if (_store.Exists(name))
        {
            PluginLog.Info($"路线「{name}」已存在，拒绝覆盖（新不覆盖旧——请在 UI 确认覆盖，或换一个路线名）");
            return false;
        }

        // 切换/新建前自动保存当前路线（防丢：记录中直接新建会覆盖未保存的段）。
        // 同名跳过——覆盖确认流程先 Delete 旧文件，若此时保存同名会把它写回磁盘（覆盖失效）。
        // 开关：自动保存间隔 0 = 关闭（仅手动「保存路线」落盘）。
        if (_current != null && _current.Name != name && Service.Config.AutoSaveEvery > 0)
        {
            _store.Save(_current);
            PluginLog.Info($"自动保存（新建前）: {_current.Name}（{_current.Segments.Count} 段）");
        }

        _current = new RouteFile
        {
            Name = name,
            TerritoryId = Service.ClientState.TerritoryType,
            TerritoryName = ResolveTerritoryName(Service.ClientState.TerritoryType),
            ControlMode = _movement.IsLegacyMode ? 1 : 0, // 录制模式：输入语义依赖操作模式，读档一致性检测用
            SegmentMode = Service.Config.SegmentMode // 记录当前段落方式（线性/碎片），持久化供回放参考
        };
        _recordingActive = false; // 新建=已建未录，点「记录开始」才采集
        _rerecordIndex = -1;
        _insertIndex = -1;
        _pendingExtended = null;
        _pendingTakeoff = null;
        _pendingSince = 0;
        _lastLandAt = long.MinValue;
        _paused = false;
        _lastTerritory = Service.ClientState.TerritoryType;
        _wasJumping = false;
        _flagLandPending = false;
        _posHistory.Clear();
        _inputHistory.Clear();
        // 关键：_prevY 必须初始化为当前 Y，否则第一帧 deltaY = Y - 0 会误判起跳
        _prevY = Service.ObjectTable.LocalPlayer?.Position.Y ?? 0f;
        PluginLog.Info($"新建路线: {name} (Territory={_current.TerritoryId})——点「记录开始」开始采集跳跃");
        return true;
    }

    /// <summary>
    /// 加载已保存路线，恢复可记录状态（可继续「记录开始」追加段/打标，或选起终点段后点悬浮窗「路线回放」读档跳回）。
    /// 若当前已有路线（记录中/已建未录），先自动保存再切换（便于随时切换路线）。
    /// </summary>
    public bool LoadRouteForRecord(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        // 切换前自动保存当前路线（防丢；自动保存间隔 0 = 关闭）
        if (_current != null && Service.Config.AutoSaveEvery > 0)
        {
            _store.Save(_current);
            PluginLog.Info($"自动保存（切换前）: {_current.Name}（{_current.Segments.Count} 段）");
        }

        var route = _store.Load(name);
        if (route == null)
        {
            PluginLog.Error($"路线加载失败: {name}");
            return false;
        }

        // 段落方式自动切换：路线绑定录制时的段落方式（线性按序号、碎片按距离衔接），
        // 模式不符时自动切到路线模式（不再阻止——玩家无需手动切换，切换会卸载旧路线由悬浮窗 Draw 处理）
        if (route.SegmentMode != Service.Config.SegmentMode)
        {
            var routeMode = route.SegmentMode == SegmentMode.Linear ? "线性" : "碎片";
            var curMode = Service.Config.SegmentMode == SegmentMode.Linear ? "线性" : "碎片";
            Service.Config.SegmentMode = route.SegmentMode;
            Service.Config.Save();
            PluginLog.Info($"加载路线「{name}」：段落方式 {curMode} → {routeMode}（自动切换）");
            Service.ChatGui.Print($"当前路线为{routeMode}模式，已自动切换");
        }

        // 非当前地图阻止：路线绑定录制地图，加载非当前地图的路线必然回放失败（Territory 校验不过），
        // 且容易误以为是插件故障——直接阻止并提示需先传送到录制地图
        if (route.TerritoryId != Service.ClientState.TerritoryType)
        {
            var routeMap = ResolveTerritoryName(route.TerritoryId) ?? $"地图 {route.TerritoryId}";
            var curMap = ResolveTerritoryName(Service.ClientState.TerritoryType) ?? $"地图 {Service.ClientState.TerritoryType}";
            PluginLog.Info($"加载被阻止：路线「{name}」录制于 {routeMap}，当前在 {curMap}——请先传送到录制地图再加载");
            Service.ChatGui.PrintError($"「{name}」录制于 {routeMap}，当前在 {curMap}——请先传送到该地图再加载");
            return false;
        }

        _current = route;
        _recordingActive = false;
        _rerecordIndex = -1;
        _insertIndex = -1;
        _pendingExtended = null;
        _pendingTakeoff = null;
        _pendingSince = 0;
        _lastLandAt = long.MinValue;
        _paused = false;
        _lastTerritory = Service.ClientState.TerritoryType;
        _wasJumping = Service.Condition[ConditionFlag.Jumping];
        _flagLandPending = false;
        _posHistory.Clear();
        _inputHistory.Clear();
        _prevY = Service.ObjectTable.LocalPlayer?.Position.Y ?? 0f;
        // 操作模式自动切换：输入（sumLeft/sumForward）语义依赖录制时的操作模式（标准=相对角色朝向可复现；
        // 传统=相对相机不可复现），加载时自动切到录制模式（写游戏配置 MoveMode 立即生效），避免回放方向偏差。
        // 副作用：会改变玩家当前操作模式，回放结束后不会自动切回——聊天提示已说明。
        var wantControl = route.ControlMode == 1 ? 1u : 0u;
        if (Service.GameConfig.UiControl.TryGetUInt("MoveMode", out var curMoveMode) && curMoveMode != wantControl)
        {
            try
            {
                Service.GameConfig.UiControl.Set("MoveMode", wantControl);
                var modeName = wantControl == 1 ? "传统" : "标准";
                PluginLog.Info($"加载路线「{name}」：操作模式 {curMoveMode} → {wantControl}（自动切换为{modeName}模式）");
                Service.ChatGui.Print($"已自动切换为{modeName}操作模式，可在设置中切回");
            }
            catch (Exception ex)
            {
                var curName = curMoveMode == 1 ? "传统" : "标准";
                var recName = route.ControlMode == 1 ? "传统" : "标准";
                Service.Log.Error(ex, $"操作模式自动切换失败（MoveMode {curMoveMode} → {wantControl}）");
                Service.ChatGui.PrintError($"操作模式不一致：当前{curName}模式，该路线录制为{recName}——请在设置中手动切换");
            }
        }
        CheckControlMode(route); // 兜底：切换失败时仅日志
        CheckMoveStateWarning(route);
        PluginLog.Info($"已加载路线「{name}」（{route.Segments.Count} 段）——" +
                   $"点「记录开始」继续记录，或选起终点段后点悬浮窗「路线回放」读档跳回");
        return true;
    }

    /// <summary>
    /// 加载路线移速状态提醒：存在"状态段之后还有常速段"（如前半速行/后半常速）时提醒——
    /// 慢跑常驻后接常速段会跳过头（用户实测：起跳速度不匹配，状态必须一致），玩家需自行
    /// 在状态切换点手动取消移速状态（进战斗/切职业可清除——跳跳乐地图通常无怪，需玩家自行处理）。
    /// </summary>
    private void CheckMoveStateWarning(RouteFile route)
    {
        if (!route.HasStateBeforeNone())
            return;
        Service.ChatGui.PrintError($"路线「{route.Name}」含状态切换，回放后段需手动取消移速状态，否则跳过头");
        PluginLog.Info($"路线「{route.Name}」含状态切换后接常速段——回放后段需手动取消移速状态（跳过头风险）");
    }

    /// <summary>聊天黄色提示（信息类醒目提示——FF14 UIColor 45 淡黄）。</summary>
    private static void ChatInfo(string msg)
        => Service.ChatGui.Print(new SeStringBuilder().AddUiForeground(45).AddText(msg).Build());

    /// <summary>解析区域 ID 的地图名（路线列表显示用）；查询失败返回 null（显示回退 TerritoryId）。</summary>
    private static string? ResolveTerritoryName(uint territoryId)
    {
        try
        {
            var row = Service.DataManager.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>()?.GetRow(territoryId);
            return row?.PlaceName.ValueNullable?.Name.ToString();
        }
        catch (Exception ex)
        {
            Service.Log.Error(ex, $"地图名查询失败 Territory={territoryId}");
            return null;
        }
    }

    /// <summary>操作模式一致性检测：录制时模式（ControlMode）与当前模式不一致 → 仅记录日志。
    /// 加载路线时已自动切到录制模式（MoveMode），此处仅兜底记录——正常情况下不会触发。</summary>
    private void CheckControlMode(RouteFile route)
    {
        var cur = _movement.IsLegacyMode ? 1 : 0;
        if (cur == route.ControlMode)
            return;
        var modeName = cur == 1 ? "传统" : "标准";
        var recName = route.ControlMode == 1 ? "传统" : "标准";
        PluginLog.Info($"操作模式不一致（兜底）：当前 {modeName} vs 录制 {recName}——玩家可能手动改回，回放方向可能偏差");
    }

    /// <summary>
    /// 记录开始/继续：开始采集跳跃（后续跳跃自动成为段）。
    /// 新建/加载的路线均直接开始采集（节点概念已移除，不再打标）。
    /// </summary>
    public bool BeginRecording()
    {
        if (_current == null)
        {
            PluginLog.Error("记录开始失败：请先「新建」或「加载」路线");
            return false;
        }
        if (_recordingActive)
        {
            PluginLog.Info("已在记录中（「暂停记录」可暂停采集）");
            return false;
        }

        _recordingActive = true;
        _pendingTakeoff = null;
        _flagLandPending = false;
        Service.ChatGui.Print($"记录开始（路线：{_current.Name}）");
        PluginLog.Info("记录开始——后续跳跃将自动采集为段");

        // 录制防呆：带移速加成录制 → 起跳速度偏高，路线只能在相同状态下回放（状态必须一致，
        // 否则回放跳过头/跳不够——用户实测结论）。录制开始时提示当前状态，让玩家心里有数。
        var moveState = MoveStatusHelper.DetectCurrentState();
        if (moveState != MoveState.None)
        {
            ChatInfo($"当前带有影响速度的状态，路线将按照此状态录制，回放时需相同状态。请留意。");
            PluginLog.Info($"录制开始带移速状态 {MoveStatusHelper.StateName(moveState)}——路线将按该状态录制");
        }
        return true;
    }

    /// <summary>撤销最后一段（跳歪了重录）。</summary>
    public bool UndoLastSegment()
    {
        if (_current == null || _current.Segments.Count == 0)
            return false;

        var removed = _current.Segments[^1];
        _current.Segments.RemoveAt(_current.Segments.Count - 1);

        _pendingTakeoff = null;
        _flagLandPending = false;
        PluginLog.Info($"撤销最后一段（{removed.TakeoffX:F1},{removed.TakeoffY:F1},{removed.TakeoffZ:F1} → {removed.LandX:F1},{removed.LandY:F1},{removed.LandZ:F1}），剩余 {_current.Segments.Count} 段");
        return true;
    }

    /// <summary>从此截断：删除指定段及其之后所有段（中间路线出问题/后半段全废时，一次清掉重录后半段）。</summary>
    public bool CutFrom(int index)
    {
        if (_current == null || index < 0 || index >= _current.Segments.Count)
            return false;

        var removed = _current.Segments.Count - index;
        _current.Segments.RemoveRange(index, removed);

        _pendingTakeoff = null;
        _flagLandPending = false;
        PluginLog.Info($"从此截断：删除段 #{index + 1} 起共 {removed} 段，剩余 {_current.Segments.Count} 段");
        return true;
    }

    /// <summary>
    /// 重录：只重录指定段——要求玩家与该段起跳点同一 Y 轴（必须站在该段起跳点所在平台），
    /// 校验通过后**只移除该段**（后续段全部保留），进入记录状态；玩家重新跳这一跳，
    /// 落地采集的新段按原序号 Insert 回原位置（替换旧段），随后自动停止记录。
    /// 用于"中间某段出问题（回放成功率低/录歪）"时只替换这一跳，不影响前后段。
    /// </summary>
    public bool RerecordFrom(int segmentIndex)
    {
        if (_current == null || segmentIndex < 0 || segmentIndex >= _current.Segments.Count)
            return false;

        var player = Service.ObjectTable.LocalPlayer;
        if (player == null)
            return false;

        var seg = _current.Segments[segmentIndex];
        var isExt = seg.Extended == true; // 扩展段：重录需从行走起点（StartPos）开始
        var anchorY = isExt ? seg.StartY : seg.TakeoffY;
        var anchorLabel = isExt ? "行走起点" : "起跳点";
        var yAlign = Service.Config.SegmentMode == SegmentMode.Fragment
            ? Service.Config.FragYAlignTolerance
            : Service.Config.YAlignTolerance;
        if (MathF.Abs(player.Position.Y - anchorY) > yAlign) // 与当前模式 Y 对齐容差一致
        {
            PluginLog.Error($"重录失败：玩家 Y {player.Position.Y:F2} vs 段{segmentIndex + 1} {anchorLabel} Y {anchorY:F2}（差 >{yAlign:F2}m）——请先到该段{anchorLabel}所在平台");
            Service.ChatGui.PrintError($"当前位置与段 {segmentIndex + 1} {anchorLabel}距离过远，无法重录，请走到要重录的段附近");
            return false;
        }

        // 只移除该段（后续段保留），记录重录索引与扩展标记
        _current.Segments.RemoveAt(segmentIndex);
        _rerecordIndex = segmentIndex;
        _insertIndex = -1; // 重录与插入互斥（插入用 _insertIndex）
        _pendingExtended = seg.Extended; // 扩展段重录按扩展（时间线覆盖行走起点）；否则短时间线

        // 进入记录状态：扩展段从行走起点开始走+跳，短段从起跳点跳，落地后自动替换回原位置
        _recordingActive = true;
        _pendingTakeoff = null;
        _flagLandPending = false;
        _paused = false;
        _lastLandAt = long.MinValue;
        _lastLandPos = null; // 扩展段行走起点以玩家当前位置为锚（重录场景无上一段衔接）
        _posHistory.Clear();
        _inputHistory.Clear();
        _prevY = player.Position.Y;
        _wasJumping = Service.Condition[ConditionFlag.Jumping];

        PluginLog.Info($"重录开始：段 {segmentIndex + 1} 已移除（其余 {_current.Segments.Count} 段保留）" +
                   $"[{(isExt ? "扩展：从行走起点开始走+跳" : "短时间线：从起跳点跳")}]——落地后自动替换回原序号");
        ChatInfo($"段 {segmentIndex + 1} 重录开始，{(isExt ? "从行走起点走+跳" : "从起跳点起跳")}");
        return true;
    }

    /// <summary>
    /// 删除指定段（段表格内"删除"按钮，替代撤销——支持删任意段）。
    /// </summary>
    public bool DeleteSegmentAt(int index)
    {
        if (_current == null || index < 0 || index >= _current.Segments.Count)
            return false;

        var removed = _current.Segments[index];
        _current.Segments.RemoveAt(index);

        _pendingTakeoff = null;
        _flagLandPending = false;
        PluginLog.Info($"删除段 #{index + 1}（{removed.TakeoffX:F1},{removed.TakeoffY:F1},{removed.TakeoffZ:F1} → {removed.LandX:F1},{removed.LandY:F1},{removed.LandZ:F1}），剩余 {_current.Segments.Count} 段");
        return true;
    }

    /// <summary>
    /// 插入新段（仅线性模式）：以"就近落点段"（同平台 = XZ 最近且 |Y差| ≤ YAlignTolerance 的段）为基准，
    /// 在它之后插入一个占位段——玩家跳一跳，落地采集的新段会 Insert 到基准段的下一位（后续段顺延）。
    /// 解决"直接删掉段 N → 段 N+1 变为段 N → 无法补录原 N 段"的问题（补录即以最近的落点段为基准插入其后）。
    /// 与重录不同：重录替换原序号、后续段保留；插入是在任意位置补一段、后续段顺延。
    /// </summary>
    public bool InsertNewSegment()
    {
        if (_current == null || _current.Segments.Count == 0)
        {
            Service.ChatGui.PrintError("无路线或尚无段");
            return false;
        }
        // 仅线性模式支持插入（部分模式段落无序号、由距离衔接，插入无意义）
        if (Service.Config.SegmentMode != SegmentMode.Linear)
        {
            Service.ChatGui.PrintError("插入新段仅线性模式可用");
            return false;
        }

        var player = Service.ObjectTable.LocalPlayer;
        if (player == null)
            return false;

        // 就近落点段：XZ 最近且同平台（|Y差| ≤ InsertSegmentYAlign，线性模式独享设置项）
        int baseIndex = -1;
        var baseD = float.MaxValue;
        for (int i = 0; i < _current.Segments.Count; i++)
        {
            var land = _current.Segments[i].Land;
            if (MathF.Abs(land.Y - player.Position.Y) > Service.Config.InsertSegmentYAlign)
                continue;
            var dx = land.X - player.Position.X;
            var dz = land.Z - player.Position.Z;
            var d = dx * dx + dz * dz;
            if (d < baseD)
            {
                baseD = d;
                baseIndex = i;
            }
        }
        if (baseIndex < 0)
        {
            PluginLog.Error($"插入新段失败：就近无同平台落点段（|Y差|≤{Service.Config.InsertSegmentYAlign:F2}m）——请靠近要插入位置前的段落点");
            Service.ChatGui.PrintError("当前位置与任一段落地点距离过远，无法插入，请走到要插入的段的落地点附近");
            return false;
        }
        if (baseIndex < 0 || baseIndex >= _current.Segments.Count)
            return false;

        // 在基准段之后插入（baseIndex+1 = 下一段序号）
        _insertIndex = baseIndex + 1;
        _rerecordIndex = -1; // 重录与插入互斥
        _pendingExtended = null;
        _recordingActive = true;
        _pendingTakeoff = null;
        _flagLandPending = false;
        _paused = false;
        _lastLandAt = long.MinValue;
        _lastLandPos = null;
        _posHistory.Clear();
        _inputHistory.Clear();
        _prevY = player.Position.Y;
        _wasJumping = Service.Condition[ConditionFlag.Jumping];

        PluginLog.Info($"插入新段：以就近落点段 #{baseIndex + 1} 为基准——下一跳将记录为段 #{_insertIndex + 1}（原 #{_insertIndex + 1} 起顺延）");
        ChatInfo($"在段{baseIndex + 1}后插入段落，下一次跳跃将记录为段{_insertIndex + 1}");
        return true;
    }

    /// <summary>
    /// 执行（记录中使用）：丢弃"终点段之后"的废段，然后从起点段回放到终点段
    /// （跳跃，非直线走位——跳跳乐地形直线不可达）。回放期间采集自动暂停（OnUpdate 检查
    /// ReplayEngine.State），到位后恢复。玩家需自行到达起点段起跳点所在平台（高度匹配检查）。
    /// 纯跳回（不丢弃）用 ReplayEngine.StartRouteSegments（段表格/悬浮窗「路线回放」）。
    /// </summary>
    public bool LoadFromSegment(int startSegment, int endSegment)
    {
        if (_current == null || _current.Segments.Count == 0)
        {
            PluginLog.Error("读档失败：无路线或尚无跳跃段");
            return false;
        }

        if (startSegment < 0 || endSegment < startSegment || endSegment >= _current.Segments.Count)
        {
            PluginLog.Error($"读档失败：非法段范围 start={startSegment} end={endSegment} 段数={_current.Segments.Count}");
            return false;
        }

        // 防丢：读档丢弃废段前，先把读档前完整数据自动保存一份——读档后忘记"记录保存"也不丢
        // （自动保存间隔 0 = 关闭）
        if (Service.Config.AutoSaveEvery > 0)
        {
            _store.Save(_current);
            PluginLog.Info($"自动保存（读档前）: {_current.Name}（{_current.Segments.Count} 段）");
        }

        // 丢弃终点段之后的段（跌落后终点后的段都是废段，点读档即丢弃）
        var deleted = 0;
        while (_current.Segments.Count - 1 > endSegment)
        {
            _current.Segments.RemoveAt(_current.Segments.Count - 1);
            deleted++;
        }

        _pendingTakeoff = null;
        _lastLandAt = long.MinValue;
        _rerecordIndex = -1;
        _insertIndex = -1;
        _pendingExtended = null;
        _paused = false; // 读档=回到已走位置继续录，恢复采集（回放期间由 ReplayEngine.State 自动挡住）
        _wasJumping = Service.Condition[ConditionFlag.Jumping]; // 读档后回放跳跃期间采集被挡住；回放结束玩家继续跳，边沿对齐当前状态
        _flagLandPending = false;
        _posHistory.Clear();
        _inputHistory.Clear();

        PluginLog.Info($"读档: 段 {startSegment + 1} → {endSegment + 1}（丢弃 {deleted} 段），剩余 {_current.Segments.Count} 段。回放中暂停采集");
        _replay.StartRouteSegments(_current, startSegment, endSegment);
        return true;
    }

    /// <summary>保存当前路线（只保存，不停止记录——记录中随时防丢；切换/新建时也会自动保存）。</summary>
    public bool SaveCurrent()
    {
        if (_current == null)
            return false;
        _store.Save(_current);
        PluginLog.Info($"已保存（继续记录）: {_current.Name}（{_current.Segments.Count} 段）");
        return true;
    }

    /// <summary>卸载当前路线（取消加载）：自动保存后清除路线引用，世界标记不再绘制。已保存数据不受影响。</summary>
    public bool UnloadRoute()
    {
        if (_current == null)
            return false;
        // 卸载前自动保存（防丢；自动保存间隔 0 = 关闭——卸载即放弃未保存改动）
        if (Service.Config.AutoSaveEvery > 0)
        {
            _store.Save(_current);
            PluginLog.Info($"卸载路线（已自动保存）: {_current.Name}（{_current.Segments.Count} 段）");
        }
        _current = null;
        _recordingActive = false;
        _rerecordIndex = -1;
        _insertIndex = -1;
        _pendingExtended = null;
        _pendingTakeoff = null;
        _paused = false;
        return true;
    }

    /// <summary>记录保存：保存路线文件并停止记录（回到空闲）。</summary>
    public RouteFile? EndRecording()
    {
        if (_current == null)
            return null;

        var route = _current;
        _current = null;
        _recordingActive = false;
        _rerecordIndex = -1;
        _insertIndex = -1;
        _pendingExtended = null;
        _pendingTakeoff = null;
        _paused = false;
        _store.Save(route);
        PluginLog.Info($"记录保存: {route.Name}（{route.Segments.Count} 段）");
        return route;
    }

    // ===== 后台采集（仅录制模式） =====

    private void OnUpdate(IFramework framework)
    {
        if (_current == null)
            return;

        var territory = Service.ClientState.TerritoryType;

        // 切换地图自动卸载：带着地图A的路线切到地图B → 路线已不可用（加载时 Territory 校验阻止，
        // 回放起点校验也会拦），自动卸载 + 提示，防止玩家误以为路线还在
        if (_lastTerritory != 0 && _lastTerritory != territory && _current.TerritoryId != territory)
        {
            var routeMap = ResolveTerritoryName(_current.TerritoryId) ?? $"地图 {_current.TerritoryId}";
            var routeName = _current.Name;
            PluginLog.Info($"离开路线地图（{_lastTerritory} → {territory}），自动卸载路线「{routeName}」（录制于 {routeMap}）");
            Service.ChatGui.Print($"已离开路线地图（{routeMap}），自动卸载路线「{routeName}」");
            UnloadRoute(); // 卸载前自动保存（跟随 AutoSaveEvery 开关）
            _lastTerritory = territory;
            return;
        }

        // 地图切换自动暂停：录制中传送/换图（如测试传送到别处再回来）→ 自动暂停采集，
        // 回程/途中跳跃不录入。传回跳跳乐地图后需手动恢复（/ja rec pause 或 UI 按钮）。
        if (_lastTerritory != 0 && _lastTerritory != territory && _recordingActive)
        {
            _paused = true;
            _pendingTakeoff = null;
            _prevY = Service.ObjectTable.LocalPlayer?.Position.Y ?? 0f; // 换图 Y 突变，防止恢复后误判起跳
            PluginLog.Info($"录制已自动暂停采集（地图 {_lastTerritory} → {territory}）——回程跳跃不录入，回到跳跳乐后 /ja rec pause 恢复");
        }
        _lastTerritory = territory;

        // 已建未录（新建/加载后未点「记录开始」）：不采集跳跃，直接返回
        if (!_recordingActive)
            return;

        if (_paused)
        {
            // 暂停期间也跟踪跳跃标志（恢复后边沿检测不因暂停期间的跳跃/落地而跳变）
            _wasJumping = Service.Condition[ConditionFlag.Jumping];
            return;
        }

        // 读档回放期间暂停采集（回放的跳跃不应被当作新段录入）。
        // 回放结束（含失败/成功）即恢复——否则 Fail 后 State=Failed 会永久暂停采集（后续录制 0 段）
        if (_replay.State != ReplayState.Idle && _replay.State != ReplayState.Failed)
        {
            _wasJumping = Service.Condition[ConditionFlag.Jumping];
            return;
        }

        var p = Service.ObjectTable.LocalPlayer;
        if (p == null)
            return;

        var now = Environment.TickCount64;

        // 维护位置历史（冲刺起点回溯 + 时间线起点朝向）
        _posHistory.Add((now, p.Position, p.Rotation));
        while (_posHistory.Count > 0 && now - _posHistory[0].Time > HistoryMs)
            _posHistory.RemoveAt(0);

        // 维护输入历史（全量输入时间线；真实输入来自 RMIWalk Original + 空格缓冲 + 玩家朝向）
        _inputHistory.Add((now, _movement.LastRealLeft, _movement.LastRealForward,
                           _movement.LastRealTurn, _jump.IsJumpHeld(), p.Rotation));
        while (_inputHistory.Count > 0 && now - _inputHistory[0].Time > HistoryMs)
            _inputHistory.RemoveAt(0);

        var y = p.Position.Y;
        var deltaY = y - _prevY;
        _prevY = y;
        var jumping = Service.Condition[ConditionFlag.Jumping];

        // 起跳检测：游戏跳跃状态标志上升沿（唯一判据）。
        // Y 累计判据已弃——爬坡/地形上升累计 0.2m 会误判起跳（用户实测：较难路段折返向上时误记录）。
        if (_pendingTakeoff == null)
        {
            if (jumping && !_wasJumping)
                OnTakeoffDetected(p.Position, p.Rotation, now);
            _wasJumping = jumping;
            return;
        }

        // pending 超时丢弃：起跳后 3s 跳跃标志仍未清除（落地未发生）= 异常/悬停，清除以免污染后续段
        if (now - _pendingSince > PendingTimeoutMs)
        {
            var tk = _pendingTakeoff.Value;
            var disp = p.Position - tk;
            disp.Y = 0;
            PluginLog.Info($"起跳点未配对（挂起 {(now - _pendingSince) / 1000.0:F1}s，跳跃标志仍未清除）丢弃：" +
                       $"起跳({tk.X:F2},{tk.Y:F2},{tk.Z:F2}) 当前位置({p.Position.X:F2},{p.Position.Y:F2},{p.Position.Z:F2}) 水平移动 {disp.Length():F2}m");
            _pendingTakeoff = null;
            _flagLandPending = false;
            _wasJumping = jumping;
            return;
        }

        // 落地检测：跳跃标志下降沿（跳跃结束）→ 等待站稳（回稳/超时兜底）→ 采样落点。
        // 不依赖下降累计——"两平台高度差接近跳跃上限、下落段短"也不会漏检。
        if (!jumping && _wasJumping)
        {
            _flagLandPending = true;
            _flagLandSince = now;
        }
        if (_flagLandPending && (deltaY >= -LandSettleDeltaY || now - _flagLandSince > FlagLandSettleMs))
        {
            _flagLandPending = false;
            OnLandDetected(p.Position, now);
        }
        _wasJumping = jumping;
    }

    /// <summary>段间是否为长距离行走（上一段落点 → 起跳点水平位移 > LongWalkDist）。</summary>
    private bool IsLongWalk(Vector3 takeoffPos, out float dist)
    {
        if (_lastLandPos is { } lastLand)
        {
            var dx = takeoffPos.X - lastLand.X;
            var dz = takeoffPos.Z - lastLand.Z;
            dist = MathF.Sqrt(dx * dx + dz * dz);
            return dist > Service.Config.LongWalkDist;
        }
        dist = 0f;
        return false; // 首段（无上一落点）：短时间线
    }

    /// <summary>起跳点采集（跳跃标志上升沿触发）：记录起跳点/朝向/速度/时间线起点数据。</summary>
    private void OnTakeoffDetected(Vector3 pos, float yaw, long now)
    {
        _flagLandPending = false;
        _pendingTakeoff = pos;
        _pendingSince = now;
        _takeoffYaw = yaw;
        _takeoffSpeed = CalcTakeoffSpeed();
        _takeoffMoveState = MoveStatusHelper.DetectCurrentState(); // 起跳瞬间移速状态（回放一致性校验用）
        _runStart = FindSprintStart();
        // 时间线起点：
        //  扩展时间线（开关开，且段标记扩展 或 段间位移自动判断超阈值）→ 从"上次落地后第一个移动帧"
        //  （行走起点）开始，完整录制段间行走（回放完整复现——适合机制确定的自装修跳跳乐高精度场景）；
        //  否则短时间线：起跳前 InputLeadMs（覆盖助跑/微调），连续跳不跨进上一跳空中
        //  （落地后 LandBufferMs 内是空中惯性/落地瞬间不稳定帧）。段间长距离默认由回放等待玩家手动走（半自动）。
        var extended = Service.Config.ExtendedTimeline
                       && (_pendingExtended ?? IsLongWalk(pos, out _));
        if (extended)
        {
            var walkStart = _lastLandAt + LandBufferMs;
            long? walkStartAt = null;
            for (int i = 0; i < _inputHistory.Count; i++)
            {
                if (_inputHistory[i].Time < walkStart)
                    continue;
                if (MathF.Abs(_inputHistory[i].Fwd) > 0.1f || MathF.Abs(_inputHistory[i].Left) > 0.1f)
                {
                    walkStartAt = _inputHistory[i].Time;
                    break;
                }
            }
            _segmentInputStart = walkStartAt ?? Math.Min(now - InputLeadMs, now - 1);
        }
        else
        {
            _segmentInputStart = Math.Min(Math.Max(now - InputLeadMs, _lastLandAt + LandBufferMs), now - 1);
        }
        _pendingExtended = null;
        _lastSegmentExtended = extended;
        _segmentStartPos = FindPosAt(_segmentInputStart) ?? pos;
        _segmentStartYaw = FindYawAt(_segmentInputStart) ?? yaw;
        _segmentStartSpeed = CalcSpeedAt(_segmentInputStart);
        // 注：冲刺起点/助跑距离（FindSprintStart）在时间线模式下不参与回放（回放驱动=时间线起点+起始朝向+全量输入），
        // 日志不再打印该值（旧回溯结果会被走路微调污染，仅调试用）。
        PluginLog.Info($"采集起跳点（跳跃标志）({pos.X:F2},{pos.Y:F2},{pos.Z:F2}) " +
                   $"起跳速度 {_takeoffSpeed:F2}m/s 朝向 {_takeoffYaw:F3} " +
                   $"时间线起点 ({_segmentStartPos.X:F2},{_segmentStartPos.Y:F2},{_segmentStartPos.Z:F2}) 起始朝向 {_segmentStartYaw:F3} 起始速度 {_segmentStartSpeed:F2}m/s");
    }

    /// <summary>落地采样并生成段（跳跃标志下降沿 + 站稳后）。</summary>
    private void OnLandDetected(Vector3 pos, long now)
    {
        var landY = pos.Y;
        var takeoffY = _pendingTakeoff!.Value.Y;

        // 跌落段自动丢弃开关（设置页）：落点比起跳点低超过 FellDropHeight 视为跌落段，不记录。
        // 默认关闭——跳跳乐有"从高处往低处跳"的正常场景；开启仅用于不需要下落跳的路线。
        // 丢弃后自动暂停记录 + 聊天提示：跌落 = 玩家操作失误，暂停让玩家先回到起点，避免后续跳跃被误录。
        if (Service.Config.DropFellSegments && landY - takeoffY < -Service.Config.FellDropHeight)
        {
            var dropTakeoff = _pendingTakeoff.Value;
            _pendingTakeoff = null;
            _flagLandPending = false;
            _paused = true; // 自动暂停：跌落段丢弃后不再采集，玩家回位后手动恢复
            Service.ChatGui.PrintError($"检测到跌落段（高差 {landY - takeoffY:F2}m），已丢弃并暂停——回到起点后点「继续记录」");
            PluginLog.Info($"跌落段已按设置丢弃并自动暂停（高差 {landY - takeoffY:F2}m < -{Service.Config.FellDropHeight:F1}m）——" +
                       $"起跳({dropTakeoff.X:F2},{dropTakeoff.Y:F2},{dropTakeoff.Z:F2})");
            return;
        }

        // 跌落提示（不自动丢弃）：玩家通过"读档"按钮/命令选择丢弃。
        // 跌落 = 死亡 = 读档回最近存档点重录，由玩家决定何时丢弃，插件不替玩家做主。
        if (landY - takeoffY < -2.0f)
        {
            PluginLog.Info($"检测到疑似跌落（高差 {landY - takeoffY:F2}m）——已记录该段，" +
                       $"可「读档」（丢弃其后废段）或段行「删除」清除");
        }

        var segment = new JumpSegment
        {
            TakeoffX = _pendingTakeoff.Value.X,
            TakeoffY = _pendingTakeoff.Value.Y,
            TakeoffZ = _pendingTakeoff.Value.Z,
            LandX = pos.X,
            LandY = pos.Y,
            LandZ = pos.Z,
            LandYaw = Service.ObjectTable.LocalPlayer?.Rotation ?? 0f,
            RunStartX = _runStart.X,
            RunStartY = _runStart.Y,
            RunStartZ = _runStart.Z,
            TakeoffYaw = _takeoffYaw,
            TakeoffSpeed = _takeoffSpeed,
            MoveState = _takeoffMoveState,
            StartX = _segmentStartPos.X,
            StartY = _segmentStartPos.Y,
            StartZ = _segmentStartPos.Z,
            StartYaw = _segmentStartYaw,
            StartSpeed = _segmentStartSpeed,
            Inputs = BuildInputs()
        };
        // 正常记录 = Append 到末尾；重录 = Insert 回原序号位置（只替换该段，后续段保留），并自动停止记录。
        // 插入新段 = Insert 到指定位置（基准段之后），后续段顺延，并自动停止记录。
        if (_rerecordIndex >= 0)
        {
            var idx = Math.Min(_rerecordIndex, _current.Segments.Count);
            _current.Segments.Insert(idx, segment);
            _rerecordIndex = -1;
            _pendingExtended = null;
            _recordingActive = false; // 重录完成：停止记录（后续跳跃不再采集，避免与保留的后续段混淆）
            PluginLog.Info($"重录完成：段 #{idx + 1} 已替换（新段 Insert 原序号，其余段保留），已停止记录");
            ChatInfo($"段 {idx + 1} 重录完成");
        }
        else if (_insertIndex >= 0)
        {
            var idx = Math.Min(_insertIndex, _current.Segments.Count);
            _current.Segments.Insert(idx, segment);
            _insertIndex = -1;
            _pendingExtended = null;
            _recordingActive = false; // 插入完成：停止记录（新段已补入，原该序号起顺延）
            PluginLog.Info($"插入完成：段 #{idx + 1} 已插入（原 #{idx + 1} 起顺延），已停止记录");
            ChatInfo($"段 {idx + 1} 插入完成");
        }
        else
        {
            _current.Segments.Add(segment);
        }
        segment.Extended = _lastSegmentExtended;
        var hDist = MathF.Sqrt((segment.LandX - segment.TakeoffX) * (segment.LandX - segment.TakeoffX)
                             + (segment.LandZ - segment.TakeoffZ) * (segment.LandZ - segment.TakeoffZ));
        PluginLog.Info($"采集段 #{_current.Segments.Count}: 起跳({segment.TakeoffX:F2},{segment.TakeoffY:F2},{segment.TakeoffZ:F2}) " +
                   $"→ 落地({segment.LandX:F2},{segment.LandY:F2},{segment.LandZ:F2}) " +
                   $"水平 {hDist:F2}m 高差 {segment.LandY - segment.TakeoffY:+0.00;-0.00}m" +
                   $"{(_lastSegmentExtended ? " [扩展时间线]" : "")}");
        // 每 X 跳自动保存（设置页 AutoSaveEvery；0 = 关闭）——防丢：录着录着崩溃/忘记保存也不丢
        var saveEvery = Service.Config.AutoSaveEvery;
        if (saveEvery > 0 && _current.Segments.Count % saveEvery == 0)
        {
            _store.Save(_current);
            Service.ChatGui.Print("已自动保存路线");
        }
        _pendingTakeoff = null;
        _flagLandPending = false;
        _lastLandAt = now;
        _lastLandPos = pos;
    }

    // ===== 冲刺起点回溯 =====

    /// <summary>
    /// 构建输入时间线：从段起始时刻（起跳前 InputLeadMs）到当前（落地帧）的完整输入序列，
    /// TimeMs 相对段起始。回放对齐段起点后逐帧重放——微调/助跑/跳跃/空中全部忠实复现。
    /// </summary>
    private List<InputFrame> BuildInputs()
    {
        var result = new List<InputFrame>();
        var start = _segmentInputStart;
        for (int i = 0; i < _inputHistory.Count; i++)
        {
            var t = _inputHistory[i].Time;
            if (t < start)
                continue;
            result.Add(new InputFrame
            {
                TimeMs = t - start,
                Left = _inputHistory[i].Left,
                Forward = _inputHistory[i].Fwd,
                Turn = _inputHistory[i].Turn,
                Jump = _inputHistory[i].Jump,
                Yaw = _inputHistory[i].Yaw
            });
        }
        return result;
    }

    /// <summary>从位置历史找指定时刻（不晚于）的玩家位置；找不到返回 null。</summary>
    private Vector3? FindPosAt(long time)
    {
        for (int i = _posHistory.Count - 1; i >= 0; i--)
        {
            if (_posHistory[i].Time <= time)
                return _posHistory[i].Pos;
        }
        return null;
    }

    /// <summary>从位置历史找指定时刻（不晚于）的玩家朝向；找不到返回 null。</summary>
    private float? FindYawAt(long time)
    {
        for (int i = _posHistory.Count - 1; i >= 0; i--)
        {
            if (_posHistory[i].Time <= time)
                return _posHistory[i].Yaw;
        }
        return null;
    }

    /// <summary>
    /// 指定时刻（不晚于）的水平速度（XZ，m/s）：取该时刻最近两帧的位移/时间。
    /// 用于时间线起点速度采集——回放预助跑到此速度再重放时间线（位置轨迹对齐）。
    /// </summary>
    private float CalcSpeedAt(long time)
    {
        if (_posHistory.Count < 2)
            return 0f;

        int idx = -1;
        for (int i = _posHistory.Count - 1; i >= 0; i--)
        {
            if (_posHistory[i].Time <= time)
            {
                idx = i;
                break;
            }
        }
        if (idx <= 0)
            return 0f;

        var dt = (_posHistory[idx].Time - _posHistory[idx - 1].Time) / 1000.0;
        if (dt <= 0)
            return 0f;

        var d = _posHistory[idx].Pos - _posHistory[idx - 1].Pos;
        d.Y = 0; // XZ 速度（空中帧 Y 变化不计入）
        return d.Length() / (float)dt;
    }

    /// <summary>
    /// 起跳瞬间水平速度（XZ，m/s）：取位置历史最后 N 帧的位移速度。
    /// 检测触发时玩家已在空中，而空中 XZ 速度保持 = 起跳水平速度（无空气阻力），
    /// 因此"最后几帧 XZ 位移 / 时间"即起跳瞬间速度——这是"跳距 = 速度 × 滞空"模型的关键数据。
    /// </summary>
    private float CalcTakeoffSpeed()
    {
        if (_posHistory.Count < 3)
            return 0f;

        var n = Math.Min(4, _posHistory.Count - 1);
        var a = _posHistory[_posHistory.Count - 1 - n].Pos;
        var b = _posHistory[_posHistory.Count - 1].Pos;
        var dt = (_posHistory[_posHistory.Count - 1].Time - _posHistory[_posHistory.Count - 1 - n].Time) / 1000.0;
        if (dt <= 0)
            return 0f;

        var d = b - a;
        d.Y = 0;
        return d.Length() / (float)dt;
    }

    /// <summary>
    /// 从位置历史回溯"助跑起点"（起跳前最后一段移动的起点）。
    /// 起跳速度由起跳瞬间的前进状态决定，因此助跑起点必须准：
    /// 策略（宁高估勿低估——高估跳过头有微调兜底，低估跳不够会掉下去）：
    /// 1) 先找"最后一段水平移动"（XZ 速度 &gt;0.5）的起点——不跨静止段（落地停顿会切断移动段）；
    /// 2) 在该移动段内找最后一个方向突变点（转向结束 = 冲刺起点，排除走位微调）；
    /// 3) 全程低速 = 原地跳，返回起跳点。
    /// </summary>
    private Vector3 FindSprintStart()
    {
        var takeoff = _pendingTakeoff!.Value;
        if (_posHistory.Count < 4)
            return takeoff;

        // 1) 最后一段"水平移动"的起点（从最新往前，遇低速/静止帧即止）
        var moveStartIdx = -1;
        for (int i = _posHistory.Count - 2; i >= 1; i--)
        {
            var dt = (_posHistory[i + 1].Time - _posHistory[i].Time) / 1000.0;
            if (dt <= 0)
                continue;
            var d = _posHistory[i + 1].Pos - _posHistory[i].Pos;
            d.Y = 0; // XZ 速度（空中帧 Y 变化不计入）
            var v = d.Length() / dt;
            if (v > MoveSpeed)
                moveStartIdx = i;
            else if (moveStartIdx >= 0)
                break; // 已回溯出移动段
        }
        if (moveStartIdx < 0)
            return takeoff; // 全程低速 = 原地跳

        // 2) 该移动段内最后一个方向突变点（转向结束 = 冲刺起点）
        var end = _posHistory.Count - 2;
        for (int i = end; i >= moveStartIdx + 1 && i >= 1; i--)
        {
            var a = _posHistory[i].Pos - _posHistory[i - 1].Pos;
            var b = _posHistory[i + 1].Pos - _posHistory[i].Pos;
            a.Y = 0;
            b.Y = 0;
            var lenA = a.Length();
            var lenB = b.Length();
            if (lenB < 0.03f)
                continue;
            if (lenA < 0.03f)
                return _posHistory[i].Pos; // 前段停顿 → 从这里开始加速
            var dot = Vector3.Dot(a / lenA, b / lenB);
            if (MathF.Acos(Math.Clamp(dot, -1f, 1f)) > 25f * MathF.PI / 180f)
                return _posHistory[i].Pos; // 方向突变 → 冲刺起点
        }

        return _posHistory[moveStartIdx].Pos;
    }

    public void Dispose()
    {
        Service.Framework.Update -= OnUpdate;
    }
}
