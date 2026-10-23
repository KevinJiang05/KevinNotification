using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace KevinNotificationBridge;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        using var mutex = new Mutex(true, "Local\\KevinNotificationBridgeNative", out var created);
        if (!created)
        {
            MessageBox.Show("新版通知桥接已在运行。", "Kevin 通知桥接", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var paths = new AppPaths();
        var store = new SettingsStore(paths);
        var settings = store.Load();
        if (string.IsNullOrWhiteSpace(settings.AppPassword))
        {
            var password = PasswordDialog.Create();
            if (password is null) return;
            settings.AppPassword = password;
            store.Save(settings);
        }

        Application.Run(new BridgeContext(paths, store, settings));
    }
}

internal sealed class BridgeContext : ApplicationContext
{
    private readonly AppPaths _paths;
    private readonly SettingsStore _store;
    private readonly NotifyIcon _icon;
    private readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer _sessionTimer = new();
    private readonly System.Windows.Forms.Timer _mouseTimer = new() { Interval = 500 };
    private readonly SessionMonitor _monitor = new();
    private readonly FeishuNotifier _notifier = new();
    private readonly HotkeyWindow _hotkeyWindow;
    private readonly AwayGuardInput _mouseInput = new();
    private readonly List<DateTime> _guardAlerts = [];
    private DateTime _lastLock = DateTime.MinValue;
    private bool _guardArmed;
    private bool? _iconEnabled;
    private string _registeredHotkey = string.Empty;
    private SecurityWarningForm? _warning;
    private AppSettings _settings;

    public BridgeContext(AppPaths paths, SettingsStore store, AppSettings settings)
    {
        _paths = paths;
        _store = store;
        _settings = settings;
        if (!_mouseInput.IsAvailable) AppendLog($"Away guard mouse input unavailable: Win32 error {_mouseInput.ErrorCode}.");
        _hotkeyWindow = new HotkeyWindow(ToggleNotifications);

        var menu = new ContextMenuStrip();
        menu.Items.Add("开启通知", null, (_, _) => ToggleNotifications());
        menu.Items.Add("离开防护", null, (_, _) => ToggleGuard());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("打开设置", null, (_, _) => OpenSettings());
        menu.Items.Add("发送测试", null, async (_, _) => await SendTestAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitWithPassword());

        _icon = new NotifyIcon
        {
            Visible = true,
            ContextMenuStrip = menu,
            Text = "Kevin 通知桥接",
            Icon = TrayIcons.Create(true),
        };
        _icon.DoubleClick += (_, _) => OpenSettings();

        _uiTimer.Tick += (_, _) => RefreshRuntime();
        _sessionTimer.Tick += async (_, _) => await ScanSessionsAsync();
        _mouseTimer.Tick += async (_, _) => await CheckMouseAsync();
        RefreshRuntime();
        _uiTimer.Start();
        _sessionTimer.Start();
        _mouseTimer.Start();
    }

    private void RefreshRuntime()
    {
        _settings = _store.Load();
        var interval = Math.Clamp(_settings.WatchIntervalSeconds, 5, 60) * 1000;
        if (_sessionTimer.Interval != interval) _sessionTimer.Interval = interval;
        var guardArmed = _settings.AwayGuardLinkedToNotifications ? _settings.Enabled : _settings.AwayGuardEnabled;
        if (_guardArmed != guardArmed)
        {
            CloseWarningAndReset();
            _guardArmed = guardArmed;
            _mouseInput.SetEnabled(_guardArmed);
        }
        if (!_guardArmed) CloseWarningAndReset();

        if (!string.Equals(_registeredHotkey, _settings.Hotkey, StringComparison.OrdinalIgnoreCase))
        {
            _hotkeyWindow.TryRegister(_settings.Hotkey, out var registered);
            _registeredHotkey = registered ? _settings.Hotkey : string.Empty;
            _store.WriteRuntimeStatus(_settings.Hotkey, registered ? "registered" : "failed");
        }

        _icon.Text = _settings.Enabled ? "Kevin 通知桥接：已开启" : "Kevin 通知桥接：已关闭";
        if (_iconEnabled != _settings.Enabled)
        {
            var oldIcon = _icon.Icon;
            _icon.Icon = TrayIcons.Create(_settings.Enabled);
            oldIcon?.Dispose();
            _iconEnabled = _settings.Enabled;
        }
    }

