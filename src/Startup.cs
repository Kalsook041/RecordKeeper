using System;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ReplayKeeper
{
    public static class Startup
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "ReplayKeeper";

        public static bool IsEnabled()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, false))
            {
                if (key == null)
                    return false;
                string actual = key.GetValue(ValueName) as string;
                return String.Equals(actual, QuotedExecutablePath(), StringComparison.OrdinalIgnoreCase);
            }
        }

        public static void SetEnabled(bool value)
        {
            if (value)
            {
                string command = QuotedExecutablePath();
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (key == null)
                        throw new InvalidOperationException("无法打开当前用户的开机启动注册表项。");
                    key.SetValue(ValueName, command, RegistryValueKind.String);
                }
            }
            else
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (key != null)
                        key.DeleteValue(ValueName, false);
                }
            }
        }

        private static string QuotedExecutablePath()
        {
            string path = Path.GetFullPath(Application.ExecutablePath);
            if (!Path.IsPathRooted(path) || !File.Exists(path) ||
                !String.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase) ||
                path.IndexOf('"') >= 0)
                throw new InvalidOperationException("当前程序路径无效，无法设置开机自启动。");
            return "\"" + path + "\"";
        }
    }
}
