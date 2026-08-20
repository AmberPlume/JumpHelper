using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using JumpHelper.Services;
using JumpHelper.UI;

namespace JumpHelper;

/// <summary>
/// 跳跳乐助手（内部名称 JumpHelper）：录制玩家真实跳跃（全量输入时间线）→ 读档逐段回放。
/// 日常操作全在悬浮窗（记录/保存/路线列表/段落编辑/路线回放）；
/// 设置窗口为参数/开关配置。命令仅保留 /jh on | off（控制悬浮窗显示）。
/// </summary>
public sealed class JumpHelper : IDalamudPlugin
{
    private const string MainCommand = "/jh";

    private readonly WindowSystem _windowSystem = new("JumpHelper");
    private readonly MainWindow _mainWindow;
    private readonly FloatingPanel _floatingPanel;
    private readonly RouteStore _routeStore;
    private readonly RecorderService _recorderService;
    private readonly MovementController _movementController;
    private readonly JumpExecutor _jumpExecutor;
    private readonly ReplayEngine _replayEngine;
    private readonly RouteOverlay _routeOverlay;

    public JumpHelper(IDalamudPluginInterface dalamud)
    {
        if (!dalamud.ConfigDirectory.Exists)
            dalamud.ConfigDirectory.Create();

        dalamud.Create<Service>();
        PluginLog.Init(dalamud.ConfigDirectory.FullName);
        Service.Log.Error("JumpHelper 构造开始");

        try
        {
            // 加载配置（参数由 UI「参数设置」页调整，Save 即时生效）
            Service.Config = dalamud.GetPluginConfig() as Configuration ?? new Configuration();
            MigrateConfig();

            _routeStore = new RouteStore();
            _movementController = new MovementController();
            _jumpExecutor = new JumpExecutor();
            _replayEngine = new ReplayEngine(_movementController, _jumpExecutor);
            _recorderService = new RecorderService(_routeStore, _movementController, _jumpExecutor, _replayEngine);
            _routeOverlay = new RouteOverlay(_replayEngine, _recorderService);
            _mainWindow = new MainWindow(_routeStore);
            _floatingPanel = new FloatingPanel(_routeStore, _recorderService, _replayEngine,
                                               _movementController, _jumpExecutor, _routeOverlay, _mainWindow);
            _mainWindow.OnFloatingPanelToggle = v => _floatingPanel.IsOpen = v;

            _windowSystem.AddWindow(_mainWindow);
            _windowSystem.AddWindow(_floatingPanel);

            Service.PluginInterface.UiBuilder.Draw += Draw;
            Service.PluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;
            Service.CommandManager.AddHandler(MainCommand, new CommandInfo(OnMainCommand)
            {
                HelpMessage = "跳跳乐助手：/jh on 显示悬浮窗；/jh off 隐藏悬浮窗；/jh 打开设置",
                ShowInHelp = true
            });

            Service.Log.Error("JumpHelper 加载成功");
        }
        catch (Exception ex)
        {
            Service.Log.Error(ex, "JumpHelper 加载失败");
            throw;
        }
    }

    private void Draw()
    {
        _windowSystem.Draw();
    }

    /// <summary>
    /// 配置迁移：v0 → v1（游戏位置精度实测为 0.001m，对齐/起跳/朝向判定容差从 0.01 收紧到 0.001）。
    /// 仅当值仍为旧默认值（0.01）时更新——玩家手动调过的值保留不动；Y 轴容差 0.3 是平台判定
    /// （路不平地图用户主动放宽），不参与迁移。
    /// </summary>
    private static void MigrateConfig()
    {
        if (Service.Config.Version >= 1)
            return;
        if (MathF.Abs(Service.Config.AlignTolerance - 0.01f) < 0.0001f)
            Service.Config.AlignTolerance = 0.001f;
        if (MathF.Abs(Service.Config.TakeoffAlongTolerance - 0.01f) < 0.0001f)
            Service.Config.TakeoffAlongTolerance = 0.001f;
        if (MathF.Abs(Service.Config.TakeoffLateralTolerance - 0.01f) < 0.0001f)
            Service.Config.TakeoffLateralTolerance = 0.001f;
        if (MathF.Abs(Service.Config.FacingToleranceRad - 0.01f) < 0.0001f)
            Service.Config.FacingToleranceRad = 0.001f;
        Service.Config.Version = 1;
        Service.Config.Save();
        Service.Log.Error($"配置迁移 v0→v1：对齐/起跳/朝向容差收紧到 0.001（游戏位置精度实测 0.001m）");
    }

    private void OpenConfigUi()
    {
        _mainWindow.Toggle();
    }

    /// <summary>命令：/jh on 显示悬浮窗；/jh off 隐藏悬浮窗。仅此两个子命令。</summary>
    private void OnMainCommand(string command, string args)
    {
        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        switch (parts.Length == 0 ? "" : parts[0].ToLowerInvariant())
        {
            case "on":
                Service.Config.ShowFloatingPanel = true;
                Service.Config.Save();
                _floatingPanel.IsOpen = true;
                Service.ChatGui.Print("悬浮窗已显示");
                break;

            case "off":
                Service.Config.ShowFloatingPanel = false;
                Service.Config.Save();
                _floatingPanel.IsOpen = false;
                Service.ChatGui.Print("悬浮窗已隐藏");
                break;

            default:
                Service.ChatGui.Print("用法: /jh on（显示悬浮窗）| /jh off（隐藏悬浮窗）");
                break;
        }
    }

    public void Dispose()
    {
        _recorderService.Dispose(); // 取消 Framework.Update 订阅（防热重载事件泄漏/重复注册）
        _replayEngine.Dispose();
        _jumpExecutor.Dispose();
        _movementController.Dispose();
        _routeOverlay.Dispose();
        Service.CommandManager.RemoveHandler(MainCommand);
        Service.PluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUi;
        Service.PluginInterface.UiBuilder.Draw -= Draw;
        _windowSystem.RemoveAllWindows();
    }
}
