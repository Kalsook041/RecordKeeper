using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace ReplayKeeper
{
    public sealed class GameActivity
    {
        public bool Running;
        public string Name = "";
        public string Detail = "未发现游戏进程，等待游戏启动";
    }

    // Exact executable paths avoid confusing launchers or unrelated names with a game.
    public sealed class GameCatalog
    {
        private readonly HashSet<string> paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public GameCatalog(IEnumerable<string> automatic, IEnumerable<string> configured)
        {
            foreach (string path in automatic)
            {
                string normalized = Normalize(path);
                if (normalized == null) continue;
                string name = Path.GetFileNameWithoutExtension(normalized).ToLowerInvariant();
                // Windows can also register clients and generic runtimes as games.
                if (name == "java" || name == "javaw" || name == "chrome" || name == "msedge" ||
                    name == "firefox" || name == "steam" || name == "leagueclientux" ||
                    name.IndexOf("launcher", StringComparison.Ordinal) >= 0 ||
                    name.IndexOf("crash", StringComparison.Ordinal) >= 0 ||
                    name.IndexOf("竞技平台", StringComparison.Ordinal) >= 0) continue;
                paths.Add(normalized);
            }
            foreach (string line in configured)
            {
                string value = line.Trim();
                if (value.Length == 0 || value.StartsWith("#", StringComparison.Ordinal)) continue;
                bool exclude = value.StartsWith("!", StringComparison.Ordinal);
                string normalized = Normalize(exclude ? value.Substring(1).Trim() : value);
                if (normalized == null) throw new FormatException("游戏列表需要填写完整的 .exe 路径：" + value);
                if (exclude) excluded.Add(normalized); else paths.Add(normalized);
            }
            paths.ExceptWith(excluded);
            foreach (string path in paths) names.Add(Path.GetFileNameWithoutExtension(path));
        }

        public bool MightMatch(string processName) { return names.Contains(processName); }
        public bool Matches(string executable)
        {
            string normalized = Normalize(executable);
            return normalized != null && paths.Contains(normalized);
        }

        private static string Normalize(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) return null;
            path = path.Trim().Trim('"');
            bool drivePath = path.Length >= 3 && Char.IsLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/');
            if (!drivePath && !path.StartsWith(@"\\", StringComparison.Ordinal)) return null;
            try
            {
                if (!String.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase)) return null;
                return Path.GetFullPath(path);
            }
            catch (ArgumentException) { return null; }
            catch (NotSupportedException) { return null; }
            catch (PathTooLongException) { return null; }
        }
    }

    public sealed class GameActivityDetector
    {
        private GameCatalog catalog;
        private long refreshAfter;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly int sessionId;
        private readonly string registryPath;
        private readonly string configurationPath;
        public static string ConfigurationPath { get { return Path.Combine(AppFiles.DataDirectory, "games.txt"); } }

        public GameActivityDetector() : this(@"System\GameConfigStore\Children", ConfigurationPath) { }

        // Integration tests use isolated registry keys and a temporary configuration file.
        public GameActivityDetector(string registryPath, string configurationPath)
        {
            this.registryPath = registryPath;
            this.configurationPath = configurationPath;
            using (Process self = Process.GetCurrentProcess()) sessionId = self.SessionId;
        }

        public void Refresh() { catalog = null; }

        public GameActivity Read()
        {
            try
            {
                if (catalog == null || clock.ElapsedMilliseconds >= refreshAfter)
                {
                    // Do not use a stale catalog if a configuration reload fails.
                    catalog = null;
                    List<string> automatic = new List<string>();
                    using (RegistryKey root = Registry.CurrentUser.OpenSubKey(registryPath, false))
                    {
                        if (root != null)
                            foreach (string name in root.GetSubKeyNames())
                                using (RegistryKey game = root.OpenSubKey(name, false))
                                    if (game != null) automatic.Add(game.GetValue("MatchedExeFullPath") as string);
                    }
                    catalog = new GameCatalog(automatic, File.Exists(configurationPath) ?
                        File.ReadAllLines(configurationPath, Encoding.UTF8) : new string[0]);
                    refreshAfter = clock.ElapsedMilliseconds + 15000;
                }
                GameActivity result = new GameActivity();
                foreach (Process process in Process.GetProcesses())
                {
                    using (process)
                    {
                        try
                        {
                            if (result.Running || !catalog.MightMatch(process.ProcessName) || process.SessionId != sessionId) continue;
                            string path = ImagePath(process.Id);
                            if (!catalog.Matches(path)) continue;
                            result.Running = true;
                            result.Name = Path.GetFileNameWithoutExtension(path);
                            result.Detail = "正在运行游戏：" + result.Name;
                        }
                        catch (InvalidOperationException) { } // Process exited during enumeration.
                        catch (System.ComponentModel.Win32Exception) { } // Inaccessible process: never assume a game.
                    }
                }
                return result;
            }
            catch (Exception error)
            {
                return new GameActivity { Detail = "游戏识别不可用，暂停检测：" + error.Message };
            }
        }

        private static string ImagePath(int processId)
        {
            IntPtr handle = OpenProcess(0x1000, false, processId); // PROCESS_QUERY_LIMITED_INFORMATION
            if (handle == IntPtr.Zero) return null;
            try
            {
                StringBuilder buffer = new StringBuilder(32768);
                int length = buffer.Capacity;
                return QueryFullProcessImageName(handle, 0, buffer, ref length) ? buffer.ToString() : null;
            }
            finally { CloseHandle(handle); }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref int size);
        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
