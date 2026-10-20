using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ReplayKeeper
{
    public static class Startup
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "ReplayKeeper";

        public static string ShortcutPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "ReplayKeeper.lnk"); }
        }

        public static bool IsEnabled()
        {
            return IsOwnShortcut(ShortcutPath);
        }

        public static void SetEnabled(bool value)
        {
            if (value)
            {
                string executable = Path.GetFullPath(Application.ExecutablePath);
                QuotedExecutablePath(); // Validate before changing startup configuration.
                if (File.Exists(ShortcutPath) && !IsOwnShortcut(ShortcutPath))
                    throw new InvalidOperationException("启动文件夹存在指向其他程序的 ReplayKeeper.lnk，未覆盖。");
                Directory.CreateDirectory(Path.GetDirectoryName(ShortcutPath));
                string staged = Path.Combine(Path.GetDirectoryName(ShortcutPath), "ReplayKeeper." + Guid.NewGuid().ToString("N") + ".lnk");
                try
                {
                    WithShortcut(staged, delegate(object shortcut)
                    {
                        Set(shortcut, "TargetPath", executable);
                        Set(shortcut, "WorkingDirectory", Path.GetDirectoryName(executable));
                        Set(shortcut, "Arguments", String.Empty);
                        Set(shortcut, "Description", "ReplayKeeper 即时回放守护");
                        shortcut.GetType().InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
                    });
                    if (!IsOwnShortcut(staged))
                        throw new InvalidOperationException("启动快捷方式验证失败。");
                    if (File.Exists(ShortcutPath)) File.Replace(staged, ShortcutPath, null);
                    else File.Move(staged, ShortcutPath);
                }
                finally { if (File.Exists(staged)) File.Delete(staged); }
            }
            else
            {
                if (IsOwnShortcut(ShortcutPath)) File.Delete(ShortcutPath);
            }
            // Migrate the old Run entry, without removing another installation's entry.
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                string legacy = key == null ? null : key.GetValue(ValueName) as string;
                if (legacy != null && InstallationPaths.SameFile(legacy.Trim('"'), Path.GetFullPath(Application.ExecutablePath)))
                    key.DeleteValue(ValueName, false);
            }
        }

        private static bool IsOwnShortcut(string path)
        {
            if (!File.Exists(path)) return false;
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("拒绝操作重解析启动快捷方式。");
            bool matches = false;
            WithShortcut(path, delegate(object shortcut)
            {
                string target = (string)shortcut.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, shortcut, null);
                string arguments = (string)shortcut.GetType().InvokeMember("Arguments", BindingFlags.GetProperty, null, shortcut, null);
                matches = InstallationPaths.SameFile(target, Path.GetFullPath(Application.ExecutablePath))
                    && String.IsNullOrEmpty(arguments);
            });
            return matches;
        }

        private static void Set(object shortcut, string name, object value)
        {
            shortcut.GetType().InvokeMember(name, BindingFlags.SetProperty, null, shortcut, new object[] { value });
        }

        private static void WithShortcut(string path, Action<object> action)
        {
            object shell = null;
            object shortcut = null;
            try
            {
                shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true));
                shortcut = shell.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
                action(shortcut);
            }
            finally
            {
                if (shortcut != null) Marshal.FinalReleaseComObject(shortcut);
                if (shell != null) Marshal.FinalReleaseComObject(shell);
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
