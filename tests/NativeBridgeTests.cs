using System.Drawing.Imaging;
using System.Net;
using System.Text.Json;
using KevinNotificationBridge;

internal static class NativeBridgeTests
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var root = Path.GetFullPath(args.Length > 0 ? args[0] : ".tmp/native-tests");
            Directory.CreateDirectory(root);
            var runRoot = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(runRoot);
            foreach (var result in AwayGuardInputTests.Run()) Console.WriteLine("PASS: " + result);
            TestSettings(Path.Combine(runRoot, "settings"));
            TestSessions(Path.Combine(runRoot, "sessions"));
            TestNotifications(Path.Combine(runRoot, "notifications")).GetAwaiter().GetResult();
            if (args.Contains("--render-docs")) RenderDocs(Path.Combine(root, "screenshots"), Path.Combine(runRoot, "preview"));
            Console.WriteLine("PASS: Native bridge checks complete (synthetic settings, sessions and HTTP only).");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL: " + ex.Message);
            return 1;
        }
    }

    private static void TestSettings(string root)
    {
        var paths = new AppPaths(root);
        var store = new SettingsStore(paths);
        var settings = store.Load();
        Expect(string.IsNullOrEmpty(settings.AppPassword), "First use must have no shared default password.");
        Expect(!settings.AwayGuardEnabled && !settings.AwayGuardLinkedToNotifications &&
            !settings.CaptureCameraOnGuardAlert && !settings.LockWorkstationAfterRepeatedGuardAlerts,
            "Guard, camera and lock must be opt-in.");
        var fixturePassword = Guid.NewGuid().ToString("N");
        settings.AppPassword = fixturePassword;
        settings.Hotkey = "Ctrl+Alt+N";
        store.Save(settings);
        Expect(store.Load().AppPassword == fixturePassword && store.Load().Hotkey == "Ctrl+Alt+N",
            "Existing user settings must survive a load/save.");
        Console.WriteLine("PASS: Empty first-use password, opt-in guard and existing settings round trip");
    }

    private static void TestSessions(string root)
    {
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "rollout-demo.jsonl");
        var header = JsonSerializer.Serialize(new { type = "session_meta", payload = new { cwd = "C:/Projects/demo" } });
        var historical = Event(new { type = "task_complete", last_agent_message = "historical" });
        File.WriteAllText(file, header + "\n" + historical + "\n");
        var monitor = new SessionMonitor(root);
        Expect(!monitor.ReadNewEvents().Any(), "First scan must skip historical events.");
        var completed = Event(new { type = "task_complete", last_agent_message = "合成完成摘要" });
        File.AppendAllText(file, completed + "\n");
        var result = monitor.ReadNewEvents().Single();
        Expect(result.Kind == BridgeEventKind.Completed && result.Project == "C:/Projects/demo" &&
            result.Message == "合成完成摘要", "Completion must retain session metadata and Unicode text.");
        File.AppendAllText(file, completed + "\n");
        Expect(!monitor.ReadNewEvents().Any(), "Repeated events must not be resent.");
        File.AppendAllText(file, Event(new { type = "user_message", message = "explain model at capacity" }) + "\n" +
            Event(new { type = "turn_aborted", reason = "interrupted" }) + "\nnot-json\n");
        Expect(!monitor.ReadNewEvents().Any(), "Conversation text, deliberate interruption and malformed input must not alert.");
        File.AppendAllText(file, Event(new { type = "task_complete", last_agent_message = "Explained model at capacity" }) + "\n");
        Expect(monitor.ReadNewEvents().Single().Kind == BridgeEventKind.Completed,
            "A successful summary quoting capacity must remain a completion.");
        File.AppendAllText(file, Event(new { type = "task_complete", error = new { message = "synthetic failure" } }) + "\n" +
            Event(new { type = "error", message = "model at capacity" }) + "\n");
        var failures = monitor.ReadNewEvents().ToArray();
        Expect(failures.Length == 2 && failures.All(item => item.Kind == BridgeEventKind.Failed),
            "Task errors and capacity events must produce failure notifications.");
        Console.WriteLine("PASS: Incremental completion/error parsing, metadata, deduplication and ignored conversation text");
    }

    private static async Task TestNotifications(string root)
    {
        Directory.CreateDirectory(root);
        var logPath = Path.Combine(root, "notifications.log");
        var handler = new FakeHttpHandler();
        using var notifier = new FeishuNotifier(handler);
        var settings = new AppSettings();
        const string destination = "https://example.invalid/demo-notification";
        Expect(!await notifier.SendAsync(settings, "demo", "demo", "demo", logPath, null) && handler.Calls == 0,
            "Missing Webhook must not make a network request.");
        settings.Enabled = false;
        Expect(!await notifier.SendAsync(settings, "demo", "demo", "demo", logPath, destination) && handler.Calls == 0,
            "Disabled notifications must not send.");
        settings.Enabled = true;
        Expect(await notifier.SendAsync(settings, "合成通知", "demo", new string('测', 500), logPath, destination),
            "Feishu code zero must be accepted.");
        using (var body = JsonDocument.Parse(handler.Body!))
        {
            var text = body.RootElement.GetProperty("content").GetProperty("text").GetString()!;
            Expect(text.Contains("合成通知") && !text.Contains(new string('测', 241)), "Notification must preserve Unicode and bound the summary.");
        }
        handler.Reply = "{\"code\":9499,\"msg\":\"synthetic error\"}";
        Expect(!await notifier.SendAsync(settings, "demo", "demo", "demo", logPath, destination),
            "HTTP 200 with a Feishu error must not be called success.");
        handler.Status = HttpStatusCode.ServiceUnavailable;
        Expect(!await notifier.SendAsync(settings, "demo", "demo", "demo", logPath, destination), "HTTP failures must be reported.");
        Console.WriteLine("PASS: Missing/disabled Webhook, Unicode payload, bounded summary, HTTP failure and Feishu API failure");
    }

    private static void RenderDocs(string output, string previewRoot)
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Directory.CreateDirectory(output);
        using (var form = PasswordDialog.CreateSetupForm())
        {
            Render(form, Path.Combine(output, "first-use.png"));
            ShowOffscreen(form);
            var password = form.Controls["password"]!;
            var confirm = form.Controls["confirm"]!;
            var save = (Button)form.Controls["save"]!;
            save.PerformClick();
            Expect(form.DialogResult == DialogResult.None, "First-use setup must reject empty input.");
            password.Text = Guid.NewGuid().ToString("N");
            confirm.Text = password.Text + "mismatch";
            save.PerformClick();
            Expect(form.DialogResult == DialogResult.None, "First-use setup must reject mismatched input.");
            confirm.Text = password.Text;
            save.PerformClick();
            Expect(form.DialogResult == DialogResult.OK, "First-use setup must accept matching input.");
        }
        using (var form = PasswordDialog.CreateSetupForm())
        {
            ShowOffscreen(form);
            ((Button)form.Controls["cancel"]!).PerformClick();
            Expect(form.DialogResult == DialogResult.Cancel, "First-use setup must allow cancellation.");
        }
        Console.WriteLine("PASS: First-use UI rejects empty/mismatched input, accepts confirmation and supports cancellation");
        var paths = new AppPaths(previewRoot);
        var settings = new AppSettings();
        using (var form = new SettingsForm(settings, paths, new SettingsStore(paths)) { ClientSize = new Size(820, 1040) })
            Render(form, Path.Combine(output, "settings.png"));
        using (var form = new SecurityWarningForm("演示画面 · 离开防护提醒\r\n检测到持续鼠标设备移动。\r\n此预览不会通知、拍照或锁屏。"))
        {
            Render(form, Path.Combine(output, "away-guard.png"));
        }
        Console.WriteLine("PASS: Documentation images rendered from forms with synthetic data (no desktop capture)");
    }

    private static void Render(Form form, string path)
    {
        // Create child handles off screen; DrawToBitmap on an unshown form omits its children.
        ShowOffscreen(form);
        form.PerformLayout();
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
        bitmap.Save(path, ImageFormat.Png);
        form.Hide();
    }

    private static void ShowOffscreen(Form form)
    {
        form.ShowInTaskbar = false;
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-32000, -32000);
        form.Show();
        Application.DoEvents();
    }

    private static string Event<T>(T payload) => JsonSerializer.Serialize(new { type = "event_msg", payload });
    private static void Expect(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? Body { get; private set; }
        public string Reply { get; set; } = "{\"code\":0,\"msg\":\"success\"}";
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(Status) { Content = new StringContent(Reply) };
        }
    }
}
