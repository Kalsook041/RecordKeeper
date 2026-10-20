using System;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace ReplayKeeper
{
    public enum ReplayState { Unknown, On, Off }
    public enum EnableResult { Sent, Deferred, Failed }

    public sealed class ReplaySnapshot
    {
        public ReplayState State;
        public bool OverlayReady;
        public string OverlayIdentity = "";
        public string Detail = "";
        public string HotkeyDisplay = "";
    }

    public interface IReplayPlatform
    {
        ReplaySnapshot Read();
        EnableResult TryEnable(out string detail);
    }

    public sealed class MonitorReport
    {
        public ReplaySnapshot Snapshot;
        public string Message;
        public int RecoveryCount;
        public int AttemptCount;
        public bool Paused;
        public string Phase;
    }

    // Monotonic time is supplied by Stopwatch, so wall-clock changes cannot skip backoff.
    public sealed class ReplayMonitor
    {
        private readonly IReplayPlatform platform;
        private readonly Action<string> log;
        private bool initialized;
        private long readyAfter;
        private long nextAttempt;
        private long? pendingSince;
        private string overlayIdentity;
        private int offPolls;
        private int failures;
        private int recoveries;
        private int attempts;
        private string lastTransition;
        private bool gameWasRunning = true;

        public ReplayMonitor(IReplayPlatform platform, Action<string> log)
        {
            if (platform == null) throw new ArgumentNullException("platform");
            this.platform = platform;
            this.log = log ?? delegate { };
        }

        public void Reset(long nowMilliseconds)
        {
            initialized = true;
            readyAfter = nowMilliseconds + 20000;
            nextAttempt = readyAfter;
            pendingSince = null;
            overlayIdentity = null;
            offPolls = 0;
            failures = 0;
        }

        public MonitorReport Tick(long nowMilliseconds, bool paused, bool gameRunning)
        {
            if (!gameRunning)
            {
                Reset(nowMilliseconds);
                gameWasRunning = false;
                return Report(new ReplaySnapshot { Detail = "非游戏期间不检测即时回放，也不执行恢复" },
                    paused, paused ? "paused" : "waiting-game", paused ? "守护已暂停，不执行恢复" : "等待游戏启动，检测未启用");
            }
            if (!gameWasRunning) Reset(nowMilliseconds);
            gameWasRunning = true;
            if (!initialized) Reset(nowMilliseconds);
            ReplaySnapshot snapshot;
            try { snapshot = platform.Read(); }
            catch (Exception ex)
            {
                snapshot = new ReplaySnapshot { State = ReplayState.Unknown,
                    Detail = "状态读取失败：" + ex.Message };
            }
            if (snapshot == null) snapshot = new ReplaySnapshot { Detail = "未收到状态" };

            if (paused)
            {
                offPolls = 0;
                pendingSince = null;
                return Report(snapshot, true, "paused", "守护已暂停，不执行恢复");
            }
            if (!snapshot.OverlayReady)
            {
                offPolls = 0;
                pendingSince = null;
                overlayIdentity = null;
                return Report(snapshot, false, "waiting-overlay", snapshot.Detail);
            }
            if (overlayIdentity != snapshot.OverlayIdentity)
            {
                overlayIdentity = snapshot.OverlayIdentity;
                readyAfter = Math.Max(readyAfter, nowMilliseconds + 10000);
                offPolls = 0;
                pendingSince = null;
            }

            if (snapshot.State == ReplayState.On)
            {
                if (pendingSince.HasValue)
                {
                    recoveries++;
                    log("恢复成功：状态复查已开启");
                }
                pendingSince = null;
                offPolls = 0;
                failures = 0;
                nextAttempt = nowMilliseconds;
                return Report(snapshot, false, "on", "即时回放已开启，正在守护");
            }
            if (snapshot.State != ReplayState.Off)
            {
                offPolls = 0;
                // Keep a sent request pending until an actual on/off observation arrives.
                return Report(snapshot, false, "unknown", snapshot.Detail);
            }
            if (pendingSince.HasValue)
            {
                if (nowMilliseconds - pendingSince.Value < 9000)
                    return Report(snapshot, false, "verifying", "已请求开启，正在复查状态");
                pendingSince = null;
                RecordFailure(nowMilliseconds);
                return Report(snapshot, false, "cooldown", "开启未生效，稍后重试；请检查 NVIDIA 提示或录制限制");
            }
            if (nowMilliseconds < readyAfter)
            {
                offPolls = 0;
                return Report(snapshot, false, "starting", "等待 NVIDIA 叠加层就绪");
            }
            if (nowMilliseconds < nextAttempt)
                return Report(snapshot, false, "cooldown", "开启未生效，稍后重试；请检查 NVIDIA 提示或录制限制");
            if (++offPolls < 2)
                return Report(snapshot, false, "confirming-off", "检测到关闭，等待再次确认");

            offPolls = 0;
            string detail;
            EnableResult result;
            try { result = platform.TryEnable(out detail); }
            catch (Exception ex) { result = EnableResult.Failed; detail = "请求开启失败：" + ex.Message; }
            if (result == EnableResult.Deferred)
                return Report(snapshot, false, "deferred", detail);
            attempts++;
            log("恢复尝试 #" + attempts + "：" + detail);
            if (result == EnableResult.Sent)
            {
                pendingSince = nowMilliseconds;
                return Report(snapshot, false, "verifying", "已请求开启，正在复查状态");
            }
            RecordFailure(nowMilliseconds);
            return Report(snapshot, false, "cooldown", detail);
        }

        private void RecordFailure(long now)
        {
            failures = Math.Min(failures + 1, 4);
            int[] delay = { 30000, 60000, 120000, 300000 };
            nextAttempt = now + delay[failures - 1];
            offPolls = 0;
            log("恢复未生效，" + (delay[failures - 1] / 1000) + " 秒后允许重试");
        }

        private MonitorReport Report(ReplaySnapshot snapshot, bool paused, string phase, string message)
        {
            string transition = phase + ": " + message;
            if (transition != lastTransition)
            {
                log(transition);
                lastTransition = transition;
            }
            return new MonitorReport { Snapshot = snapshot, Paused = paused, Phase = phase,
                Message = message, RecoveryCount = recoveries, AttemptCount = attempts };
        }
    }

    public static class AppFiles
    {
        private static readonly object gate = new object();
        public static string LastLogError { get; private set; }
        public static string DataDirectory
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReplayKeeper"); }
        }

        public static void Log(string message)
        {
            lock (gate)
            {
                try
                {
                    Directory.CreateDirectory(DataDirectory);
                    string path = Path.Combine(DataDirectory, "keeper.log");
                    if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024)
                    {
                        string previous = path + ".1";
                        if (File.Exists(previous)) File.Delete(previous);
                        File.Move(path, previous);
                    }
                    File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message + Environment.NewLine, new UTF8Encoding(false));
                    LastLogError = "";
                }
                catch (IOException ex) { LastLogError = ex.Message; System.Diagnostics.Trace.WriteLine("Log write failed: " + ex); }
                catch (UnauthorizedAccessException ex) { LastLogError = ex.Message; System.Diagnostics.Trace.WriteLine("Log write failed: " + ex); }
            }
        }

        public static void WriteJson(string filename, object value)
        {
            if (Path.GetFileName(filename) != filename) throw new ArgumentException("Expected a filename", "filename");
            lock (gate)
            {
                Directory.CreateDirectory(DataDirectory);
                string path = Path.Combine(DataDirectory, filename);
                string temporary = path + ".tmp";
                File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(value), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
        }
    }
}
