using System.Drawing.Drawing2D;

namespace Bridge;

internal static class UiStyle
{
    public static readonly Color Canvas = Color.FromArgb(246, 248, 252), Ink = Color.FromArgb(46, 60, 78), Muted = Color.FromArgb(109, 122, 141), Line = Color.FromArgb(230, 235, 243);
    public static readonly Color Accent = Color.FromArgb(76, 116, 247), Green = Color.FromArgb(86, 190, 103), Amber = Color.FromArgb(228, 157, 41), Red = Color.FromArgb(218, 82, 82);
    public static readonly Font Body = new("Microsoft YaHei UI", 10), Strong = new("Microsoft YaHei UI", 10, FontStyle.Bold), Heading = new("Microsoft YaHei UI", 12, FontStyle.Bold), Title = new("Microsoft YaHei UI", 20, FontStyle.Bold), Number = new("Microsoft YaHei UI", 21, FontStyle.Bold), Small = new("Microsoft YaHei UI", 9);
    public static GraphicsPath Round(RectangleF bounds, float radius)
    {
        float d = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        var p = new GraphicsPath();
        p.AddArc(bounds.Left, bounds.Top, d, d, 180, 90); p.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
        p.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90); p.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90); p.CloseFigure(); return p;
    }
    public static string Compact(long n) => n >= 1_000_000_000 ? $"{n / 1_000_000_000m:0.##}B" : n >= 1_000_000 ? $"{n / 1_000_000m:0.##}M" : n >= 1_000 ? $"{n / 1_000m:0.##}K" : n.ToString("N0");
    public static string WindowName(string name) => name.Replace("Week", "周限额").Replace("Month", "月限额").Replace(" · codex_main", "").Replace("claude_gpt", "Claude / GPT").Replace("gemini", "Gemini");
    public static string CredentialStatus(string status) => status switch { "ready" or "active" or "available" => "可用", "disabled" => "已禁用", "unavailable" => "暂不可用", "missing_identity_reselect_required" => "已移除", "unknown" => "待确认", _ => status };
    public static string ResetText(DateTimeOffset? reset, DateTimeOffset now)
    {
        if (reset is null) return "";
        var left = reset.Value - now;
        if (left <= TimeSpan.Zero) return "等待更新";
        if (left.TotalDays >= 1) return $"{(int)left.TotalDays} 天后";
        if (left.TotalHours >= 1) return $"{(int)left.TotalHours} 小时后";
        return $"{Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))} 分钟后";
    }
}

internal class Surface : Panel
{
    public Color BorderColor { get; set; } = UiStyle.Line;
    public Surface() { DoubleBuffered = true; BackColor = Color.White; Padding = new Padding(18); ResizeRedraw = true; }
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? UiStyle.Canvas); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = UiStyle.Round(new RectangleF(.5f, .5f, Width - 1, Height - 1), 12);
        using var fill = new SolidBrush(BackColor); using var border = new Pen(BorderColor);
        e.Graphics.FillPath(fill, path); e.Graphics.DrawPath(border, path);
    }
}
internal sealed class MetricTile : Surface
{
    private readonly Label title = new() { AutoSize = true, ForeColor = UiStyle.Muted, Font = UiStyle.Body, Location = new Point(18, 16) };
    private readonly Label value = new() { AutoSize = false, AutoEllipsis = true, Font = UiStyle.Number, ForeColor = UiStyle.Ink };
    public MetricTile(string caption) { Height = 110; Dock = DockStyle.Fill; Margin = new Padding(6); title.Text = caption; Controls.Add(title); Controls.Add(value); value.Width = Width - 32; }
    public void Set(string text, Color? color = null) { if (value.Text != text) value.Text = text; value.ForeColor = color ?? UiStyle.Ink; }
    protected override void OnLayout(LayoutEventArgs e) { base.OnLayout(e); if (value is not null) value.SetBounds(16, 45, Math.Max(1, Width - 32), Math.Max(44, Height - 53)); }
    internal int ValueWidth => value.Width;
}
internal sealed class QuotaTrack : Control
{
    private decimal? remaining;
    private bool stale;
    public QuotaTrack() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true); Height = 10; AccessibleRole = AccessibleRole.ProgressBar; }
    public void UpdateValue(decimal? value, bool isStale) { if (remaining == value && stale == isStale) return; remaining = value; stale = isStale; AccessibleDescription = value.HasValue ? $"剩余额度 {value:0.#}%" : "暂无额度"; Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var track = UiStyle.Round(new RectangleF(0, 0, Math.Max(1, Width), Height), 5); using var background = new SolidBrush(Color.FromArgb(241, 243, 247));
        e.Graphics.FillPath(background, track);
        if (remaining is not { } n || n <= 0) return;
        float w = Math.Max(1, Width * (float)Math.Clamp(n, 0, 100) / 100);
        using var fill = new SolidBrush(stale ? Color.FromArgb(164, 184, 170) : n <= 10 ? UiStyle.Red : n <= 25 ? UiStyle.Amber : UiStyle.Green);
        using var progress = UiStyle.Round(new RectangleF(0, 0, w, Height), 5); e.Graphics.FillPath(fill, progress);
    }
}
internal sealed class DisplaySwitch : CheckBox
{
    public DisplaySwitch() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true); Size = new Size(48, 28); Cursor = Cursors.Hand; }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = UiStyle.Round(new RectangleF(1, 2, 44, 24), 12); using var fill = new SolidBrush(Checked ? UiStyle.Accent : Color.FromArgb(211, 219, 231));
        e.Graphics.FillPath(fill, path); e.Graphics.FillEllipse(Brushes.White, Checked ? 24 : 4, 5, 18, 18);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, ClientRectangle);
    }
}
