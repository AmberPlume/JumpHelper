using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Plugin.Services;

namespace JumpHelper.Services;

/// <summary>
/// 跳跃执行：直接写游戏键盘状态缓冲模拟空格键按下。
///
/// 背景：Dalamud 的 IKeyState 官方明确不支持"按下按键"（SetRawValue 非零即抛异常，
/// 只能清除/阻止），因此走社区验证过的底层通道——通过
/// ClientStateAddressResolver.KeyboardState / KeyboardStateIndexArray 定位游戏键盘状态缓冲，
/// 写入组合标志位（bit0=pressed 按住, bit1=down 按下事件, bit2=up 释放事件）。
///
/// FF14 跳跃语义：起跳由"按下事件"（down 位）触发，按住不会连续跳；
/// 因此注入一次 down+held 后按帧清除即可。
/// </summary>
public unsafe sealed class JumpExecutor : IDisposable
{
    /// <summary>注入保持时长（毫秒）：保证游戏至少消费一帧的 down 边沿。</summary>
    private const long JumpHeldMs = 120;

    /// <summary>键盘状态缓冲基址（每个键 4 字节 int 组合标志）。</summary>
    private readonly IntPtr _bufferBase;

    /// <summary>键码转换数组基址（VirtualKey → 游戏索引）。</summary>
    private readonly IntPtr _indexBase;

    private readonly IPluginLog _log;

    private long _releaseAt;
    private bool _jumpActive;
    private int _currentKeyIndex;

    public JumpExecutor()
    {
        _log = Service.Log;

        var sig = Service.SigScanner;
        var moduleBase = sig.Module.BaseAddress;

        // 参考国际服 ClientStateAddressResolver 的签名：
        //   lea   rcx, ds:offset[rax*4]            → 键盘状态缓冲偏移
        //   movzx edx, byte ptr [rbx+rsi+offset]   → 键码转换数组偏移
        var keyboardStateAddr = sig.ScanText("48 8D 0C 85 ?? ?? ?? ?? 8B 04 31 85 C2 0F 85") + 0x4;
        var indexArrayAddr = sig.ScanText("0F B6 94 33 ?? ?? ?? ?? 84 D2") + 0x4;

        _bufferBase = moduleBase + Marshal.ReadInt32(keyboardStateAddr);
        _indexBase = moduleBase + Marshal.ReadInt32(indexArrayAddr);
        PluginLog.Info($"JumpExecutor: buffer=0x{_bufferBase:X} index=0x{_indexBase:X}");

        Service.Framework.Update += OnFrameworkUpdate;
    }

    /// <summary>触发一次起跳：注入空格"按下事件 + 按住"，短时后自动清除。</summary>
    public void Jump()
    {
        var gameIndex = ResolveGameKeyIndex(VirtualKey.SPACE);
        if (gameIndex == 0)
        {
            PluginLog.Info("JumpExecutor: 空格键映射为无效游戏键码，起跳失败");
            return;
        }

        *(int*)(_bufferBase + 4 * gameIndex) = 0b011; // pressed + down
        _currentKeyIndex = gameIndex;
        _releaseAt = Environment.TickCount64 + JumpHeldMs;
        _jumpActive = true;
        PluginLog.Info($"JumpExecutor: 注入起跳 (vk=0x{(int)VirtualKey.SPACE:X}, gameIdx={gameIndex})");
    }

    /// <summary>立即清除全部注入的键盘状态（紧急停止）。</summary>
    public void Stop()
    {
        if (_jumpActive)
            *(int*)(_bufferBase + 4 * _currentKeyIndex) = 0;
        _jumpActive = false;
    }

    /// <summary>
    /// 持续按住/松开空格（全量输入时间线回放用）。
    /// 写 0b011（pressed+down）：down=按下事件触发起跳（FF14 由按下边沿起跳）；
    /// 游戏对持续 down 去重（Jump() 保持 0b011 120ms 实测只跳一次），按住不重复跳。
    /// 不自动释放，须显式 SetHeld(false) 或 Stop()。
    /// </summary>
    public void SetHeld(bool held)
    {
        var gameIndex = ResolveGameKeyIndex(VirtualKey.SPACE);
        if (gameIndex == 0)
            return;

        if (held)
        {
            *(int*)(_bufferBase + 4 * gameIndex) = 0b011; // pressed + down（起跳边沿）
            _currentKeyIndex = gameIndex;
            _jumpActive = true;
            _releaseAt = long.MaxValue; // 不自动清除，由 SetHeld(false)/Stop() 释放
        }
        else
        {
            *(int*)(_bufferBase + 4 * gameIndex) = 0;
            _jumpActive = false;
        }
    }

    /// <summary>当前空格是否按住（bit0；录制时插件不注入 = 玩家真实状态）。</summary>
    public bool IsJumpHeld()
    {
        var gameIndex = ResolveGameKeyIndex(VirtualKey.SPACE);
        return gameIndex != 0 && (*(int*)(_bufferBase + 4 * gameIndex) & 0b001) != 0;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (_jumpActive && Environment.TickCount64 >= _releaseAt)
            Stop();
    }

    /// <summary>VirtualKey → 游戏键盘状态数组索引；0 表示无效。</summary>
    private int ResolveGameKeyIndex(VirtualKey vk)
    {
        var vkCode = (int)vk;
        if (vkCode <= 0 || vkCode >= 0xF0)
            return 0;

        return *(byte*)(_indexBase + vkCode);
    }

    public void Dispose()
    {
        Stop();
        Service.Framework.Update -= OnFrameworkUpdate;
    }
}