    private void ToggleNotifications()
    {
        _settings.Enabled = !_settings.Enabled;
        if (!_settings.Enabled)
        {
            _settings.AwayGuardEnabled = false;
            CloseWarningAndReset();
        }
        _store.Save(_settings);
        RefreshRuntime();
    }

    private void ToggleGuard()
    {
        if (_settings.AwayGuardLinkedToNotifications) return;
        _settings.AwayGuardEnabled = !_settings.AwayGuardEnabled;
        _store.Save(_settings);
        RefreshRuntime();
    }

    private async Task ScanSessionsAsync()
    {
        foreach (var item in _monitor.ReadNewEvents())
        {
            if (item.Kind == BridgeEventKind.Completed && _settings.CompletionNotifications)
                await _notifier.SendAsync(_settings, "✅ Codex 任务完成", item.Project, item.Message, _paths.LogPath);
            else if (item.Kind == BridgeEventKind.Failed && _settings.FailureNotifications)
                await _notifier.SendAsync(_settings, "⚠️ Codex 任务异常", item.Project, item.Message, _paths.LogPath);
        }
    }

    private async Task CheckMouseAsync()
    {
        if (!_guardArmed) return;
        if (!_mouseInput.TryTakeAlert(_settings.GuardAlertIntervalSeconds, out var reason)) return;
        var now = DateTime.Now;
        AppendLog($"Away guard alert: {reason}.");
        _guardAlerts.RemoveAll(time => time <= now.AddSeconds(-30));
        _guardAlerts.Add(now);
        ShowWarning();
        if (_settings.CaptureCameraOnGuardAlert) _ = Task.Run(() => CameraCapture.TryCapture(_settings, _paths));
        await _notifier.SendAsync(_settings, "🚨 离开防护告警", Environment.CurrentDirectory, "检测到 10 秒内持续的鼠标设备移动。", _paths.LogPath);

        if (!_guardArmed) return;

        if (_settings.LockWorkstationAfterRepeatedGuardAlerts && _guardAlerts.Count >= 3)
        {
            if ((now - _lastLock).TotalSeconds >= _settings.LockScreenCooldownSeconds)
            {
                _lastLock = now;
                if (NativeMethods.LockWorkStation())
                {
                    _guardArmed = false;
                    _settings.Enabled = false;
                    _settings.AwayGuardEnabled = false;
                    _store.Save(_settings);
                    AppendLog("自动锁屏成功后，已关闭后续通知和离开防护。");
                    CloseWarningAndReset();
                }
                else AppendLog("自动锁屏请求失败。");
            }
            _guardAlerts.Clear();
        }
    }

    private void ShowWarning()
    {
        if (_warning is { IsDisposed: false }) { _warning.Activate(); return; }
        _warning = new SecurityWarningForm(_settings.SecurityWarningText);
        _warning.FormClosed += (_, _) => _warning = null;
        _warning.EmergencyDismissed += (_, _) => EmergencyDismissGuard();
        _warning.Show();
    }

    private void CloseWarningAndReset()
    {
        _mouseInput.SetEnabled(false);
        _guardAlerts.Clear();
        if (_warning is { IsDisposed: false }) _warning.Close();
        _warning = null;
    }

    private void EmergencyDismissGuard()
    {
        _settings = _store.Load();
        _settings.AwayGuardEnabled = false;
        _settings.AwayGuardLinkedToNotifications = false;
        _store.Save(_settings);
        _guardArmed = false;
        CloseWarningAndReset();
        AppendLog("Emergency mouse dismissal disabled away guard after five warning-icon clicks.");
    }

