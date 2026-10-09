using System;
using System.Diagnostics;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;

namespace ReplayKeeper
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (args.Length > 1)
                return CommandError("只支持一个命令行参数。");

            string command = args.Length == 0 ? String.Empty : args[0].ToLowerInvariant();
            try
            {
                if (command == "--diagnose")
                    return Diagnose();
                if (command == "--enable-startup")
                {
                    Startup.SetEnabled(true);
                    return 0;
                }
                if (command == "--disable-startup")
                {
                    Startup.SetEnabled(false);
                    return 0;
                }
                if (command != String.Empty && command != "--exit")
                    return CommandError("不支持的命令行参数: " + args[0]);

                string sid = WindowsIdentity.GetCurrent().User.Value;
                string prefix = @"Local\ReplayKeeper." + sid;
                if (command == "--exit")
                    return SignalExit(prefix + ".Exit");

                bool ownsMutex;
                using (Mutex mutex = new Mutex(true, prefix + ".Instance", out ownsMutex))
                {
                    if (!ownsMutex)
                        return 0;
                    try
                    {
                        using (EventWaitHandle exitEvent = new EventWaitHandle(
                            false, EventResetMode.ManualReset, prefix + ".Exit"))
                        {
                            Application.ThreadException += OnThreadException;
                            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
                            Application.Run(new TrayApplication(exitEvent));
                        }
                    }
                    finally { mutex.ReleaseMutex(); }
                }
                return 0;
            }
            catch (Exception error)
            {
                LogFailure("程序启动或命令执行失败", error);
                if (command == String.Empty)
                    MessageBox.Show("ReplayKeeper 无法启动。请查看日志文件夹中的错误记录。\n\n" +
                        error.Message, "ReplayKeeper", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        private static int Diagnose()
        {
            try
            {
                ReplaySnapshot snapshot = new NvidiaPlatform().Read();
                AppFiles.WriteJson("diagnostics.json", new
                {
                    CheckedAt = DateTime.Now.ToString("o"),
                    Success = true,
                    Snapshot = snapshot
                });
                return 0;
            }
            catch (Exception error)
            {
                LogFailure("只读诊断失败", error);
                try
                {
                    AppFiles.WriteJson("diagnostics.json", new
                    {
                        CheckedAt = DateTime.Now.ToString("o"),
                        Success = false,
                        Error = error.Message
                    });
                }
                catch (Exception writeError)
                {
                    LogFailure("无法保存诊断结果", writeError);
                }
                return 1;
            }
        }

        private static int SignalExit(string eventName)
        {
            Stopwatch timeout = Stopwatch.StartNew();
            while (true)
            {
                try
                {
                    using (EventWaitHandle exitEvent = EventWaitHandle.OpenExisting(eventName))
                        exitEvent.Set();
                    return 0;
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                    // A newly launched guard may still be loading the CLR before it creates its event.
                    if (!HasStartingPeer()) return 0;
                    if (timeout.ElapsedMilliseconds >= 5000)
                    {
                        LogFailure("守护实例尚未建立退出通道，请稍后重试", null);
                        return 1;
                    }
                    Thread.Sleep(50);
                }
            }
        }

        private static bool HasStartingPeer()
        {
            using (Process current = Process.GetCurrentProcess())
            {
                foreach (Process peer in Process.GetProcessesByName(current.ProcessName))
                {
                    using (peer)
                    {
                        try
                        {
                            if (peer.Id != current.Id && peer.SessionId == current.SessionId) return true;
                        }
                        catch (InvalidOperationException) { }
                    }
                }
            }
            return false;
        }

        private static int CommandError(string message)
        {
            LogFailure(message, null);
            return 2;
        }

        private static void OnThreadException(object sender, ThreadExceptionEventArgs args)
        {
            LogFailure("界面线程发生异常", args.Exception);
        }

        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs args)
        {
            LogFailure("程序发生未处理异常", args.ExceptionObject as Exception);
        }

        internal static void LogFailure(string message, Exception error)
        {
            string detail = message + (error == null ? String.Empty : ": " + error);
            try
            {
                AppFiles.Log(detail);
            }
            catch (Exception logError)
            {
                Trace.TraceError(detail + "；写入日志失败: " + logError);
            }
        }
    }
}
