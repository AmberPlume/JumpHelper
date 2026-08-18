using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace JumpHelper;

/// <summary>
/// CN 版 Dalamud 15 服务定位（卫月样板：由 Plugin 构造时 Create&lt;Service&gt;() 注入）。
/// </summary>
public class Service
{
    [PluginService]
    public static IPluginLog Log { get; private set; } = null!;

    [PluginService]
    public static ICommandManager CommandManager { get; private set; } = null!;

    [PluginService]
    public static IClientState ClientState { get; private set; } = null!;

    [PluginService]
    public static IGameGui GameGui { get; private set; } = null!;

    [PluginService]
    public static IGameConfig GameConfig { get; private set; } = null!;

    [PluginService]
    public static IChatGui ChatGui { get; private set; } = null!;

    [PluginService]
    public static IKeyState KeyState { get; private set; } = null!;

    [PluginService]
    public static IFramework Framework { get; private set; } = null!;

    [PluginService]
    public static IGameInteropProvider Hook { get; private set; } = null!;

    [PluginService]
    public static IObjectTable ObjectTable { get; private set; } = null!;

    [PluginService]
    public static ICondition Condition { get; private set; } = null!;

    [PluginService]
    public static ISigScanner SigScanner { get; private set; } = null!;

    [PluginService]
    public static IDataManager DataManager { get; private set; } = null!;

    [PluginService]
    public static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    /// <summary>插件配置（JumpHelper 构造时从卫月加载；UI 调整后 Save 即时生效）。</summary>
    public static Configuration Config { get; set; } = new();
}