    private async Task SendTestAsync()
    {
        var sent = await _notifier.SendAsync(_settings, "✅ Codex 任务完成", "新程序测试", "原生 .NET 托盘程序测试成功。", _paths.LogPath);
        MessageBox.Show(sent ? "测试通知已被飞书接受，请查看群聊。" : "测试通知未发送成功，请检查通知开关、Webhook 和运行日志。", "Kevin 通知桥接", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void OpenSettings()
    {
        _settings = _store.Load();
        if (!PasswordDialog.Verify(_settings.AppPassword, "身份验证")) return;
        using var form = new SettingsForm(_settings, _paths, _store);
        if (form.ShowDialog() == DialogResult.OK) RefreshRuntime();
    }

    private void ExitWithPassword()
    {
        _settings = _store.Load();
        if (PasswordDialog.Verify(_settings.AppPassword, "身份验证")) ExitThread();
    }

    private void AppendLog(string message) => _paths.AppendLog(message);

    protected override void ExitThreadCore()
    {
        _uiTimer.Dispose(); _sessionTimer.Dispose(); _mouseTimer.Dispose(); _hotkeyWindow.Dispose();
        _icon.Visible = false; _icon.Dispose(); CloseWarningAndReset();
        _mouseInput.Dispose();
        _notifier.Dispose();
        _store.WriteRuntimeStatus(_settings.Hotkey, "stopped");
        base.ExitThreadCore();
    }
}

internal sealed class AppPaths
{
    public string Root { get; }
    public string SettingsPath => Path.Combine(Root, "settings.json");
    public string RuntimeStatusPath => Path.Combine(Root, "runtime-status.json");
    public string LogPath => Path.Combine(Root, "notifications.log");
    public string CaptureRoot => Path.Combine(Root, "captures");
    public AppPaths(string? root = null)
    {
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KevinNotificationBridge");
        Directory.CreateDirectory(Root);
    }
    public void AppendLog(string message) => File.AppendAllText(LogPath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}", new UTF8Encoding(false));
}

internal sealed class AppSettings
{
    public bool Enabled { get; set; } = true;
    public string Hotkey { get; set; } = "Alt+W";
    public bool CompletionNotifications { get; set; } = true;
    public bool FailureNotifications { get; set; } = true;
    public bool AwayGuardLinkedToNotifications { get; set; }
    public bool AwayGuardEnabled { get; set; }
    public int WatchIntervalSeconds { get; set; } = 10;
    public int GuardAlertIntervalSeconds { get; set; } = 2;
    public bool CaptureCameraOnGuardAlert { get; set; }
    public int CaptureRetentionCount { get; set; } = 30;
    public bool LockWorkstationAfterRepeatedGuardAlerts { get; set; }
    public int LockScreenCooldownSeconds { get; set; } = 120;
    public string AppPassword { get; set; } = string.Empty;
    public string SecurityWarningText { get; set; } = "检测到异常操作\r\n为保护设备与隐私，已触发远程提醒。\r\n请立即停止操作。";
}

internal sealed class SettingsStore(AppPaths paths)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public AppSettings Load()
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(paths.SettingsPath), Options) ?? new AppSettings(); }
        catch { return new AppSettings(); }
    }
    public void Save(AppSettings settings) => File.WriteAllText(paths.SettingsPath, JsonSerializer.Serialize(settings, Options), new UTF8Encoding(false));
    public void WriteRuntimeStatus(string hotkey, string state) => File.WriteAllText(paths.RuntimeStatusPath, JsonSerializer.Serialize(new { hotkey, state, updatedAt = DateTimeOffset.Now }, Options), new UTF8Encoding(false));
}

internal enum BridgeEventKind { Completed, Failed }
internal sealed record BridgeEvent(BridgeEventKind Kind, string Project, string Message);

internal sealed class SessionMonitor
{
    private readonly Dictionary<string, long> _offsets = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _cwdByFile = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _sessionRoot;

    public SessionMonitor(string? sessionRoot = null) => _sessionRoot = sessionRoot ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");

    public IEnumerable<BridgeEvent> ReadNewEvents()
    {
        if (!Directory.Exists(_sessionRoot)) yield break;
        foreach (var file in Directory.EnumerateFiles(_sessionRoot, "rollout-*.jsonl", SearchOption.AllDirectories))
        {
            var info = new FileInfo(file);
            if (info.LastWriteTime < DateTime.Now.AddHours(-24)) continue;
            if (!_offsets.TryGetValue(file, out var offset))
            {
                // Learn the project from session metadata without replaying old notifications.
                using var headerStream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var headerReader = new StreamReader(headerStream, Encoding.UTF8);
                var header = headerReader.ReadLine();
                if (header is not null) _ = ParseLine(file, header);
                _offsets[file] = info.Length;
                continue;
            }
            if (info.Length < offset) offset = 0;
            if (info.Length <= offset) continue;
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.Seek(offset, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                var eventItem = ParseLine(file, line);
                if (eventItem is not null) yield return eventItem;
            }
            _offsets[file] = stream.Position;
        }
    }

