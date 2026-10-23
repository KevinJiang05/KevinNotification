using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace KevinNotificationBridge
{
    // Shared by Windows PowerShell 5.1 and the .NET tray application.
    // Raw reports exclude cursor warps; injected-event flags exclude SendInput.
    // Both listeners run on one dedicated thread, away from notification I/O.
    public sealed class AwayGuardInput : IDisposable
    {
        private readonly object _gate = new object();
        private readonly MouseActivity _activity = new MouseActivity();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly ManualResetEvent _ready = new ManualResetEvent(false);
        private readonly Thread _thread;
        private readonly HookProc _callback;
        private uint _threadId;
        private IntPtr _lastDevice;
        private bool _hasAbsolutePosition;
        private Point _lastAbsolutePosition;
        private ushort _lastAbsoluteFlags;
        private bool _disposed;

        public bool IsAvailable { get; private set; }
        public int ErrorCode { get; private set; }

        public AwayGuardInput()
        {
            _callback = OnMouseInput;
            _thread = new Thread(Run) { IsBackground = true, Name = "Away guard mouse input" };
            _thread.Start();
            _ready.WaitOne();
        }

        public void SetEnabled(bool enabled)
        {
            lock (_gate)
            {
                if (_activity.Enabled == enabled) return;
                _activity.SetEnabled(enabled);
                _hasAbsolutePosition = false;
                _lastDevice = IntPtr.Zero;
            }
        }

        public bool TryTakeAlert(int intervalSeconds, out string reason)
        {
            lock (_gate)
            {
                return _activity.TryTakeAlert(_clock.ElapsedMilliseconds, intervalSeconds, out reason);
            }
        }

        private void Run()
        {
            IntPtr hook = IntPtr.Zero;
            try
            {
                _threadId = GetCurrentThreadId();
                NativeMessage message;
                PeekMessage(out message, IntPtr.Zero, 0, 0, 0); // Create the thread message queue before signalling readiness.
                hook = SetWindowsHookEx(14, _callback, GetModuleHandle(IntPtr.Zero), 0);
                if (hook == IntPtr.Zero) ErrorCode = Marshal.GetLastWin32Error();
                using (var inputWindow = new RawMouseWindow(this))
                {
                    IsAvailable = hook != IntPtr.Zero && inputWindow.IsAvailable;
                    if (!inputWindow.IsAvailable) ErrorCode = inputWindow.ErrorCode;
                    _ready.Set();
                    if (!IsAvailable) return;
                    while (GetMessage(out message, IntPtr.Zero, 0, 0) > 0)
                    {
                        TranslateMessage(ref message);
                        DispatchMessage(ref message);
                    }
                }
            }
            finally
            {
                if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
                IsAvailable = false;
                _ready.Set();
            }
        }

        private IntPtr OnMouseInput(int code, IntPtr message, IntPtr data)
        {
            if (code >= 0 && message.ToInt64() == 0x0200)
            {
                var mouse = Marshal.PtrToStructure<HookMouse>(data);
                lock (_gate)
                {
                    // WM_INPUT has no injected flag, and a zero device handle can
                    // also be a genuine precision touchpad. Correlate event times
                    // instead of discarding all zero-handle reports.
                    if ((mouse.Flags & 3) != 0)
                    {
                        _activity.RecordSoftwareInput(mouse.Time);
                    }
                }
            }
            return CallNextHookEx(IntPtr.Zero, code, message, data);
        }

        private void RecordDeviceMouse(IntPtr device, RawMouse mouse, uint time)
        {
            lock (_gate)
            {
                if (!_activity.AcceptsDeviceInput(device, time)) return;
                if (device != _lastDevice)
                {
                    _hasAbsolutePosition = false;
                    _lastDevice = device;
                }
                if ((mouse.Flags & 4) != 0) { _hasAbsolutePosition = false; return; }
                if ((mouse.Flags & 1) == 0)
                {
                    _hasAbsolutePosition = false;
                    _activity.RecordMovement(mouse.LastX, mouse.LastY);
                    return;
                }
                var area = (mouse.Flags & 2) != 0 ? SystemInformation.VirtualScreen : new Rectangle(Point.Empty, SystemInformation.PrimaryMonitorSize);
                var position = new Point((int)((long)mouse.LastX * area.Width / 65535), (int)((long)mouse.LastY * area.Height / 65535));
                ushort flags = (ushort)(mouse.Flags & 3);
                if (_hasAbsolutePosition && flags == _lastAbsoluteFlags)
                    _activity.RecordMovement(position.X - _lastAbsolutePosition.X, position.Y - _lastAbsolutePosition.Y);
                _lastAbsolutePosition = position;
                _lastAbsoluteFlags = flags;
                _hasAbsolutePosition = true;
            }
        }

        private sealed class RawMouseWindow : NativeWindow, IDisposable
        {
            private readonly AwayGuardInput _owner;
            private readonly uint _headerSize = (uint)Marshal.SizeOf(typeof(RawInputHeader));
            private readonly uint _packetSize = (uint)(Marshal.SizeOf(typeof(RawInputHeader)) + 24);
            private readonly IntPtr _buffer;
            public bool IsAvailable { get; private set; }
            public int ErrorCode { get; private set; }

            public RawMouseWindow(AwayGuardInput owner)
            {
                _owner = owner;
                _buffer = Marshal.AllocHGlobal((int)_packetSize);
                CreateHandle(new CreateParams { Parent = new IntPtr(-3) });
                var devices = new[] { new RawInputDevice { UsagePage = 1, Usage = 2, Flags = 0x100, Target = Handle } };
                IsAvailable = RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf(typeof(RawInputDevice)));
                if (!IsAvailable) ErrorCode = Marshal.GetLastWin32Error();
            }

            protected override void WndProc(ref Message message)
            {
                if (message.Msg == 0x00ff && IsAvailable)
                {
                    uint size = _packetSize;
                    uint read = GetRawInputData(message.LParam, 0x10000003, _buffer, ref size, _headerSize);
                    if (read != uint.MaxValue && read >= _packetSize)
                    {
                        var header = Marshal.PtrToStructure<RawInputHeader>(_buffer);
                        if (header.Type == 0)
                        {
                            var mouse = Marshal.PtrToStructure<RawMouse>(IntPtr.Add(_buffer, (int)_headerSize));
                            _owner.RecordDeviceMouse(header.Device, mouse, unchecked((uint)GetMessageTime()));
                        }
                    }
                }
                base.WndProc(ref message);
            }

            public void Dispose()
            {
                if (IsAvailable)
                {
                    var devices = new[] { new RawInputDevice { UsagePage = 1, Usage = 2, Flags = 1 } };
                    RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf(typeof(RawInputDevice)));
                    IsAvailable = false;
                }
                DestroyHandle();
                Marshal.FreeHGlobal(_buffer);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            SetEnabled(false);
            if (_thread.IsAlive)
            {
                PostThreadMessage(_threadId, 0x0012, IntPtr.Zero, IntPtr.Zero);
                _thread.Join();
            }
            _ready.Dispose();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        private struct RawInputDevice
        {
            public ushort UsagePage, Usage;
            public uint Flags;
            public IntPtr Target;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct RawInputHeader
        {
            public uint Type, Size;
            public IntPtr Device, WParam;
        }
        [StructLayout(LayoutKind.Explicit, Size = 24)]
        private struct RawMouse
        {
            [FieldOffset(0)] public ushort Flags;
            [FieldOffset(12)] public int LastX;
            [FieldOffset(16)] public int LastY;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct HookMouse
        {
            public NativePoint Position;
            public uint MouseData, Flags, Time;
            public UIntPtr ExtraInfo;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeMessage
        {
            public IntPtr Window;
            public uint Message;
            public UIntPtr WParam;
            public IntPtr LParam;
            public uint Time;
            public NativePoint Position;
            public uint Private;
        }
        private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int type, HookProc callback, IntPtr module, uint threadId);
        [DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
        [DllImport("user32.dll")]
        private static extern int GetMessage(out NativeMessage message, IntPtr window, uint min, uint max);
        [DllImport("user32.dll")]
        private static extern bool PeekMessage(out NativeMessage message, IntPtr window, uint min, uint max, uint remove);
        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref NativeMessage message);
        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref NativeMessage message);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterRawInputDevices([In] RawInputDevice[] devices, uint count, uint size);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetRawInputData(IntPtr input, uint command, IntPtr data, ref uint size, uint headerSize);
        [DllImport("user32.dll")]
        private static extern int GetMessageTime();
        [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW")]
        private static extern IntPtr GetModuleHandle(IntPtr name);
        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();
    }

    internal sealed class MouseActivity
    {
        private const int MinimumMovement = 24;
        private const int WindowMilliseconds = 10000;
        private readonly Queue<long> _samples = new Queue<long>();
        private readonly List<uint> _injectedTimes = new List<uint>();
        private long _movementX, _movementY;
        private long _lastAlertMilliseconds = -1;
        private long _ignoredSoftwareEvents;
        public bool Enabled { get; private set; }

        public void SetEnabled(bool enabled)
        {
            Enabled = enabled;
            _samples.Clear();
            _movementX = _movementY = 0;
            _lastAlertMilliseconds = -1;
            _ignoredSoftwareEvents = 0;
        }

        public void RecordSoftwareInput(uint time)
        {
            _injectedTimes.RemoveAll(previous => unchecked(time - previous) > 2000);
            _injectedTimes.Add(time);
            if (Enabled) _ignoredSoftwareEvents++;
        }

        public bool AcceptsDeviceInput(IntPtr device, uint time)
        {
            return Enabled && (device != IntPtr.Zero || !_injectedTimes.Contains(time));
        }

        public void RecordMovement(int dx, int dy)
        {
            if (!Enabled) return;
            // Net movement in each 500 ms sample filters small back-and-forth jitter.
            _movementX += dx;
            _movementY += dy;
        }

        public bool TryTakeAlert(long nowMilliseconds, int intervalSeconds, out string reason)
        {
            reason = string.Empty;
            long dx = _movementX, dy = _movementY;
            _movementX = _movementY = 0;
            while (_samples.Count > 0 && nowMilliseconds - _samples.Peek() >= WindowMilliseconds) _samples.Dequeue();
            if (!Enabled || (double)dx * dx + (double)dy * dy < MinimumMovement * MinimumMovement) return false;
            _samples.Enqueue(nowMilliseconds);
            if (_samples.Count < 2 || (_lastAlertMilliseconds >= 0 && nowMilliseconds - _lastAlertMilliseconds < Math.Max(1, intervalSeconds) * 1000L)) return false;
            reason = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "source=non-injected-mouse; activeSamples={0}; windowMs={1}; latestNetDelta=({2},{3}); ignoredSoftwareEvents={4}", _samples.Count, WindowMilliseconds, dx, dy, _ignoredSoftwareEvents);
            _samples.Clear();
            _lastAlertMilliseconds = nowMilliseconds;
            return true;
        }
    }
}
