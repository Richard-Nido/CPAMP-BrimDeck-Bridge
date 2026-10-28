namespace Bridge;

internal sealed class QuotaWindowRow : Panel
{
    private readonly CheckBox selected = new() { AutoSize = true, Font = UiStyle.Strong, ForeColor = UiStyle.Muted };
    private readonly Label remaining = new() { AutoSize = true, Font = UiStyle.Strong, ForeColor = UiStyle.Ink };
    private readonly QuotaTrack track = new();
    private readonly Label used = new() { AutoSize = true, Font = UiStyle.Small, ForeColor = UiStyle.Muted };
    private readonly Label reset = new() { AutoSize = true, Font = UiStyle.Small, ForeColor = UiStyle.Muted };
    private bool loading;
    public string WindowId { get; }
    public event EventHandler? SelectionEdited;
    private readonly AccountEditor editor;
    public QuotaWindowRow(AccountEditor editor, QuotaWindow window)
    {
        this.editor = editor; WindowId = window.Id; Height = 86; BackColor = Color.White;
        Controls.AddRange([selected, remaining, track, used, reset]);
        selected.CheckedChanged += (_, _) => { if (loading) return; if (selected.Checked && !editor.WindowIds.Contains(WindowId)) editor.WindowIds.Add(WindowId); if (!selected.Checked) editor.WindowIds.Remove(WindowId); SelectionEdited?.Invoke(this, EventArgs.Empty); };
        UpdateWindow(window, DateTimeOffset.UtcNow);
    }
    public void UpdateWindow(QuotaWindow w, DateTimeOffset now)
    {
        loading = true; selected.Text = UiStyle.WindowName(w.Name); selected.Checked = editor.WindowIds.Contains(w.Id); selected.AccessibleName = $"展示 {selected.Text} 窗口"; loading = false;
        bool known = w.Status == "known" && w.RemainingPercent.HasValue;
        remaining.Text = known ? $"剩余 {w.RemainingPercent:0.#}%" : w.Status == "invalid_percent" ? "额度数据异常" : "暂无额度";
        remaining.ForeColor = known ? UiStyle.Ink : UiStyle.Muted;
        used.Text = known ? $"已使用 {w.UsedPercent:0.#}%{(w.Stale ? "  ·  过期" : "")}" : w.Stale ? "过期" : "";
        reset.Text = UiStyle.ResetText(w.ResetAt, now); track.UpdateValue(known ? w.RemainingPercent : null, w.Stale); PerformLayout();
    }
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e); selected.Location = new Point(0, 10); remaining.Location = new Point(Math.Max(170, Width - remaining.Width), 10);
        track.SetBounds(0, 40, Width, 9); used.Location = new Point(0, 57); reset.Location = new Point(Math.Max(180, Width - reset.Width), 57);
    }
    protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); using var pen = new Pen(UiStyle.Line); e.Graphics.DrawLine(pen, 0, 0, Width, 0); }
    internal bool IsSelected { get => selected.Checked; set => selected.Checked = value; }
}

