using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using ReplayKeeper;

internal static class MonitorTests
{
    private static int assertions;
    private static int Main()
    {
        try
        {
            OnAndUnknownNeverSend();
            OffConfirmationAndSuccess();
            BackoffAndFailure();
            PauseAndOverlayRestart();
            ExceptionsAndRace();
            RegistryIntegration();
            GameOnlyGuard();
            GameMatching();
            GameDetectionIntegration();
            Check(Marshal.SizeOf(typeof(NvidiaPlatform).GetNestedType("INPUT", System.Reflection.BindingFlags.NonPublic)) == 40,
                "x64 SendInput structure matches Windows ABI");
            LiveReadOnly();
            Console.WriteLine("PASS: " + assertions + " assertions, including isolated registry integration and live read-only state.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void Check(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new Exception("FAIL: " + message);
    }

    private static ReplayMonitor New(FakePlatform fake)
    {
        return new ReplayMonitor(fake, delegate { });
    }

    private static void OnAndUnknownNeverSend()
    {
        var fake = new FakePlatform { State = ReplayState.On };
        var monitor = New(fake);
        monitor.Tick(0, false, true);
        Check(monitor.Tick(100000, false, true).Phase == "on", "on reported");
        Check(fake.EnableCalls == 0, "already on must never receive toggle");
        fake.State = ReplayState.Unknown;
        for (int i = 1; i < 10; i++) monitor.Tick(100000 + i * 3000, false, true);
        Check(fake.EnableCalls == 0, "unknown must never receive toggle");
        fake.Ready = false;
        fake.State = ReplayState.Off;
        Check(monitor.Tick(140000, false, true).Phase == "waiting-overlay", "wait for overlay");
        Check(fake.EnableCalls == 0, "missing overlay must never receive toggle");
    }

    private static void OffConfirmationAndSuccess()
    {
        var fake = new FakePlatform();
        var monitor = New(fake);
        Check(monitor.Tick(0, false, true).Phase == "starting", "startup grace");
        monitor.Tick(19000, false, true);
        Check(fake.EnableCalls == 0, "startup guard protects overlay");
        Check(monitor.Tick(21000, false, true).Phase == "confirming-off", "first off read");
        Check(monitor.Tick(24000, false, true).Phase == "verifying", "second off read requests enable");
        Check(fake.EnableCalls == 1, "one request");
        Check(monitor.Tick(27000, false, true).Phase == "verifying", "do not send again while pending");
        fake.State = ReplayState.On;
        MonitorReport report = monitor.Tick(30000, false, true);
        Check(report.RecoveryCount == 1 && report.AttemptCount == 1, "count only verified success");
        monitor.Tick(60000, false, true);
        Check(fake.EnableCalls == 1, "on remains untouched");
        fake.State = ReplayState.Off;
        monitor.Tick(63000, false, true);
        monitor.Tick(66000, false, true);
        Check(fake.EnableCalls == 2, "later unexpected off recovers again");
        fake.State = ReplayState.On;
        Check(monitor.Tick(69000, false, true).RecoveryCount == 2, "second recovery confirmed");
    }

    private static void BackoffAndFailure()
    {
        var fake = new FakePlatform();
        var monitor = New(fake);
        monitor.Tick(0, false, true);
        monitor.Tick(21000, false, true);
        monitor.Tick(24000, false, true);
        monitor.Tick(30000, false, true);
        MonitorReport report = monitor.Tick(33000, false, true);
        Check(report.Phase == "cooldown" && report.RecoveryCount == 0, "unconfirmed send is not success");
        monitor.Tick(62000, false, true);
        Check(fake.EnableCalls == 1, "first retry waits 30 seconds");
        monitor.Tick(63000, false, true);
        monitor.Tick(66000, false, true);
        Check(fake.EnableCalls == 2, "first retry also requires confirmation");
        monitor.Tick(75000, false, true);
        monitor.Tick(134000, false, true);
        Check(fake.EnableCalls == 2, "second retry waits 60 seconds");
        monitor.Tick(135000, false, true);
        monitor.Tick(138000, false, true);
        Check(fake.EnableCalls == 3, "second retry");
        monitor.Tick(147000, false, true);
        monitor.Tick(266000, false, true);
        Check(fake.EnableCalls == 3, "third retry waits 120 seconds");
        monitor.Tick(267000, false, true);
        monitor.Tick(270000, false, true);
        monitor.Tick(279000, false, true);
        monitor.Tick(578000, false, true);
        Check(fake.EnableCalls == 4, "further retries wait 300 seconds");
        monitor.Tick(579000, false, true);
        monitor.Tick(582000, false, true);
        Check(fake.EnableCalls == 5, "persistent failure continues with bounded retry");

        fake = new FakePlatform { Result = EnableResult.Failed };
        monitor = New(fake);
        monitor.Tick(0, false, true);
        monitor.Tick(21000, false, true);
        Check(monitor.Tick(24000, false, true).Phase == "cooldown", "input failure backs off immediately");
        monitor.Tick(53000, false, true);
        Check(fake.EnableCalls == 1, "input failure is rate limited");
    }

    private static void PauseAndOverlayRestart()
    {
        var fake = new FakePlatform();
        var monitor = New(fake);
        monitor.Tick(0, false, true);
        monitor.Tick(21000, false, true);
        Check(monitor.Tick(24000, true, true).Paused, "user pause reported");
        monitor.Tick(100000, true, true);
        Check(fake.EnableCalls == 0, "paused guard never toggles");
        monitor.Reset(100000);
        monitor.Tick(103000, false, true);
        monitor.Tick(119000, false, true);
        Check(fake.EnableCalls == 0, "resume grace");
        monitor.Tick(120000, false, true);
        fake.Identity = "replacement";
        Check(monitor.Tick(123000, false, true).Phase == "starting", "overlay replacement restarts readiness wait");
        monitor.Tick(132000, false, true);
        Check(fake.EnableCalls == 0, "overlay replacement grace");
        monitor.Tick(133000, false, true);
        monitor.Tick(136000, false, true);
        Check(fake.EnableCalls == 1, "replacement overlay eventually recovers");
    }

    private static void ExceptionsAndRace()
    {
        var fake = new FakePlatform { ThrowRead = true };
        var monitor = New(fake);
        Check(monitor.Tick(0, false, true).Snapshot.State == ReplayState.Unknown, "read exception becomes unknown");
        monitor.Tick(100000, false, true);
        Check(fake.EnableCalls == 0, "read error never toggles");
        fake.ThrowRead = false;
        fake.Result = EnableResult.Deferred;
        monitor.Tick(103000, false, true);
        monitor.Tick(114000, false, true);
        Check(monitor.Tick(117000, false, true).Phase == "deferred", "final state race or busy input can defer");
        Check(monitor.Tick(120000, false, true).AttemptCount == 0, "deferred is not a sent attempt");
        fake.Result = EnableResult.Sent;
        monitor.Tick(123000, false, true);
        fake.State = ReplayState.Unknown;
        Check(monitor.Tick(126000, false, true).Phase == "unknown", "unknown after send does not imply success");
        fake.State = ReplayState.On;
        Check(monitor.Tick(129000, false, true).RecoveryCount == 1, "confirmed on after unknown is success");
    }

    private static void RegistryIntegration()
    {
        string path = @"Software\ReplayKeeperTests\" + Guid.NewGuid().ToString("N");
        try
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(path))
            {
                var platform = new NvidiaPlatform(path, delegate { return "test-overlay"; });
                Check(platform.Read().State == ReplayState.Unknown, "missing flag is unknown");
                key.SetValue(NvidiaPlatform.ReplayValue, new byte[] { 1, 0, 0, 0 }, RegistryValueKind.Binary);
                Check(platform.Read().State == ReplayState.On, "binary on");
                key.SetValue(NvidiaPlatform.ReplayValue, 0, RegistryValueKind.DWord);
                Check(platform.Read().State == ReplayState.Off, "DWORD off");
                key.SetValue(NvidiaPlatform.ReplayValue, 7, RegistryValueKind.DWord);
                Check(platform.Read().State == ReplayState.Unknown, "unexpected flag value is unknown");
                key.SetValue(NvidiaPlatform.ReplayValue, "0", RegistryValueKind.String);
                Check(platform.Read().State == ReplayState.Unknown, "string flag rejected");
                key.SetValue(NvidiaPlatform.ReplayValue, new byte[] { 0 }, RegistryValueKind.Binary);
                Check(platform.Read().State == ReplayState.Unknown, "malformed binary flag rejected");
                Check(NvidiaPlatform.ReadHotkey(key) == null, "missing hotkey does not invent default");
                key.SetValue("IRToggleHKeyCount", new byte[] { 3, 0, 0, 0 }, RegistryValueKind.Binary);
                key.SetValue("IRToggleHKey0", new byte[] { 18, 0, 0, 0 }, RegistryValueKind.Binary);
                key.SetValue("IRToggleHKey1", 16, RegistryValueKind.DWord);
                key.SetValue("IRToggleHKey2", 121, RegistryValueKind.DWord);
                Check(NvidiaPlatform.FormatHotkey(NvidiaPlatform.ReadHotkey(key)) == "Alt + Shift + F10", "actual mixed binary/DWORD shortcut read");
                key.SetValue("IRToggleHKey2", 16, RegistryValueKind.DWord);
                Check(NvidiaPlatform.ReadHotkey(key) == null, "duplicate modifier rejected");
                key.SetValue("IRToggleHKey2", 91, RegistryValueKind.DWord);
                Check(NvidiaPlatform.ReadHotkey(key) == null, "Windows key shortcut rejected");
                key.SetValue("IRToggleHKeyCount", 100, RegistryValueKind.DWord);
                Check(NvidiaPlatform.ReadHotkey(key) == null, "unbounded count rejected");
                var unavailable = new NvidiaPlatform(path, delegate { return ""; });
                Check(!unavailable.Read().OverlayReady, "missing overlay reported");
                key.SetValue(NvidiaPlatform.ReplayValue, 1, RegistryValueKind.DWord);
                Check(unavailable.Read().State == ReplayState.Unknown, "stored on flag cannot prove on without a live overlay");
                string reason;
                Check(unavailable.TryEnable(out reason) == EnableResult.Deferred, "missing overlay never attempts input");
                Check(platform.TryEnable(out reason) == EnableResult.Deferred, "final recheck of on does not attempt input");
            }
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(path, false); }
    }

