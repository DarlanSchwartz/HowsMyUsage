using System.Runtime.InteropServices;
using System.Drawing.Drawing2D;
using Microsoft.Win32;

namespace Usage;

sealed class UsageWidget : Form
{
    readonly WidgetSettings settings = WidgetSettings.Load();
    readonly ClaudeUsageService claude = new();
    readonly CancellationTokenSource lifetime = new();
    readonly System.Windows.Forms.Timer timer = new() { Interval = 120000 };
    readonly Label[] values = new Label[3];
    readonly UsageToolTip tips = new();
    readonly PictureBox[] pictures = new PictureBox[3];
    readonly NotifyIcon tray = new();
    readonly List<Image> logos = [];
    readonly DesktopHost desktop = new();
    bool refreshing;
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public UsageWidget()
    {
        Text = "Usage";
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(432, 64);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(28, 30, 34);
        ForeColor = Color.WhiteSmoke;
        Font = new Font("Segoe UI", 9);
        DoubleBuffered = true;
        AccessibleName = "Usage: remaining Codex, Gemini and Claude limits";

        var menu = new ContextMenuStrip();
        menu.Items.Add("Show / hide widget", null, (_, _) => ToggleWidget());
        menu.Items.Add("Refresh", null, async (_, _) => await RefreshUsage());
        var alwaysOnTop = new ToolStripMenuItem("Always on top") { Checked = settings.AlwaysOnTop, CheckOnClick = true };
        alwaysOnTop.CheckedChanged += (_, _) =>
        {
            settings.AlwaysOnTop = alwaysOnTop.Checked;
            ApplyWindowMode();
            SavePosition();
        };
        menu.Items.Add(alwaysOnTop);
        var startup = new ToolStripMenuItem("Start with Windows") { Checked = StartupEnabled(), CheckOnClick = true };
        startup.CheckedChanged += (_, _) =>
        {
            try { SetStartup(startup.Checked); }
            catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            { tray.ShowBalloonTip(4000, "Usage", "Could not change startup settings.", ToolTipIcon.Error); }
        };
        menu.Items.Add(startup);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Close());
        ContextMenuStrip = menu;
        using (var stream = typeof(UsageWidget).Assembly.GetManifestResourceStream("Usage.assets.usage.ico")!)
        using (var source = new Icon(stream)) Icon = (Icon)source.Clone();
        tray.Icon = Icon;
        tray.Text = "Usage · Codex, Gemini and Claude";
        tray.ContextMenuStrip = menu;
        tray.Visible = true;
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ToggleWidget(); };

        string[] names = ["Codex", "Gemini", "Claude"];
        for (int i = 0; i < 3; i++)
        {
            int x = 16 + i * 150;
            using var logoStream = typeof(UsageWidget).Assembly.GetManifestResourceStream($"Usage.assets.{names[i].ToLowerInvariant()}.png")!;
            using var original = Image.FromStream(logoStream);
            var logo = new Bitmap(original);
            logos.Add(logo);
            var picture = new PictureBox { Image = logo, SizeMode = PictureBoxSizeMode.Zoom, Bounds = new Rectangle(x, 22, 28, 28), AccessibleName = names[i] };
            pictures[i] = picture;
            tips.Set(picture, names[i], null, null);
            Controls.Add(picture);
            values[i] = new PercentageLabel { Text = "—", Bounds = new Rectangle(x + 34, 16, 102, 40), Font = new Font("Segoe UI", 24, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter };
            Controls.Add(values[i]);
            tips.Set(values[i], names[i], null, null);
        }
        LayoutReadings();
        AttachDrag(this);
        timer.Tick += async (_, _) => await RefreshUsage();
        Shown += async (_, _) =>
        {
            var area = Screen.PrimaryScreen!.WorkingArea;
            Location = settings.X is int x && settings.Y is int y ? new Point(x, y) : new Point(area.Right - Width - 24, area.Bottom - Height - 24);
            var screen = Screen.FromRectangle(Bounds).WorkingArea;
            Location = new Point(Math.Clamp(Left, screen.Left, Math.Max(screen.Left, screen.Right - Width)), Math.Clamp(Top, screen.Top, Math.Max(screen.Top, screen.Bottom - Height)));
            ApplyWindowMode();
            timer.Start();
            await RefreshUsage();
            if (Environment.GetCommandLineArgs().Contains("--check-widget"))
            {
                Directory.CreateDirectory("artifacts");
                using var preview = new Bitmap(ClientSize.Width, ClientSize.Height);
                DrawToBitmap(preview, ClientRectangle);
                preview.Save("artifacts/widget-preview.png");
                tips.SavePreview(values[1], "artifacts/tooltip-preview.png");
                for (int i = 0; i < names.Length; i++)
                    tips.SavePreview(values[i], $"artifacts/tooltip-{names[i].ToLowerInvariant()}.png");
                menu.Show(this, new Point(0, Height));
                using (var menuPreview = new Bitmap(menu.Width, menu.Height))
                {
                    menu.DrawToBitmap(menuPreview, new Rectangle(Point.Empty, menu.Size));
                    menuPreview.Save("artifacts/context-menu.png");
                }
                menu.Close();
                bool originalMode = settings.AlwaysOnTop;
                var originalPosition = PointToScreen(Point.Empty);
                settings.AlwaysOnTop = true;
                ApplyWindowMode();
                bool topModePassed = TopMost && !desktop.IsAttached(this) && !ShowInTaskbar && PointToScreen(Point.Empty) == originalPosition;
                settings.AlwaysOnTop = false;
                ApplyWindowMode();
                bool desktopModePassed = !TopMost && desktop.IsAttached(this) && PointToScreen(Point.Empty) == originalPosition;
                settings.AlwaysOnTop = originalMode;
                ApplyWindowMode();
                File.WriteAllText("artifacts/widget-check.json", System.Text.Json.JsonSerializer.Serialize(new
                {
                    topModePassed, desktopModePassed, desktopAttached = desktop.IsAttached(this), topMost = TopMost, showInTaskbar = ShowInTaskbar,
                    logos = logos.Count, position = PointToScreen(Point.Empty), roundedCorners = Region != null && !Region.IsVisible(0, 0) && Region.IsVisible(Width / 2, Height / 2)
                }));
                Close();
            }
        };
    }

    void LayoutReadings()
    {
        if (values.Any(value => value == null)) return;
        float scale = DeviceDpi / 96f;
        int icon = (int)(28 * scale), gap = (int)(8 * scale), margin = (int)(18 * scale);
        int cellGap = (int)(28 * scale);
        var widths = values.Select(value => TextRenderer.MeasureText(value.Text, value.Font,
            Size.Empty, TextFormatFlags.NoPadding).Width + (int)(4 * scale)).ToArray();
        ClientSize = new Size(2 * margin + 2 * cellGap + widths.Sum() + 3 * (icon + gap), (int)(64 * scale));
        int x = margin;
        for (int i = 0; i < values.Length; i++)
        {
            pictures[i].Bounds = new Rectangle(x, (ClientSize.Height - icon) / 2, icon, icon);
            values[i].Bounds = new Rectangle(x + icon + gap, 0, widths[i], ClientSize.Height);
            x += icon + gap + widths[i] + cellGap;
        }
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        LayoutReadings();
    }

    GraphicsPath RoundedOutline(float inset = 0)
    {
        float diameter = Math.Min(28f * DeviceDpi / 96f, Math.Min(ClientSize.Width, ClientSize.Height) - 2 * inset);
        float right = ClientSize.Width - inset, bottom = ClientSize.Height - inset;
        var path = new GraphicsPath();
        path.AddArc(inset, inset, diameter, diameter, 180, 90);
        path.AddArc(right - diameter, inset, diameter, diameter, 270, 90);
        path.AddArc(right - diameter, bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(inset, bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        using var outline = RoundedOutline();
        var previous = Region;
        Region = new Region(outline);
        previous?.Dispose();
        Invalidate();
    }

    void ApplyWindowMode()
    {
        if (settings.AlwaysOnTop)
        {
            desktop.Detach(this);
            TopMost = true;
        }
        else
        {
            TopMost = false;
            desktop.Attach(this);
        }
    }

    void ToggleWidget()
    {
        if (Visible) Hide();
        else { Show(); ApplyWindowMode(); }
    }

    void AttachDrag(Control control)
    {
        if (control is Button) return;
        control.ContextMenuStrip = ContextMenuStrip;
        control.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            ReleaseCapture();
            SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero);
            SavePosition();
        };
        foreach (Control child in control.Controls) AttachDrag(child);
    }

    async Task RefreshUsage()
    {
        if (refreshing) return;
        refreshing = true;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(40));
        try
        {
            await Task.WhenAll(UpdateProvider(0, Providers.Codex), UpdateProvider(1, Providers.Antigravity),
                UpdateProvider(2, claude.Read));
        }
        finally { refreshing = false; }

        async Task UpdateProvider(int i, Func<CancellationToken, Task<Snapshot>> fetch)
        {
            var result = await Program.Safe(fetch, timeout.Token);
            if (IsDisposed || lifetime.IsCancellationRequested) return;
            values[i].Text = result.Remaining is double p ? $"{Math.Floor(p):0}%" : "—";
            values[i].ForeColor = result.Error != null ? Color.Silver : result.Remaining <= 10 ? Color.Salmon : Color.WhiteSmoke;
            var name = new[] { "Codex", "Gemini", "Claude" }[i];
            tips.Set(values[i], name, result, DateTimeOffset.Now);
            tips.Set(pictures[i], name, result, DateTimeOffset.Now);
            LayoutReadings();
            values[i].AccessibleName = $"{new[] { "Codex", "Gemini", "Claude" }[i]}: {values[i].Text} remaining";
        }
    }

    public static bool StartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue("UsageWidget") is string;
    }
    public static void SetStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
            var script = Path.Combine(AppContext.BaseDirectory, "startup.ps1");
            key.SetValue("UsageWidget", $"\"{powershell}\" -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File \"{script}\"");
        }
        else key.DeleteValue("UsageWidget", false);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        lifetime.Cancel(); timer.Stop(); timer.Dispose(); tips.Dispose(); claude.Dispose();
        tray.Visible = false; tray.Dispose();
        Icon?.Dispose();
        foreach (var logo in logos) logo.Dispose();
        ContextMenuStrip?.Dispose();
        base.OnFormClosed(e);
    }
    void SavePosition()
    {
        var point = PointToScreen(Point.Empty);
        settings.X = point.X; settings.Y = point.Y; settings.Save();
    }
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        SavePosition();
        base.OnFormClosing(e);
    }
    protected override bool ShowWithoutActivation => true;
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}

sealed class PercentageLabel : Label
{
    protected override void OnPaint(PaintEventArgs e) => TextRenderer.DrawText(e.Graphics, Text, Font,
        ClientRectangle, ForeColor, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine |
        TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
}
