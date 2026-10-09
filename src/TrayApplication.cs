using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ReplayKeeper
{
    internal sealed class TrayApplication : ApplicationContext
    {
        private readonly EventWaitHandle exitEvent;
        private readonly Stopwatch clock;
        private readonly ReplayMonitor monitor;
        private readonly System.Windows.Forms.Timer timer;
        private readonly NotifyIcon trayIcon;
        private readonly Icon applicationIcon;
        private readonly ContextMenuStrip menu;
        private readonly ToolStripMenuItem stateItem;
        private readonly ToolStripMenuItem pauseItem;
        private readonly ToolStripMenuItem startupItem;
        private StatusWindow statusWindow;
        private MonitorReport lastReport;
        private DateTime lastCheck;
        private bool userPaused;
        private bool exiting;
        private int sessionLocked;
        private int powerSuspended;
        private int resetPending;
        private string lastError;

        public TrayApplication(EventWaitHandle exitEvent)
        {
            if (exitEvent == null) throw new ArgumentNullException("exitEvent");
            this.exitEvent = exitEvent;
            clock = Stopwatch.StartNew();
            monitor = new ReplayMonitor(new NvidiaPlatform(), Log);

            menu = new ContextMenuStrip();
            stateItem = new ToolStripMenuItem("实时状态：检查中") { Enabled = false };
            pauseItem = new ToolStripMenuItem("暂停守护");
            startupItem = new ToolStripMenuItem("开机自启动") { CheckOnClick = false };
            ToolStripMenuItem showItem = new ToolStripMenuItem("查看状态窗口");
            ToolStripMenuItem logsItem = new ToolStripMenuItem("打开日志文件夹");
            ToolStripMenuItem exitItem = new ToolStripMenuItem("退出");
            pauseItem.Click += delegate { TogglePause(); };
            startupItem.Click += delegate { ToggleStartup(); };
            showItem.Click += delegate { ShowStatus(); };
            logsItem.Click += delegate { OpenLogs(); };
            exitItem.Click += delegate { ExitThread(); };
            menu.Items.Add(stateItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(pauseItem);
            menu.Items.Add(startupItem);
            menu.Items.Add(showItem);
            menu.Items.Add(logsItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exitItem);
            menu.Opening += delegate { RefreshStartup(); };

            trayIcon = new NotifyIcon();
            applicationIcon = AppIcon.Load(SystemInformation.SmallIconSize);
            trayIcon.Icon = applicationIcon;
            trayIcon.Text = "ReplayKeeper：检查中";
            trayIcon.ContextMenuStrip = menu;
            trayIcon.DoubleClick += delegate { ShowStatus(); };

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 3000;
            timer.Tick += delegate { Tick(); };
            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            trayIcon.Visible = true;
            Log("ReplayKeeper 已启动");
            Application.Idle += OnFirstIdle;
            timer.Start();
        }

        private void OnFirstIdle(object sender, EventArgs args)
        {
            Application.Idle -= OnFirstIdle;
            Tick();
        }

        private void Tick()
        {
            if (exiting) return;
            try
            {
                if (exitEvent.WaitOne(0))
                {
                    Log("收到退出请求");
                    ExitThread();
                    return;
                }

                long now = clock.ElapsedMilliseconds;
                if (Interlocked.Exchange(ref resetPending, 0) != 0)
                    monitor.Reset(now);
                bool paused = userPaused ||
                    Interlocked.CompareExchange(ref sessionLocked, 0, 0) != 0 ||
                    Interlocked.CompareExchange(ref powerSuspended, 0, 0) != 0;
                lastReport = monitor.Tick(now, paused);
                lastCheck = DateTime.Now;
                lastError = null;
            }
            catch (Exception error)
            {
                string description = error.ToString();
                if (description != lastError)
                {
                    Program.LogFailure("守护检查失败", error);
                    lastError = description;
                }
                lastCheck = DateTime.Now;
                lastReport = new MonitorReport
                {
                    Snapshot = new ReplaySnapshot { State = ReplayState.Unknown, Detail = error.Message },
                    Message = "守护检查失败：" + error.Message,
                    Phase = "error",
                    Paused = true
                };
            }

            UpdateView();
            try
            {
                AppFiles.WriteJson("status.json", new
                {
                    Snapshot = lastReport.Snapshot,
                    Message = lastReport.Message,
                    RecoveryCount = lastReport.RecoveryCount,
                    AttemptCount = lastReport.AttemptCount,
                    Paused = lastReport.Paused,
                    Phase = lastReport.Phase,
                    LastCheck = lastCheck.ToString("o"),
                    LogError = AppFiles.LastLogError
                });
            }
            catch (Exception error)
            {
                Program.LogFailure("无法保存状态文件", error);
            }
        }

        private void UpdateView()
        {
            ReplaySnapshot snapshot = lastReport == null ? null : lastReport.Snapshot;
            string state = StateText(snapshot);
            stateItem.Text = "实时状态：" + state;
            pauseItem.Text = userPaused ? "恢复守护" : "暂停守护";
            trayIcon.Text = "ReplayKeeper：" + (lastReport != null && lastReport.Paused ? "守护暂停" : "守护中") + "，回放" + state;
            if (statusWindow != null && !statusWindow.IsDisposed)
                statusWindow.UpdateStatus(lastReport, lastCheck, userPaused,
                    Interlocked.CompareExchange(ref sessionLocked, 0, 0) != 0,
                    Interlocked.CompareExchange(ref powerSuspended, 0, 0) != 0,
                    ReadStartupText());
        }

        private void TogglePause()
        {
            userPaused = !userPaused;
            if (!userPaused)
                monitor.Reset(clock.ElapsedMilliseconds);
            Log(userPaused ? "用户暂停守护" : "用户恢复守护");
            Tick();
        }

        private void ToggleStartup()
        {
            try
            {
                Startup.SetEnabled(!Startup.IsEnabled());
                RefreshStartup();
                Log(startupItem.Checked ? "已启用开机自启动" : "已关闭开机自启动");
            }
            catch (Exception error)
            {
                Program.LogFailure("修改开机自启动失败", error);
                MessageBox.Show("无法修改开机自启动：" + error.Message, "ReplayKeeper",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            UpdateView();
        }

        private void RefreshStartup()
        {
            try
            {
                startupItem.Checked = Startup.IsEnabled();
                startupItem.Enabled = true;
                startupItem.Text = "开机自启动";
            }
            catch (Exception error)
            {
                startupItem.Checked = false;
                startupItem.Enabled = false;
                startupItem.Text = "开机自启动（读取失败）";
                Program.LogFailure("读取开机自启动状态失败", error);
            }
        }

        private string ReadStartupText()
        {
            try { return Startup.IsEnabled() ? "已启用" : "未启用"; }
            catch (Exception error)
            {
                Program.LogFailure("读取开机自启动状态失败", error);
                return "读取失败";
            }
        }

        private void ShowStatus()
        {
            if (statusWindow == null || statusWindow.IsDisposed)
            {
                statusWindow = new StatusWindow();
                statusWindow.FormClosing += OnStatusClosing;
            }
            statusWindow.UpdateStatus(lastReport, lastCheck, userPaused,
                Interlocked.CompareExchange(ref sessionLocked, 0, 0) != 0,
                Interlocked.CompareExchange(ref powerSuspended, 0, 0) != 0,
                ReadStartupText());
            statusWindow.Show();
            statusWindow.Activate();
        }

        private void OnStatusClosing(object sender, FormClosingEventArgs args)
        {
            if (!exiting && args.CloseReason == CloseReason.UserClosing)
            {
                args.Cancel = true;
                statusWindow.Hide();
            }
        }

        private void OpenLogs()
        {
            try
            {
                string folder = AppFiles.DataDirectory;
                Directory.CreateDirectory(folder);
                Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
            }
            catch (Exception error)
            {
                Program.LogFailure("打开日志文件夹失败", error);
                MessageBox.Show("无法打开日志文件夹：" + error.Message, "ReplayKeeper",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OnSessionSwitch(object sender, SessionSwitchEventArgs args)
        {
            if (args.Reason == SessionSwitchReason.SessionLock ||
                args.Reason == SessionSwitchReason.RemoteDisconnect ||
                args.Reason == SessionSwitchReason.ConsoleDisconnect)
                Interlocked.Exchange(ref sessionLocked, 1);
            else if (args.Reason == SessionSwitchReason.SessionUnlock ||
                args.Reason == SessionSwitchReason.RemoteConnect ||
                args.Reason == SessionSwitchReason.ConsoleConnect)
            {
                Interlocked.Exchange(ref sessionLocked, 0);
                Interlocked.Exchange(ref resetPending, 1);
            }
        }

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs args)
        {
            if (args.Mode == PowerModes.Suspend)
                Interlocked.Exchange(ref powerSuspended, 1);
            else if (args.Mode == PowerModes.Resume)
            {
                Interlocked.Exchange(ref powerSuspended, 0);
                Interlocked.Exchange(ref resetPending, 1);
            }
        }

        protected override void ExitThreadCore()
        {
            if (exiting) return;
            exiting = true;
            timer.Stop();
            Application.Idle -= OnFirstIdle;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            trayIcon.Visible = false;
            if (statusWindow != null)
            {
                statusWindow.FormClosing -= OnStatusClosing;
                statusWindow.Close();
                statusWindow.Dispose();
            }
            trayIcon.Dispose();
            applicationIcon.Dispose();
            menu.Dispose();
            timer.Dispose();
            Log("ReplayKeeper 已退出");
            base.ExitThreadCore();
        }

        private static string StateText(ReplaySnapshot snapshot)
        {
            if (snapshot == null) return "未知";
            if (snapshot.State == ReplayState.On) return "已开启";
            if (snapshot.State == ReplayState.Off) return "已关闭";
            return "未知";
        }

        private static void Log(string message)
        {
            Program.LogFailure(message, null);
        }

        private sealed class StatusWindow : Form
        {
            private readonly Icon windowIcon;
            private readonly Label stateValue;
            private readonly Label pauseValue;
            private readonly Label recoveryValue;
            private readonly Label checkValue;
            private readonly Label startupValue;
            private readonly Label hotkeyValue;
            private readonly TextBox reasonValue;

            public StatusWindow()
            {
                windowIcon = AppIcon.Load(SystemInformation.IconSize);
                Icon = windowIcon;
                Text = "ReplayKeeper 状态";
                ClientSize = new Size(500, 300);
                MinimumSize = new Size(516, 339);
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = false;
                MinimizeBox = false;
                StartPosition = FormStartPosition.CenterScreen;

                TableLayoutPanel layout = new TableLayoutPanel();
                layout.Dock = DockStyle.Fill;
                layout.Padding = new Padding(16);
                layout.ColumnCount = 2;
                layout.RowCount = 7;
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                for (int i = 0; i < 6; i++)
                    layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 31));
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

                stateValue = AddValue(layout, 0, "即时回放");
                pauseValue = AddValue(layout, 1, "守护状态");
                recoveryValue = AddValue(layout, 2, "恢复次数");
                checkValue = AddValue(layout, 3, "上次检查");
                startupValue = AddValue(layout, 4, "开机自启动");
                hotkeyValue = AddValue(layout, 5, "恢复快捷键");
                Label reasonTitle = new Label { Text = "状态原因", AutoSize = true,
                    Anchor = AnchorStyles.Left | AnchorStyles.Top, Margin = new Padding(3, 8, 3, 3) };
                reasonValue = new TextBox { ReadOnly = true, Multiline = true,
                    BorderStyle = BorderStyle.None, BackColor = SystemColors.Control,
                    ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, TabStop = false };
                layout.Controls.Add(reasonTitle, 0, 6);
                layout.Controls.Add(reasonValue, 1, 6);
                Controls.Add(layout);
            }

            protected override void Dispose(bool disposing)
            {
                base.Dispose(disposing);
                if (disposing && windowIcon != null) windowIcon.Dispose();
            }

            public void UpdateStatus(MonitorReport report, DateTime checkedAt, bool userPaused,
                bool locked, bool suspended, string startup)
            {
                ReplaySnapshot snapshot = report == null ? null : report.Snapshot;
                stateValue.Text = StateText(snapshot);
                pauseValue.Text = userPaused ? "用户已暂停" : locked ? "会话已锁定" :
                    suspended ? "系统已挂起" : report != null && report.Phase == "error" ?
                    "检查异常" : "运行中";
                recoveryValue.Text = report == null ? "0" : report.RecoveryCount.ToString();
                checkValue.Text = checkedAt == DateTime.MinValue ? "尚未检查" :
                    checkedAt.ToString("yyyy-MM-dd HH:mm:ss");
                startupValue.Text = startup;
                hotkeyValue.Text = snapshot == null || String.IsNullOrEmpty(snapshot.HotkeyDisplay) ?
                    "未读取到" : snapshot.HotkeyDisplay;
                string message = report == null ? "等待首次检查" : report.Message ?? String.Empty;
                string detail = snapshot == null ? String.Empty : snapshot.Detail ?? String.Empty;
                string reason = String.IsNullOrEmpty(detail) || detail == message ? message :
                    message + Environment.NewLine + detail;
                string logError = AppFiles.LastLogError;
                if (!String.IsNullOrEmpty(logError))
                    reason += Environment.NewLine + "日志写入失败：" + logError;
                reasonValue.Text = reason;
            }

            private static Label AddValue(TableLayoutPanel layout, int row, string title)
            {
                Label caption = new Label { Text = title, AutoSize = true,
                    Anchor = AnchorStyles.Left, Margin = new Padding(3) };
                Label value = new Label { Text = "—", AutoEllipsis = true,
                    Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
                    Margin = new Padding(3) };
                layout.Controls.Add(caption, 0, row);
                layout.Controls.Add(value, 1, row);
                return value;
            }
        }
    }
}