    private BridgeEvent? ParseLine(string file, string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (!doc.RootElement.TryGetProperty("payload", out var payload)) return null;
            if (payload.TryGetProperty("cwd", out var cwd)) _cwdByFile[file] = cwd.GetString() ?? string.Empty;
            if (!doc.RootElement.TryGetProperty("type", out var recordType) || recordType.GetString() != "event_msg") return null;
            var type = payload.TryGetProperty("type", out var typeNode) ? typeNode.GetString() ?? string.Empty : string.Empty;
            var hasError = payload.TryGetProperty("error", out var errorNode) && errorNode.ValueKind != JsonValueKind.Null;
            var failed = (type == "turn_aborted" && (!payload.TryGetProperty("reason", out var reason) || reason.GetString() != "interrupted")) || (type == "task_complete" && hasError);
            var capacity = (failed || type == "error") &&
                Regex.IsMatch(line, "model\\s+at\\s+capacity|model.*overloaded", RegexOptions.IgnoreCase);
            failed |= capacity;
            var completed = type == "task_complete" && !hasError;
            if (!failed && !completed) return null;
            var key = $"{file}:{line.GetHashCode()}";
            if (!_seen.Add(key)) return null;
            var project = _cwdByFile.TryGetValue(file, out var known) ? known : string.Empty;
            if (failed)
            {
                var message = capacity ? "模型当前容量不足，请稍后重试。" : hasError && errorNode.TryGetProperty("message", out var messageNode) ? messageNode.GetString() ?? "Codex 任务异常。" : "Codex 任务已中断。";
                return new BridgeEvent(BridgeEventKind.Failed, project, message);
            }
            var summary = payload.TryGetProperty("last_agent_message", out var last) ? last.GetString() ?? "任务已完成。" : "任务已完成。";
            return new BridgeEvent(BridgeEventKind.Completed, project, summary);
        }
        catch { return null; }
    }
}

internal sealed class FeishuNotifier : IDisposable
{
    private readonly HttpClient _client;
    public FeishuNotifier(HttpMessageHandler? handler = null)
    {
        _client = handler is null ? new HttpClient() : new HttpClient(handler);
        _client.Timeout = TimeSpan.FromSeconds(10);
    }

    public Task<bool> SendAsync(AppSettings settings, string title, string project, string result, string logPath)
    {
        if (!settings.Enabled) return Task.FromResult(false);
        return SendAsync(settings, title, project, result, logPath,
            Environment.GetEnvironmentVariable("CODEX_FEISHU_WEBHOOK", EnvironmentVariableTarget.User));
    }

    // Explicit destination lets tests use an in-memory HTTP handler without reading user credentials.
    internal async Task<bool> SendAsync(AppSettings settings, string title, string project, string result, string logPath, string? webhook)
    {
        if (!settings.Enabled) return false;
        if (string.IsNullOrWhiteSpace(webhook)) { Append(logPath, "Skipped: CODEX_FEISHU_WEBHOOK is not configured."); return false; }
        try
        {
            var text = $"{title}\n项目：{Truncate(project, 120)}\n时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}\n结果：{Truncate(result, 240)}";
            var content = JsonSerializer.Serialize(new { msg_type = "text", content = new { text } });
            using var response = await _client.PostAsync(webhook, new StringContent(content, Encoding.UTF8, "application/json"));
            if (!response.IsSuccessStatusCode)
            {
                Append(logPath, $"Send failed: HTTP {(int)response.StatusCode}.");
                return false;
            }
            using var reply = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (!reply.RootElement.TryGetProperty("code", out var code) || !code.TryGetInt32(out var codeValue))
            {
                Append(logPath, "Send failed: unrecognized Feishu response.");
                return false;
            }
            Append(logPath, codeValue == 0 ? "Send succeeded." : $"Send failed: Feishu code {codeValue}.");
            return codeValue == 0;
        }
        catch (Exception ex) { Append(logPath, $"Send failed: {ex.GetType().Name}."); return false; }
    }
    private static string Truncate(string value, int max) => string.IsNullOrWhiteSpace(value) ? "未提供" : value.Length <= max ? value : value[..max] + "…";
    private static void Append(string path, string message) => File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}", new UTF8Encoding(false));
    public void Dispose() => _client.Dispose();
}