    private static void LiveReadOnly()
    {
        ReplaySnapshot snapshot = new NvidiaPlatform().Read();
        Console.WriteLine("Live read-only: State=" + snapshot.State + ", OverlayReady=" + snapshot.OverlayReady + ", Hotkey=" + snapshot.HotkeyDisplay);
        Console.WriteLine("Live detail: " + snapshot.Detail);
        Check(snapshot.State == ReplayState.On || snapshot.State == ReplayState.Off || snapshot.State == ReplayState.Unknown, "live read produces typed state");
    }

    private static void GameOnlyGuard()
    {
        FakePlatform platform = new FakePlatform();
        ReplayMonitor monitor = new ReplayMonitor(platform, null);
        Check(monitor.Tick(0, false, false).Phase == "waiting-game", "no game waits");
        monitor.Tick(60000, false, false);
        Check(platform.ReadCalls == 0 && platform.EnableCalls == 0, "idle never reads NVIDIA or sends keys");
        monitor.Tick(61000, false, true);
        monitor.Tick(79000, false, true);
        Check(platform.ReadCalls == 2 && platform.EnableCalls == 0, "entering game starts with grace period");
        monitor.Tick(82000, false, true);
        Check(monitor.Tick(85000, false, true).Phase == "verifying", "in-game off confirmation requests recovery");
        int reads = platform.ReadCalls;
        monitor.Tick(86000, false, false);
        platform.State = ReplayState.On;
        MonitorReport idle = monitor.Tick(92000, false, false);
        Check(platform.ReadCalls == reads && platform.EnableCalls == 1, "leaving game immediately stops polling and retries");
        Check(idle.RecoveryCount == 0, "idle does not falsely confirm a pending recovery");
        MonitorReport reenter = monitor.Tick(100000, false, true);
        Check(reenter.RecoveryCount == 0, "reenter discards previous game's pending recovery");
        platform.State = ReplayState.Off;
        monitor.Tick(101000, false, true);
        Check(platform.EnableCalls == 1, "reenter gets fresh startup grace");
        reads = platform.ReadCalls;
        Check(monitor.Tick(103000, true, false).Phase == "paused", "manual pause remains distinct from waiting for game");
        Check(platform.ReadCalls == reads, "paused without game never polls NVIDIA");
    }