internal sealed class QuotaCard : Surface
{
    public AccountEditor Editor { get; }
    private readonly Label avatar = new() { Text = "C", TextAlign = ContentAlignment.MiddleCenter, Font = UiStyle.Heading, ForeColor = UiStyle.Accent, BackColor = Color.FromArgb(236, 241, 255), Size = new Size(42, 42) };
    private readonly Label name = new() { Font = UiStyle.Heading, ForeColor = UiStyle.Ink, AutoEllipsis = true, Height = 28 };
    private readonly Label provider = new() { Font = UiStyle.Small, ForeColor = UiStyle.Muted, Height = 22 };
    private readonly Label status = new() { AutoSize = true, Font = UiStyle.Small, ForeColor = UiStyle.Green, BackColor = Color.FromArgb(239, 250, 242), Padding = new Padding(6, 3, 6, 3) };
    private readonly DisplaySwitch show = new();
    private readonly TextBox alias = new() { PlaceholderText = "显示别名", Font = UiStyle.Body, MaxLength = 64 };
    private readonly NumericUpDown order = new() { Minimum = int.MinValue, Maximum = int.MaxValue, Font = UiStyle.Body, Width = 72 };
    private readonly Label aliasLabel = new() { Text = "别名", AutoSize = true, ForeColor = UiStyle.Muted, Font = UiStyle.Small };
    private readonly Label orderLabel = new() { Text = "顺序", AutoSize = true, ForeColor = UiStyle.Muted, Font = UiStyle.Small };
    private readonly Label empty = new() { Text = "暂无额度快照", ForeColor = UiStyle.Muted, Font = UiStyle.Body, AutoSize = true };
    private readonly Label updated = new() { ForeColor = UiStyle.Muted, Font = UiStyle.Small, Height = 20 };
    private readonly Dictionary<string, QuotaWindowRow> rows = [];
    private bool loading;
    private readonly ToolTip tooltip = new();
    public event EventHandler? Edited;
    public QuotaCard(AccountEditor editor)
    {
        Editor = editor; Width = 500; Margin = new Padding(8); Padding = new Padding(20);
        Controls.AddRange([avatar, name, provider, status, show, aliasLabel, alias, orderLabel, order, empty, updated]);
        loading = true; alias.Text = editor.Alias; order.Value = editor.Order; show.Checked = editor.Selected; loading = false;
        alias.TextChanged += (_, _) => { if (loading) return; editor.Alias = alias.Text; name.Text = Json.First(editor.Alias, editor.Name); Edited?.Invoke(this, EventArgs.Empty); };
        order.ValueChanged += (_, _) => { if (loading) return; editor.Order = (int)order.Value; Edited?.Invoke(this, EventArgs.Empty); };
        show.CheckedChanged += (_, _) => { if (loading) return; editor.Selected = show.Checked; Edited?.Invoke(this, EventArgs.Empty); };
        show.AccessibleName = "在 BrimDeck 展示此账号"; alias.AccessibleName = "账号显示别名"; order.AccessibleName = "显示顺序";
        UpdateData(DateTimeOffset.UtcNow);
    }
    public void UpdateData(DateTimeOffset now)
    {
        name.Text = Json.First(Editor.Alias, Editor.Name); provider.Text = Editor.Provider.ToUpperInvariant() + (Editor.Alias.Length > 0 ? " · " + Editor.Name : ""); provider.AutoEllipsis = true; avatar.Text = Editor.Provider.Length == 0 ? "?" : Editor.Provider[..1].ToUpperInvariant();
        status.Text = UiStyle.CredentialStatus(Editor.Status); status.ForeColor = Editor.Status is "active" or "ready" or "available" ? UiStyle.Green : UiStyle.Amber;
        updated.Text = "更新 " + Editor.Updated;
        tooltip.SetToolTip(name, Editor.Name); tooltip.SetToolTip(show, "在 BrimDeck 展示此账号");
        var ids = Editor.Windows.Select(w => w.Id).ToHashSet();
        foreach (var id in rows.Keys.Where(id => !ids.Contains(id)).ToArray()) { var row = rows[id]; Controls.Remove(row); row.Dispose(); rows.Remove(id); }
        foreach (var w in Editor.Windows)
        {
            if (!rows.TryGetValue(w.Id, out var row)) { row = new QuotaWindowRow(Editor, w); row.SelectionEdited += (_, _) => Edited?.Invoke(this, EventArgs.Empty); rows.Add(w.Id, row); Controls.Add(row); }
            row.UpdateWindow(w, now);
        }
        empty.Visible = Editor.Windows.Count == 0; Height = 166 + Math.Max(1, Editor.Windows.Count) * 86; PerformLayout();
    }
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e); if (name is null || Editor is null) return;
        int right = Width - 20;
        avatar.Location = new Point(20, 22); show.Location = new Point(right - show.Width, 22); status.Location = new Point(show.Left - status.Width - 12, 25);
        name.SetBounds(76, 20, Math.Max(120, status.Left - 84), 28); provider.SetBounds(76, 50, Width - 100, 22);
        aliasLabel.Location = new Point(20, 93); alias.SetBounds(61, 87, Math.Max(90, Width - 225), 30);
        order.Location = new Point(right - order.Width, 87); orderLabel.Location = new Point(order.Left - 41, 93);
        int y = 134;
        foreach (var window in Editor.Windows) if (rows.TryGetValue(window.Id, out var row)) { row.SetBounds(20, y, Width - 40, 86); y += 86; }
        empty.Location = new Point(20, 155); updated.SetBounds(20, Height - 28, Width - 40, 20);
    }
    internal void EditForTest(bool selected, string value, int position) { show.Checked = selected; alias.Text = value; order.Value = position; }
    internal QuotaWindowRow WindowForTest(string id) => rows[id];
    protected override void Dispose(bool disposing) { if (disposing) tooltip.Dispose(); base.Dispose(disposing); }
}
