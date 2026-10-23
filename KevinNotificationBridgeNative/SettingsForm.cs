using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace KevinNotificationBridge;

internal sealed class SettingsForm : Form
{
    private readonly AppSettings _settings;
    private readonly AppPaths _paths;
    private readonly SettingsStore _store;
    private readonly CheckBox _enabled = Check("开启总通知");
    private readonly CheckBox _completion = Check("任务完成通知");
    private readonly CheckBox _failure = Check("任务失败通知");
    private readonly CheckBox _linkGuard = Check("离开防护跟随通知");
    private readonly CheckBox _guard = Check("独立开启离开防护");
    private readonly CheckBox _camera = Check("触发时拍摄前置摄像头");
    private readonly CheckBox _lock = Check("连续三次告警（30 秒内）自动锁屏");
    private readonly TextBox _hotkey = new() { Width = 140 };
    private readonly ComboBox _watch = Choice("5", "10", "30", "60");
    private readonly ComboBox _alert = Choice("1", "2", "5", "10", "30");
    private readonly ComboBox _cooldown = Choice("30", "60", "120", "300");
    private readonly ComboBox _retention = Choice("10", "30", "100", "500");
    private readonly TextBox _webhook = new() { Width = 420, UseSystemPasswordChar = true };
    private readonly TextBox _warningText = new() { Multiline = true, ScrollBars = ScrollBars.Vertical };

    public SettingsForm(AppSettings settings, AppPaths paths, SettingsStore store)
    {
        _settings = settings;
        _paths = paths;
        _store = store;
        Text = "Kevin 通知桥接设置";
        ClientSize = new Size(820, 820);
        MinimumSize = new Size(720, 700);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9F);
        AutoScaleMode = AutoScaleMode.Dpi;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 8,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var index = 0; index < 7; index++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        root.Controls.Add(CreateNotificationGroup(), 0, 0);
        root.Controls.Add(CreateGuardGroup(), 0, 1);
        root.Controls.Add(CreateWarningTextGroup(), 0, 2);
        root.Controls.Add(CreateRuntimeGroup(), 0, 3);
        root.Controls.Add(CreateWebhookGroup(), 0, 4);
        root.Controls.Add(CreateButtonBar(), 0, 5);
        root.Controls.Add(new Label { Text = "Webhook 保存在当前用户环境变量中。设置密码仅用于避免误操作。", AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(4, 8, 4, 4) }, 0, 6);
        root.Controls.Add(CreateLogBox(), 0, 7);
        Controls.Add(root);