internal static class CameraCapture
{
    public static void TryCapture(AppSettings settings, AppPaths paths)
    {
        try
        {
            var ffmpeg = FindFfmpeg();
            if (ffmpeg is null) { paths.AppendLog("Camera capture skipped: ffmpeg is unavailable."); return; }
            var devices = Run(ffmpeg, "-hide_banner -list_devices true -f dshow -i dummy", 8000);
            var match = Regex.Match(devices, "\"([^\"]+)\"\\s+\\(video\\)");
            if (!match.Success) { paths.AppendLog("Camera capture skipped: no video device found."); return; }
            Directory.CreateDirectory(paths.CaptureRoot);
            var output = Path.Combine(paths.CaptureRoot, $"guard-{DateTime.Now:yyyyMMdd-HHmmss-fff}.jpg");
            var result = Run(ffmpeg, $"-hide_banner -loglevel error -f dshow -i \"video={match.Groups[1].Value.Replace("\"", "\\\"")}\" -frames:v 1 -y \"{output}\"", 15000);
            if (!File.Exists(output)) { paths.AppendLog("Camera capture failed."); return; }
            foreach (var file in Directory.EnumerateFiles(paths.CaptureRoot, "guard-*.jpg").OrderByDescending(File.GetLastWriteTime).Skip(Math.Clamp(settings.CaptureRetentionCount, 10, 500))) File.Delete(file);
            paths.AppendLog("Camera capture succeeded.");
        }
        catch { paths.AppendLog("Camera capture failed."); }
    }
    private static string? FindFfmpeg()
    {
        var pathEntries = (Environment.GetEnvironmentVariable("Path") ?? string.Empty).Split(';');
        return pathEntries.Select(path => Path.Combine(path, "ffmpeg.exe")).FirstOrDefault(File.Exists);
    }
    private static string Run(string fileName, string arguments, int timeoutMs)
    {
        using var process = Process.Start(new ProcessStartInfo(fileName, arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true }) ?? throw new InvalidOperationException();
        if (!process.WaitForExit(timeoutMs)) { process.Kill(true); throw new TimeoutException(); }
        return process.StandardError.ReadToEnd();
    }
}

internal sealed class HotkeyWindow : NativeWindow, IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 8820;
    private readonly Action _pressed;
    public HotkeyWindow(Action pressed) { _pressed = pressed; CreateHandle(new CreateParams()); }
    public bool TryRegister(string specification, out bool registered)
    {
        NativeMethods.UnregisterHotKey(Handle, HotkeyId);
        var (modifiers, key) = Parse(specification);
        registered = NativeMethods.RegisterHotKey(Handle, HotkeyId, modifiers, (uint)key);
        return registered;
    }
    protected override void WndProc(ref Message m) { if (m.Msg == WmHotkey) _pressed(); base.WndProc(ref m); }
    private static (uint, Keys) Parse(string value)
    {
        uint modifiers = 0; var key = Keys.W;
        foreach (var part in value.ToUpperInvariant().Split('+', StringSplitOptions.RemoveEmptyEntries))
            if (part is "ALT") modifiers |= 1; else if (part is "CTRL" or "CONTROL") modifiers |= 2; else if (part is "SHIFT") modifiers |= 4; else if (Enum.TryParse<Keys>(part, true, out var parsed)) key = parsed;
        return (modifiers, key);
    }
    public void Dispose() { NativeMethods.UnregisterHotKey(Handle, HotkeyId); DestroyHandle(); }
}

internal sealed class SecurityWarningForm : Form
{
    private DateTime _firstWarningIconClick = DateTime.MinValue;
    private int _warningIconClicks;
    public event EventHandler? EmergencyDismissed;

    public SecurityWarningForm(string warningText)
    {
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Size = new Size((int)(area.Width * .75), (int)(area.Height * .75));
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
        FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.Manual; TopMost = true; BackColor = Color.Firebrick; ShowInTaskbar = false;
        var icon = new Label { Text = "⚠", ForeColor = Color.Gold, Font = new Font("Microsoft YaHei UI", 72, FontStyle.Bold), Dock = DockStyle.Top, Height = Height / 3, TextAlign = ContentAlignment.MiddleCenter };
        icon.Click += (_, _) => RegisterWarningIconClick();
        var text = new Label { Text = string.IsNullOrWhiteSpace(warningText) ? "检测到异常操作\r\n请立即停止操作。" : warningText, ForeColor = Color.White, Font = new Font("Microsoft YaHei UI", 28, FontStyle.Bold), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter };
        Controls.Add(text); Controls.Add(icon);
    }

