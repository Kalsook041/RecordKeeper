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
        monitor.Tick(0, false);
        Check(monitor.Tick(100000, false).Phase == "on", "on reported");
        Check(fake.EnableCalls == 0, "already on must never receive toggle");
        fake.State = ReplayState.Unknown;
        for (int i = 1; i < 10; i++) monitor.Tick(100000 + i * 3000, false);
        Check(fake.EnableCalls == 0, "unknown must never receive toggle");
        fake.Ready = false;
        fake.State = ReplayState.Off;
        Check(monitor.Tick(140000, false).Phase == "waiting-overlay", "wait for overlay");
        Check(fake.EnableCalls == 0, "missing overlay must never receive toggle");
    }

    private static void OffConfirmationAndSuccess()
    {
        var fake = new FakePlatform();
        var monitor = New(fake);
        Check(monitor.Tick(0, false).Phase == "starting", "startup grace");
        monitor.Tick(19000, false);
        Check(fake.EnableCalls == 0, "startup guard protects overlay");
        Check(monitor.Tick(21000, false).Phase == "confirming-off", "first off read");
        Check(monitor.Tick(24000, false).Phase == "verifying", "second off read requests enable");
        Check(fake.EnableCalls == 1, "one request");
        Check(monitor.Tick(27000, false).Phase == "verifying", "do not send again while pending");
        fake.State = ReplayState.On;
        MonitorReport report = monitor.Tick(30000, false);
        Check(report.RecoveryCount == 1 && report.AttemptCount == 1, "count only verified success");
        monitor.Tick(60000, false);
        Check(fake.EnableCalls == 1, "on remains untouched");
        fake.State = ReplayState.Off;
        monitor.Tick(63000, false);
        monitor.Tick(66000, false);
        Check(fake.EnableCalls == 2, "later unexpected off recovers again");
        fake.State = ReplayState.On;
        Check(monitor.Tick(69000, false).RecoveryCount == 2, "second recovery confirmed");
    }

    private static void BackoffAndFailure()
    {
        var fake = new FakePlatform();
        var monitor = New(fake);
        monitor.Tick(0, false);
        monitor.Tick(21000, false);
        monitor.Tick(24000, false);
        monitor.Tick(30000, false);
        MonitorReport report = monitor.Tick(33000, false);
        Check(report.Phase == "cooldown" && report.RecoveryCount == 0, "unconfirmed send is not success");
        monitor.Tick(62000, false);
        Check(fake.EnableCalls == 1, "first retry waits 30 seconds");
        monitor.Tick(63000, false);
        monitor.Tick(66000, false);
        Check(fake.EnableCalls == 2, "first retry also requires confirmation");
        monitor.Tick(75000, false);
        monitor.Tick(134000, false);
        Check(fake.EnableCalls == 2, "second retry waits 60 seconds");
        monitor.Tick(135000, false);
        monitor.Tick(138000, false);
        Check(fake.EnableCalls == 3, "second retry");
        monitor.Tick(147000, false);
        monitor.Tick(266000, false);
        Check(fake.EnableCalls == 3, "third retry waits 120 seconds");
        monitor.Tick(267000, false);
        monitor.Tick(270000, false);
        monitor.Tick(279000, false);
        monitor.Tick(578000, false);
        Check(fake.EnableCalls == 4, "further retries wait 300 seconds");
        monitor.Tick(579000, false);
        monitor.Tick(582000, false);
        Check(fake.EnableCalls == 5, "persistent failure continues with bounded retry");

        fake = new FakePlatform { Result = EnableResult.Failed };
        monitor = New(fake);
        monitor.Tick(0, false);
        monitor.Tick(21000, false);
        Check(monitor.Tick(24000, false).Phase == "cooldown", "input failure backs off immediately");
        monitor.Tick(53000, false);
        Check(fake.EnableCalls == 1, "input failure is rate limited");
    }

    private static void PauseAndOverlayRestart()
    {
        var fake = new FakePlatform();
        var monitor = New(fake);
        monitor.Tick(0, false);
        monitor.Tick(21000, false);
        Check(monitor.Tick(24000, true).Paused, "user pause reported");
        monitor.Tick(100000, true);
        Check(fake.EnableCalls == 0, "paused guard never toggles");
        monitor.Reset(100000);
        monitor.Tick(103000, false);
        monitor.Tick(119000, false);
        Check(fake.EnableCalls == 0, "resume grace");
        monitor.Tick(120000, false);
        fake.Identity = "replacement";
        Check(monitor.Tick(123000, false).Phase == "starting", "overlay replacement restarts readiness wait");
        monitor.Tick(132000, false);
        Check(fake.EnableCalls == 0, "overlay replacement grace");
        monitor.Tick(133000, false);
        monitor.Tick(136000, false);
        Check(fake.EnableCalls == 1, "replacement overlay eventually recovers");
    }

    private static void ExceptionsAndRace()
    {
        var fake = new FakePlatform { ThrowRead = true };
        var monitor = New(fake);
        Check(monitor.Tick(0, false).Snapshot.State == ReplayState.Unknown, "read exception becomes unknown");
        monitor.Tick(100000, false);
        Check(fake.EnableCalls == 0, "read error never toggles");
        fake.ThrowRead = false;
        fake.Result = EnableResult.Deferred;
        monitor.Tick(103000, false);
        monitor.Tick(114000, false);
        Check(monitor.Tick(117000, false).Phase == "deferred", "final state race or busy input can defer");
        Check(monitor.Tick(120000, false).AttemptCount == 0, "deferred is not a sent attempt");
        fake.Result = EnableResult.Sent;
        monitor.Tick(123000, false);
        fake.State = ReplayState.Unknown;
        Check(monitor.Tick(126000, false).Phase == "unknown", "unknown after send does not imply success");
        fake.State = ReplayState.On;
        Check(monitor.Tick(129000, false).RecoveryCount == 1, "confirmed on after unknown is success");
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

    private sealed class FakePlatform : IReplayPlatform
    {
        public ReplayState State = ReplayState.Off;
        public bool Ready = true;
        public string Identity = "test";
        public int EnableCalls;
        public bool ThrowRead;
        public EnableResult Result = EnableResult.Sent;

        public ReplaySnapshot Read()
        {
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