    private static void GameMatching()
    {
        GameCatalog catalog = new GameCatalog(new[] { @"D:\Games\Example.exe", @"D:\Steam\steam.exe",
            @"C:\Java\javaw.exe", @"D:\Game\Launcher.exe", @"D:\LoL\LeagueClientUx.exe" },
            new[] { "# comment", @"E:\游戏\Other.exe", @"!D:\Games\Example.exe", @"C:\Java\javaw.exe" });
        Check(!catalog.Matches(@"D:\Games\Example.exe"), "explicit exclusion wins");
        Check(catalog.Matches(@"e:\游戏\other.EXE"), "custom full paths match case insensitively");
        Check(!catalog.Matches(@"D:\Unrelated\Other.exe"), "same name at different path never matches");
        Check(!catalog.Matches(@"D:\Steam\steam.exe") && !catalog.Matches(@"D:\Game\Launcher.exe") &&
            !catalog.Matches(@"D:\LoL\LeagueClientUx.exe"), "launcher and clients alone do not count as a game");
        Check(catalog.Matches(@"C:\Java\javaw.exe"), "explicitly added shared runtime is supported");
        Check(!new GameCatalog(new[] { @"C:\Java\javaw.exe" }, new string[0]).Matches(@"C:\Java\javaw.exe"),
            "automatic shared runtime does not count as a game");
        Check(catalog.MightMatch("Other") && !catalog.MightMatch("steam"), "name prefilter follows final catalog");
        bool rejected = false;
        try { new GameCatalog(new string[0], new[] { "relative.exe" }); } catch (FormatException) { rejected = true; }
        Check(rejected, "malformed custom configuration is rejected rather than widening matching");
        rejected = false;
        try { new GameCatalog(new string[0], new[] { @"C:relative.exe" }); } catch (FormatException) { rejected = true; }
        Check(rejected, "drive-relative paths are rejected");
        GameActivity live = new GameActivityDetector().Read();
        Check(live != null && live.Detail.Length > 0, "live game probe is read-only and returns a reason");
        Console.WriteLine("Live game probe: " + live.Running + ", " + live.Name);
    }

