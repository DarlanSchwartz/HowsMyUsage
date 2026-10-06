namespace Usage;

sealed class UsageToolTip : IDisposable
{
    record Content(string Title, string Body, string Footer);
    readonly ToolTip tip = new() { OwnerDraw = true, AutoPopDelay = 25000, InitialDelay = 350, ReshowDelay = 100 };
    readonly Dictionary<Control, Content> contents = [];
    readonly Font titleFont = new("Segoe UI", 11, FontStyle.Bold);
    readonly Font bodyFont = new("Segoe UI", 10);
    readonly Font footerFont = new("Segoe UI", 9);
    const TextFormatFlags Flags = TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding;

    public UsageToolTip()
    {
        tip.Popup += (_, e) =>
        {
            if (e.AssociatedControl is { } control && contents.TryGetValue(control, out var content))
                e.ToolTipSize = Measure(content, control.DeviceDpi);
        };
        tip.Draw += (_, e) =>
        {
            if (e.AssociatedControl is { } control && contents.TryGetValue(control, out var content))
                Draw(e.Graphics, e.Bounds, content, control.DeviceDpi);
        };
    }

    public void Set(Control control, string name, Snapshot? snapshot, DateTimeOffset? checkedAt)
    {
        string body = "Checking usage…";
        if (snapshot != null)
        {
            var groups = snapshot.Quotas.GroupBy(q => (q.Remaining, q.Reset)).ToArray();
            body = string.Join("\n\n", groups.Select(group =>
                $"{group.Key.Remaining:0.#}% remaining" +
                (group.Key.Reset is { } reset ? $"  ·  Resets {reset.LocalDateTime:MMM d, HH:mm}" : "") +
                "\n" + (groups.Length == 1 && group.Count() > 3 ? $"All {group.Count()} models" : string.Join(", ", group.Select(q => q.Name)))));
            if (snapshot.Error != null)
                body += (body.Length > 0 ? "\n\n" : "") +
                    (snapshot.MeasuredAt is { } captured ? $"Cached reading · {captured.LocalDateTime:MMM d, HH:mm}\nLive refresh unavailable." : snapshot.Error);
        }
        contents[control] = new(name, body, checkedAt is { } time ? $"Last checked {time.LocalDateTime:HH:mm}" : "Waiting for first check");
        tip.SetToolTip(control, name + "\n" + body + "\n" + contents[control].Footer);
    }

    Size Measure(Content content, int dpi)
    {
        int pad = 16 * dpi / 96, width = 350 * dpi / 96;
        int bodyHeight = TextRenderer.MeasureText(content.Body, bodyFont, new Size(width - 2 * pad, 0), Flags).Height;
        return new Size(width, bodyHeight + 2 * pad + 60 * dpi / 96);
    }

    void Draw(Graphics graphics, Rectangle bounds, Content content, int dpi)
    {
        int pad = 16 * dpi / 96, heading = 30 * dpi / 96, footer = 30 * dpi / 96;
        graphics.Clear(Color.FromArgb(35, 37, 42));
        using var border = new Pen(Color.FromArgb(70, 73, 80));
        graphics.DrawRectangle(border, 0, 0, bounds.Width - 1, bounds.Height - 1);
        TextRenderer.DrawText(graphics, content.Title, titleFont, new Rectangle(pad, pad, bounds.Width - 2 * pad, heading), Color.WhiteSmoke, Flags);
        TextRenderer.DrawText(graphics, content.Body, bodyFont, new Rectangle(pad, pad + heading, bounds.Width - 2 * pad, bounds.Height - 2 * pad - heading - footer), Color.FromArgb(210, 214, 222), Flags);
        int y = bounds.Height - pad - footer;
        graphics.DrawLine(border, pad, y + 4, bounds.Width - pad, y + 4);
        TextRenderer.DrawText(graphics, content.Footer, footerFont, new Rectangle(pad, y + 13, bounds.Width - 2 * pad, footer), Color.FromArgb(150, 159, 174), Flags);
    }

    public void SavePreview(Control control, string path)
    {
        var content = contents[control];
        using var bitmap = new Bitmap(Measure(content, control.DeviceDpi).Width, Measure(content, control.DeviceDpi).Height);
        bitmap.SetResolution(control.DeviceDpi, control.DeviceDpi);
        using var graphics = Graphics.FromImage(bitmap);
        Draw(graphics, new Rectangle(Point.Empty, bitmap.Size), content, control.DeviceDpi);
        bitmap.Save(path);
    }

    public void Dispose()
    {
        tip.Dispose(); titleFont.Dispose(); bodyFont.Dispose(); footerFont.Dispose();
    }
}