        _linkGuard.CheckedChanged += (_, _) => _guard.Enabled = !_linkGuard.Checked;
        LoadSettings();
    }

    private GroupBox CreateNotificationGroup()
    {
        var group = Group("通知设置");
        group.Controls.Add(Flow(_enabled, _completion, _failure));
        return group;
    }

    private GroupBox CreateGuardGroup()
    {
        var group = Group("离开防护");
        var content = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(10, 8, 10, 8) };
        content.Controls.Add(Flow(_linkGuard, _guard));
        content.Controls.Add(Flow(_camera, _lock));
        content.Controls.Add(Flow(Pair("报警间隔", _alert, "秒"), Pair("锁屏冷却", _cooldown, "秒")));
        var openPhotos = new Button { Text = "打开照片目录", AutoSize = true };
        openPhotos.Click += (_, _) => { Directory.CreateDirectory(_paths.CaptureRoot); Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_paths.CaptureRoot}\"") { UseShellExecute = true }); };
        content.Controls.Add(Flow(Pair("照片保留", _retention, "张"), openPhotos));
        group.Controls.Add(content);
        return group;
    }

    private GroupBox CreateRuntimeGroup()
    {
        var group = Group("快捷键与扫描");
        group.Controls.Add(Flow(Pair("快捷键", _hotkey), Pair("扫描间隔", _watch, "秒")));
        return group;
    }

    private GroupBox CreateWarningTextGroup()
    {
        var group = Group("警告文案");
        group.AutoSize = false;
        group.Height = 132;
        group.Padding = new Padding(12, 26, 12, 12);
        _warningText.Dock = DockStyle.Fill;
        _warningText.TextAlign = HorizontalAlignment.Center;
        group.Controls.Add(_warningText);
        return group;
    }

    private GroupBox CreateWebhookGroup()
    {
        var group = Group("Webhook");
        group.Controls.Add(Flow(new Label { Text = "更新（留空保持原配置）：", AutoSize = true, Margin = new Padding(4, 8, 6, 4) }, _webhook));
        return group;
    }

    private Control CreateButtonBar()
    {
        var save = new Button { Text = "保存设置", AutoSize = true };
        save.Click += (_, _) => SaveAndClose();
        var test = new Button { Text = "发送测试", AutoSize = true };
        test.Click += async (_, _) =>
        {
            using var notifier = new FeishuNotifier();
            var sent = await notifier.SendAsync(_settings, "✅ Codex 任务完成", "新程序测试", "原生 .NET 托盘程序测试成功。", _paths.LogPath);
            MessageBox.Show(sent ? "测试通知已被飞书接受，请查看群聊。" : "测试通知未发送成功，请检查通知开关、Webhook 和运行日志。", "Kevin 通知桥接", MessageBoxButtons.OK, MessageBoxIcon.Information);
        };
        var password = new Button { Text = "修改密码", AutoSize = true };
        password.Click += (_, _) => ChangePassword();
        return Flow(save, test, password);
    }

    private TextBox CreateLogBox() => new()
    {
        Dock = DockStyle.Fill,
        MinimumSize = new Size(0, 150),
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        Text = File.Exists(_paths.LogPath) ? string.Join(Environment.NewLine, File.ReadLines(_paths.LogPath).TakeLast(10)) : string.Empty,
        Margin = new Padding(0, 6, 0, 0),
    };

    private void LoadSettings()
    {
        _enabled.Checked = _settings.Enabled;
        _completion.Checked = _settings.CompletionNotifications;
        _failure.Checked = _settings.FailureNotifications;
        _linkGuard.Checked = _settings.AwayGuardLinkedToNotifications;
        _guard.Checked = _settings.AwayGuardEnabled;
        _guard.Enabled = !_linkGuard.Checked;
        _camera.Checked = _settings.CaptureCameraOnGuardAlert;
        _lock.Checked = _settings.LockWorkstationAfterRepeatedGuardAlerts;
        _warningText.Text = _settings.SecurityWarningText;
        _hotkey.Text = _settings.Hotkey;
        Select(_watch, _settings.WatchIntervalSeconds);
        Select(_alert, _settings.GuardAlertIntervalSeconds);
        Select(_cooldown, _settings.LockScreenCooldownSeconds);
        Select(_retention, _settings.CaptureRetentionCount);
    }

    private void SaveAndClose()
    {
        if (!string.IsNullOrWhiteSpace(_webhook.Text)) Environment.SetEnvironmentVariable("CODEX_FEISHU_WEBHOOK", _webhook.Text, EnvironmentVariableTarget.User);
        _settings.Enabled = _enabled.Checked;
        _settings.CompletionNotifications = _completion.Checked;
        _settings.FailureNotifications = _failure.Checked;
        _settings.AwayGuardLinkedToNotifications = _linkGuard.Checked;
        _settings.AwayGuardEnabled = _guard.Checked;
        _settings.CaptureCameraOnGuardAlert = _camera.Checked;
        _settings.LockWorkstationAfterRepeatedGuardAlerts = _lock.Checked;
        _settings.SecurityWarningText = string.IsNullOrWhiteSpace(_warningText.Text) ? "检测到异常操作\r\n请立即停止操作。" : _warningText.Text.Trim();
        _settings.Hotkey = _hotkey.Text.Trim();
        _settings.WatchIntervalSeconds = int.Parse(_watch.Text);
        _settings.GuardAlertIntervalSeconds = int.Parse(_alert.Text);
        _settings.LockScreenCooldownSeconds = int.Parse(_cooldown.Text);
        _settings.CaptureRetentionCount = int.Parse(_retention.Text);
        _store.Save(_settings);
        DialogResult = DialogResult.OK;
    }

    private void ChangePassword()
    {
        using var form = new Form { Text = "修改密码", ClientSize = new Size(390, 230), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, Font = new Font("Microsoft YaHei UI", 9F) };
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 2, RowCount = 4 };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var current = PasswordInput(); var newer = PasswordInput(); var confirm = PasswordInput();
        AddRow(table, 0, "当前密码：", current); AddRow(table, 1, "新密码：", newer); AddRow(table, 2, "确认新密码：", confirm);
        var save = new Button { Text = "保存新密码", AutoSize = true, Anchor = AnchorStyles.Right };
        save.Click += (_, _) =>
        {
            if (current.Text != _settings.AppPassword || string.IsNullOrWhiteSpace(newer.Text) || newer.Text != confirm.Text)
            {
                MessageBox.Show("密码不正确或两次输入不一致。", "修改密码", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _settings.AppPassword = newer.Text;
            _store.Save(_settings);
            form.DialogResult = DialogResult.OK;
        };
        table.Controls.Add(save, 1, 3); form.Controls.Add(table); form.ShowDialog();
    }

    private static void AddRow(TableLayoutPanel table, int row, string text, Control input)
    {
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 12, 8) }, 0, row);
        table.Controls.Add(input, 1, row);
    }

    private static GroupBox Group(string text) => new() { Text = text, Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8), Margin = new Padding(0, 0, 0, 8) };
    private static FlowLayoutPanel Flow(params Control[] controls)
    {
        var flow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, FlowDirection = FlowDirection.LeftToRight, Dock = DockStyle.Top, Margin = new Padding(0) };
        foreach (var control in controls) { control.Margin = new Padding(4); flow.Controls.Add(control); }
        return flow;
    }
    private static FlowLayoutPanel Pair(string label, Control control, string? suffix = null)
    {
        var items = new List<Control> { new Label { Text = label + "：", AutoSize = true, Margin = new Padding(0, 7, 4, 0) }, control };
        if (suffix is not null) items.Add(new Label { Text = suffix, AutoSize = true, Margin = new Padding(4, 7, 0, 0) });
        return Flow([.. items]);
    }
    private static CheckBox Check(string text) => new() { Text = text, AutoSize = true };
    private static ComboBox Choice(params string[] values) { var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 92 }; box.Items.AddRange(values); return box; }
    private static TextBox PasswordInput() => new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    private static void Select(ComboBox box, int value) => box.SelectedItem = value.ToString();
}