    private static void GameDetectionIntegration()
    {
        string root = @"Software\ReplayKeeper.Tests.Games." + Guid.NewGuid().ToString("N");
        string config = Path.GetTempFileName();
        try
        {
            string executable;
            using (System.Diagnostics.Process self = System.Diagnostics.Process.GetCurrentProcess()) executable = self.MainModule.FileName;
            GameActivityDetector detector = new GameActivityDetector(root, config);
            Check(!detector.Read().Running, "empty game catalog remains idle with normal processes running");
            using (RegistryKey game = Registry.CurrentUser.CreateSubKey(root + @"\one"))
                game.SetValue("MatchedExeFullPath", executable);
            detector.Refresh();
            Check(detector.Read().Running, "registered exact executable is detected in current session");
            File.WriteAllText(config, "!" + executable);
            detector.Refresh();
            Check(!detector.Read().Running, "custom exclusion disables an automatically recognized process");
            Registry.CurrentUser.DeleteSubKeyTree(root);
            File.WriteAllText(config, executable);
            detector.Refresh();
            Check(detector.Read().Running, "custom game works even without Windows game registry");
            File.WriteAllText(config, "invalid.exe");
            detector.Refresh();
            GameActivity invalid = detector.Read();
            Check(!invalid.Running && invalid.Detail.Contains("不可用"), "bad reload fails closed instead of retaining active game");
            File.WriteAllText(config, "");
            detector.Refresh();
            Check(!detector.Read().Running, "removing configured game stops recognition");
        }
        finally
        {
            File.Delete(config);
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(root))
                if (key != null) { key.Close(); Registry.CurrentUser.DeleteSubKeyTree(root); }
        }
    }

    private sealed class FakePlatform : IReplayPlatform
    {
        public ReplayState State = ReplayState.Off;
        public bool Ready = true;
        public string Identity = "test";
        public int EnableCalls;
        public int ReadCalls;
        public bool ThrowRead;
        public EnableResult Result = EnableResult.Sent;

        public ReplaySnapshot Read()
        {
            ReadCalls++;
            if (ThrowRead) throw new IOException("simulated read error");
            return new ReplaySnapshot { State = State, OverlayReady = Ready, OverlayIdentity = Identity, Detail = "test" };
        }
        public EnableResult TryEnable(out string detail)
        {
            EnableCalls++;
            detail = "simulated request";
            return Result;
        }
    }
}
