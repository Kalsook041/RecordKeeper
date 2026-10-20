using System;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ReplayKeeper
{
    public static class InstallationPaths
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint size, uint flags);

        public static string Resolve(string path)
        {
            using (FileStream file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                StringBuilder result = new StringBuilder(32768);
                uint length = GetFinalPathNameByHandle(file.SafeFileHandle, result, (uint)result.Capacity, 0);
                if (length == 0 || length >= result.Capacity) throw new IOException("Cannot resolve installed executable path.");
                string full = result.ToString();
                if (full.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return @"\\" + full.Substring(8);
                return full.StartsWith(@"\\?\", StringComparison.Ordinal) ? full.Substring(4) : full;
            }
        }

        public static bool SameFile(string first, string second)
        {
            if (String.IsNullOrWhiteSpace(first) || String.IsNullOrWhiteSpace(second)) return false;
            if (String.Equals(first, second, StringComparison.OrdinalIgnoreCase)) return true;
            return File.Exists(first) && File.Exists(second) && String.Equals(Resolve(first), Resolve(second), StringComparison.OrdinalIgnoreCase);
        }
    }
}
