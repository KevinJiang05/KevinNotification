using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using KevinNotificationBridge;

public static class AwayGuardInputTests
{
    public static string[] Run()
    {
        var passed = new List<string>();
        string reason;
        var activity = EnabledActivity();
        activity.RecordSoftwareInput(100);
        Expect(!activity.AcceptsDeviceInput(IntPtr.Zero, 100), "Injected zero-handle mouse report");
        Expect(activity.AcceptsDeviceInput(new IntPtr(1), 100), "Concurrent genuine device report");
        Expect(activity.AcceptsDeviceInput(IntPtr.Zero, 101), "Precision touchpad report");
        activity.SetEnabled(false);
        Expect(!activity.AcceptsDeviceInput(new IntPtr(1), 101), "Disarmed device report");
        activity.SetEnabled(true);
        passed.Add("Software reports are excluded; genuine mice and zero-handle touchpads remain accepted");
        Expect(!activity.TryTakeAlert(0, 2, out reason), "Stationary input");
        passed.Add("Stationary cursor: no alert");

        for (int i = 0; i < 10; i++)
        {
            activity.RecordMovement(6, 6);
            Expect(!activity.TryTakeAlert(i * 500, 2, out reason), "Small diagonal jitter");
        }
        passed.Add("Small repeated diagonal jitter: no alert");

        activity = EnabledActivity();
        for (int i = 0; i < 4; i++)
        {
            activity.RecordMovement(40, 0);
            activity.RecordMovement(-40, 0);
            Expect(!activity.TryTakeAlert(i * 500, 2, out reason), "Back-and-forth jitter");
        }
        passed.Add("Back-and-forth movement within a sample: no alert");

        activity = EnabledActivity();
        activity.RecordMovement(40, 0);
        Expect(!activity.TryTakeAlert(0, 2, out reason), "First movement sample");
        Expect(!activity.TryTakeAlert(500, 2, out reason), "One-off movement");
        activity.RecordMovement(40, 0);
        Expect(activity.TryTakeAlert(1000, 2, out reason), "Second device movement sample");
        Expect(reason.Contains("source=non-injected-mouse") && reason.Contains("activeSamples=2"), "Alert reason");
        activity.RecordMovement(40, 0);
        Expect(!activity.TryTakeAlert(3000, 2, out reason), "Old activity must not be reused");
        activity.RecordMovement(40, 0);
        Expect(activity.TryTakeAlert(3500, 2, out reason), "Two new samples after an alert");
        passed.Add("Sustained movement alerts; each alert requires fresh movement");

        activity = EnabledActivity();
        activity.RecordMovement(24, 0);
        Expect(!activity.TryTakeAlert(0, 2, out reason), "Window first sample");
        activity.RecordMovement(24, 0);
        Expect(!activity.TryTakeAlert(10000, 2, out reason), "Ten-second expiry boundary");
        passed.Add("Expired activity does not trigger a later alert");

        activity = EnabledActivity();
        activity.RecordMovement(24, 0);
        activity.TryTakeAlert(0, 2, out reason);
        activity.RecordMovement(24, 0);
        Expect(activity.TryTakeAlert(500, 2, out reason), "Initial cooldown alert");
        activity.RecordMovement(24, 0);
        Expect(!activity.TryTakeAlert(1000, 2, out reason), "Cooldown first sample");
        activity.RecordMovement(24, 0);
        Expect(!activity.TryTakeAlert(1500, 2, out reason), "Cooldown second sample");
        Expect(!activity.TryTakeAlert(2500, 2, out reason), "Cooldown cannot alert without new input");
        activity.RecordMovement(24, 0);
        Expect(activity.TryTakeAlert(3000, 2, out reason), "Cooldown has expired");
        passed.Add("Alert interval remains enforced without delayed stationary alerts");

        activity = EnabledActivity();
        activity.RecordMovement(40, 0);
        activity.TryTakeAlert(0, 2, out reason);
        activity.RecordMovement(100, 0);
        activity.SetEnabled(false);
        activity.RecordMovement(100, 0);
        activity.SetEnabled(true);
        Expect(!activity.TryTakeAlert(1000, 2, out reason), "Disabled activity was discarded");
        activity.RecordMovement(40, 0);
        Expect(!activity.TryTakeAlert(1500, 2, out reason), "Rearming needs new samples");
        passed.Add("Disarm and rearm clear stale movement and queued activity");

        activity = EnabledActivity();
        for (int sample = 0; sample < 2; sample++)
        {
            for (int report = 0; report < 20; report++) activity.RecordMovement(2, 0);
            bool alert = activity.TryTakeAlert(sample * 500, 2, out reason);
            Expect(alert == (sample == 1), "High-frequency reports");
        }
        passed.Add("Small genuine device reports accumulate into sustained movement");
        return passed.ToArray();
    }

