using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ReplayKeeper
{
    // This is an internal NVIDIA state value, not a supported public API.
    // Source: https://github.com/Verpous/AlwaysShadow/blob/master/src/fixer.c
    public sealed class NvidiaPlatform : IReplayPlatform
    {
        public const string RegistryPath = @"Software\NVIDIA Corporation\Global\ShadowPlay\NVSPCAPS";
        public const string ReplayValue = "{1B1D3DAA-601D-49E5-8508-81736CA28C6D}";
        private readonly string registryPath;
        private readonly Func<string> findOverlay;
        public Func<bool> CanEnable = delegate { return true; };

        public NvidiaPlatform() : this(RegistryPath, FindOverlay) { }

        // Allows integration tests against an isolated test registry, never NVIDIA's settings.
        public NvidiaPlatform(string registryPath, Func<string> findOverlay)
        {
            this.registryPath = registryPath;
            this.findOverlay = findOverlay;
        }

        public ReplaySnapshot Read()
        {
            var snapshot = new ReplaySnapshot();
            try
            {
                snapshot.OverlayIdentity = findOverlay() ?? "";
                snapshot.OverlayReady = snapshot.OverlayIdentity.Length > 0;
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(registryPath, false))
                {
                    if (key == null)
                    {
                        snapshot.Detail = "未找到 NVIDIA 录制配置，请安装 NVIDIA App 并开启叠加层";
                        return snapshot;
                    }
                    uint? state = ReadNumber(key.GetValue(ReplayValue));
                    snapshot.State = state == 1 ? ReplayState.On : state == 0 ? ReplayState.Off : ReplayState.Unknown;
                    int[] hotkey = ReadHotkey(key);
                    snapshot.HotkeyDisplay = hotkey == null ? "未配置" : FormatHotkey(hotkey);
                    if (!snapshot.OverlayReady)
                    {
                        snapshot.State = ReplayState.Unknown;
                        snapshot.Detail = "等待 NVIDIA 叠加层进程；请在 NVIDIA App 中启用叠加层";
                    }
                    else if (snapshot.State == ReplayState.Unknown)
                        snapshot.Detail = "无法确认即时回放状态，暂不发送快捷键";
                    else if (hotkey == null)
                        snapshot.Detail = "即时回放快捷键未配置或格式不支持，请在 NVIDIA 叠加层中设置";
                    else
                        snapshot.Detail = snapshot.State == ReplayState.On ? "即时回放开关已开启" : "即时回放开关已关闭";
                }
            }
            catch (Exception ex)
            {
                snapshot.State = ReplayState.Unknown;
                snapshot.Detail = "读取 NVIDIA 状态失败：" + ex.Message;
            }
            return snapshot;
        }

        public EnableResult TryEnable(out string detail)
        {
            // Recheck actual state immediately before pressing a *toggle* shortcut.
            ReplaySnapshot current = Read();
            if (!current.OverlayReady || current.State != ReplayState.Off)
            {
                detail = "恢复前复查：" + current.Detail;
                return EnableResult.Deferred;
            }
            IntPtr desktop = OpenInputDesktop(0, false, 0x0100); // DESKTOP_SWITCHDESKTOP
            if (desktop == IntPtr.Zero)
            {
                detail = "当前桌面无法接收输入，等待解锁或退出安全桌面";
                return EnableResult.Deferred;
            }
            bool interactive;
            try { interactive = SwitchDesktop(desktop); }
            finally { CloseDesktop(desktop); }
            if (!interactive)
            {
                detail = "等待交互桌面就绪";
                return EnableResult.Deferred;
            }

            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(registryPath, false))
            {
                int[] hotkey = key == null ? null : ReadHotkey(key);
                if (hotkey == null)
                {
                    detail = "快捷键未配置或格式不支持，未发送按键";
                    return EnableResult.Deferred;
                }
                // Do not combine injected keys with the user's current keyboard input.
                for (int vk = 1; vk < 256; vk++)
                {
                    if ((GetAsyncKeyState(vk) & 0x8000) != 0)
                    {
                        detail = "等待键盘和鼠标空闲";
                        return EnableResult.Deferred;
                    }
                }
                if (ReadNumber(key.GetValue(ReplayValue)) != 0 || findOverlay() != current.OverlayIdentity)
                {
                    detail = "恢复前状态或叠加层发生变化，重新检测";
                    return EnableResult.Deferred;
                }
                INPUT[] input = new INPUT[hotkey.Length * 2];
                for (int i = 0; i < hotkey.Length; i++)
                {
                    input[i] = KeyInput(hotkey[i], false);
                    input[hotkey.Length + i] = KeyInput(hotkey[hotkey.Length - 1 - i], true);
                }
                if (!CanEnable())
                {
                    detail = "游戏已退出或无法确认游戏运行，取消恢复";
                    return EnableResult.Deferred;
                }
                uint sent = SendInput((uint)input.Length, input, Marshal.SizeOf(typeof(INPUT)));
                if (sent != input.Length)
                {
                    int error = Marshal.GetLastWin32Error();
                    // Release any keys we may have pressed if Windows accepted only a prefix.
                    if (sent > 0)
                    {
                        INPUT[] release = hotkey.Reverse().Select(vk => KeyInput(vk, true)).ToArray();
                        SendInput((uint)release.Length, release, Marshal.SizeOf(typeof(INPUT)));
                    }
                    detail = "Windows 未完整接收快捷键（" + sent + "/" + input.Length + "，错误 " + error + "），将复查后重试";
                    return EnableResult.Failed;
                }
                detail = "已发送 " + FormatHotkey(hotkey) + "，等待 NVIDIA 状态确认";
                return EnableResult.Sent;
            }
        }

        public static uint? ReadNumber(object value)
        {
            byte[] binary = value as byte[];
            if (binary != null) return binary.Length == 4 ? (uint?)BitConverter.ToUInt32(binary, 0) : null;
            if (value is int) return unchecked((uint)(int)value);
            return null;
        }

        public static int[] ReadHotkey(RegistryKey key)
        {
            uint? count = ReadNumber(key.GetValue("IRToggleHKeyCount"));
            if (!count.HasValue || count.Value < 1 || count.Value > 4) return null;
            List<int> keys = new List<int>();
            for (int i = 0; i < (int)count.Value; i++)
            {
                uint? value = ReadNumber(key.GetValue("IRToggleHKey" + i));
                if (!value.HasValue || value.Value < 8 || value.Value > 254 || value.Value == 91 || value.Value == 92)
                    return null;
                int vk = (int)value.Value;
                if (keys.Contains(vk)) return null;
                keys.Add(vk);
            }
            // NVIDIA shortcuts have zero or more modifiers followed by one main key.
            if (keys.Take(keys.Count - 1).Any(vk => !IsModifier(vk)) || IsModifier(keys.Last())) return null;
            return keys.ToArray();
        }

        private static bool IsModifier(int key)
        {
            return key == 16 || key == 17 || key == 18 || (key >= 160 && key <= 165);
        }

        public static string FormatHotkey(int[] hotkey)
        {
            return string.Join(" + ", hotkey.Select(key => key == 16 ? "Shift" : key == 17 ? "Ctrl" : key == 18 ? "Alt" : ((Keys)key).ToString()).ToArray());
        }

        private static string FindOverlay()
        {
            int session = Process.GetCurrentProcess().SessionId;
            List<string> identities = new List<string>();
            foreach (string name in new[] { "NVIDIA Overlay", "NVIDIA Share" })
            {
                foreach (Process process in Process.GetProcessesByName(name))
                {
                    using (process)
                    {
                        try
                        {
                            if (process.SessionId == session)
                                identities.Add(process.Id + ":" + process.StartTime.ToUniversalTime().Ticks);
                        }
                        catch (InvalidOperationException) { }
                        catch (System.ComponentModel.Win32Exception) { }
                    }
                }
            }
            identities.Sort(StringComparer.Ordinal);
            return string.Join(";", identities.ToArray());
        }

        private static INPUT KeyInput(int key, bool up)
        {
            bool extended = key == 163 || key == 165 || (key >= 33 && key <= 46) || key == 111 || key == 144;
            return new INPUT { type = 1, data = new InputUnion { keyboard = new KEYBDINPUT {
                wVk = (ushort)key, dwFlags = (up ? 2u : 0u) | (extended ? 1u : 0u) } } };
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT { public uint type; public InputUnion data; }
        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public KEYBDINPUT keyboard;
            [FieldOffset(0)] public MOUSEINPUT mouse;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk, wScan;
            public uint dwFlags, time;
            public UIntPtr dwExtraInfo;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx, dy;
            public uint mouseData, dwFlags, time;
            public UIntPtr dwExtraInfo;
        }
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint count, INPUT[] input, int size);
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int virtualKey);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
        [DllImport("user32.dll")]
        private static extern bool SwitchDesktop(IntPtr desktop);
        [DllImport("user32.dll")]
        private static extern bool CloseDesktop(IntPtr desktop);
    }
}