    private void RegisterWarningIconClick()
    {
        var now = DateTime.Now;
        if ((now - _firstWarningIconClick).TotalSeconds > 5)
        {
            _firstWarningIconClick = now;
            _warningIconClicks = 0;
        }
        _warningIconClicks++;
        if (_warningIconClicks < 5) return;
        EmergencyDismissed?.Invoke(this, EventArgs.Empty);
    }
}

internal static class PasswordDialog
{
    public static string? Create()
    {
        using var form = CreateSetupForm();
        return form.ShowDialog() == DialogResult.OK ? form.Controls["password"]!.Text : null;
    }

    internal static Form CreateSetupForm()
    {
        var form = new Form { Text = "首次使用：设置密码", ClientSize = new Size(430, 250), StartPosition = FormStartPosition.CenterScreen, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, Font = new Font("Microsoft YaHei UI", 9), AutoScaleMode = AutoScaleMode.Dpi };
        var hint = new Label { Text = "请设置打开设置和退出程序的密码。\r\n仅防误操作，不替代系统登录保护。", Location = new Point(20, 18), AutoSize = true, MaximumSize = new Size(390, 0) };
        var password = new TextBox { Name = "password", Location = new Point(120, 80), Width = 280, UseSystemPasswordChar = true };
        var confirm = new TextBox { Name = "confirm", Location = new Point(120, 120), Width = 280, UseSystemPasswordChar = true };
        var error = new Label { Location = new Point(20, 158), AutoSize = true, ForeColor = Color.Firebrick };
        var save = new Button { Name = "save", Text = "设置并启动", Location = new Point(180, 200), Size = new Size(105, 30) };
        var cancel = new Button { Name = "cancel", Text = "取消", DialogResult = DialogResult.Cancel, Location = new Point(295, 200), Size = new Size(105, 30) };
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(password.Text) || password.Text != confirm.Text)
            {
                error.Text = "请输入非空密码，并确保两次输入一致。";
                return;
            }
            form.DialogResult = DialogResult.OK;
        };
        form.Controls.AddRange([hint, new Label { Text = "新密码", Location = new Point(20, 84), AutoSize = true }, password,
            new Label { Text = "确认密码", Location = new Point(20, 124), AutoSize = true }, confirm, error, save, cancel]);
        form.AcceptButton = save; form.CancelButton = cancel; form.Shown += (_, _) => password.Focus();
        return form;
    }

    public static bool Verify(string expected, string title)
    {
        using var form = new Form { Text = title, Size = new Size(360, 170), StartPosition = FormStartPosition.CenterScreen, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, Font = new Font("Microsoft YaHei UI", 9) };
        var label = new Label { Text = "请输入密码。", Location = new Point(24, 20), AutoSize = true };
        var input = new TextBox { Location = new Point(24, 52), Width = 294, UseSystemPasswordChar = true };
        var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Location = new Point(145, 92), Size = new Size(82, 30) };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Location = new Point(236, 92), Size = new Size(82, 30) };
        form.Controls.AddRange([label, input, ok, cancel]); form.AcceptButton = ok; form.CancelButton = cancel; form.Shown += (_, _) => input.Focus();
        return form.ShowDialog() == DialogResult.OK && input.Text == expected;
    }
}

internal static class NativeMethods
{
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] public static extern bool LockWorkStation();
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr handle);
}

internal static class TrayIcons
{
    public static Icon Create(bool enabled)
    {
        using var bitmap = new Bitmap(16, 16);
        using var graphics = Graphics.FromImage(bitmap);
        using var fill = new SolidBrush(enabled ? Color.ForestGreen : Color.DimGray);
        using var border = new Pen(Color.Black, 1);
        using var mark = new Pen(Color.White, 2);
        graphics.Clear(Color.Transparent);
        graphics.FillEllipse(fill, 1, 1, 14, 14);
        graphics.DrawEllipse(border, 1, 1, 14, 14);
        if (enabled)
        {
            graphics.DrawLine(mark, 4, 8, 7, 11);
            graphics.DrawLine(mark, 7, 11, 12, 5);
        }
        else
        {
            graphics.DrawLine(mark, 5, 5, 11, 11);
            graphics.DrawLine(mark, 11, 5, 5, 11);
        }
        var handle = bitmap.GetHicon();
        using var native = Icon.FromHandle(handle);
        var copy = (Icon)native.Clone();
        NativeMethods.DestroyIcon(handle);
        return copy;
    }
}