    // Optional integration probe: only moves the cursor, never clicks or sends keys.
    // The original cursor position is restored even if the test fails.
    public static string[] ProbeSoftwareMovement()
    {
        var passed = new List<string>();
        NativePoint original;
        Expect(GetCursorPos(out original), "Read original cursor position");
        using (var input = new AwayGuardInput())
        {
            Expect(input.IsAvailable, "Mouse hook registration: error " + input.ErrorCode);
            input.SetEnabled(true);
            try
            {
                string reason;
                int direction = original.X > 100 ? -1 : 1;
                if (!SetCursorPos(original.X + 60 * direction, original.Y))
                    throw new InvalidOperationException("Windows refused the cursor probe; unlock the input desktop before retrying. Win32 error " + Marshal.GetLastWin32Error());
                Thread.Sleep(550); // Simulate a busy tray UI without starving the input listener.
                bool cursorAlert1 = input.TryTakeAlert(1, out reason);
                Expect(SetCursorPos(original.X, original.Y), "SetCursorPos return");
                Thread.Sleep(550);
                bool cursorAlert2 = input.TryTakeAlert(1, out reason);
                Expect(!cursorAlert1 && !cursorAlert2, "SetCursorPos probe: alert occurred (keep physical mouse idle during the probe)");
                passed.Add("Live SetCursorPos move and return: no alert");

                input.SetEnabled(false);
                input.SetEnabled(true);
                SendMovement(60 * direction);
                Thread.Sleep(550);
                bool sendAlert1 = input.TryTakeAlert(1, out reason);
                SendMovement(-60 * direction);
                Thread.Sleep(550);
                bool sendAlert2 = input.TryTakeAlert(1, out reason);
                Expect(!sendAlert1 && !sendAlert2, "SendInput probe: alert occurred (keep physical mouse idle during the probe): " + reason);
                passed.Add("Live SendInput move and return: no alert");
            }
            finally
            {
                SetCursorPos(original.X, original.Y);
                Pump(50);
            }
        }
        return passed.ToArray();
    }

    private static MouseActivity EnabledActivity()
    {
        var activity = new MouseActivity();
        activity.SetEnabled(true);
        return activity;
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Pump(int milliseconds)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < milliseconds)
        {
            Application.DoEvents();
            Thread.Sleep(5);
        }
    }

    private static void SendMovement(int dx)
    {
        var inputs = new[] { new NativeInput { Mouse = new NativeMouseInput { Dx = dx, Flags = 0x2001 } } };
        Expect(SendInput(1, inputs, Marshal.SizeOf(typeof(NativeInput))) == 1, "SendInput injection: error " + Marshal.GetLastWin32Error());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMouseInput
    {
        public int Dx, Dy;
        public uint MouseData, Flags, Time;
        public UIntPtr ExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput { public uint Type; public NativeMouseInput Mouse; }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, [In] NativeInput[] inputs, int size);
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetCursorPos(int x, int y);
}
