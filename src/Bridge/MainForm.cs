using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace Bridge;

public static class Scripts
{
    public static string Read(string name)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(assembly.GetManifestResourceNames().Single(n => n.EndsWith(name, StringComparison.Ordinal)) )!;
        using var reader = new StreamReader(stream); return reader.ReadToEnd();
    }
    public static string Account(string id)
    {
        if (id.Length != 64 || id.Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException("安全账号标识无效");
        return Read("BrimDeck-CPA-Account.template.js").Replace("__ACCOUNT_ID__", id);
    }
    public static string DirectAccount(string name, string email = "", string workspace = "", bool snapshots = false)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("账号凭证文件名缺失");
        var script = Read("BrimDeck-CPA-Direct-Codex.js").Replace("/*ACCOUNT*/{ name: \"请填写凭证文件名.json\", email: \"\", workspace: \"\" }", "/*ACCOUNT*/" + JsonSerializer.Serialize(new { name, email, workspace }));
        return snapshots ? script.Replace("const QUOTA_SOURCE = \"official\";", "const QUOTA_SOURCE = \"cpamp\";") : script;
    }
}
public sealed class AccountEditor
{
    public string Id { get; set; } = "";
    public bool Selected { get; set; }
    public string Name { get; set; } = "";
    public string Alias { get; set; } = "";
    public string Provider { get; set; } = "";
    public int Order { get; set; }
    public string Preview { get; set; } = "";
    public string Status { get; set; } = "";
    public string Updated { get; set; } = "";
    public string CredentialName { get; set; } = "";
    public string MemberEmail { get; set; } = "";
    public string WorkspaceId { get; set; } = "";
    public string? RefreshError { get; set; }
    public List<string> WindowIds { get; set; } = [];
    public List<QuotaWindow> Windows { get; set; } = [];
}
public sealed class MainForm : Form
{
    private readonly BridgeRuntime runtime;
    private readonly ConfigStore store;
    private readonly NotifyIcon tray;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 60_000 };
    private readonly TabControl tabs = new() { Dock = DockStyle.Fill, DrawMode = TabDrawMode.OwnerDrawFixed, ItemSize = new Size(160, 38), SizeMode = TabSizeMode.Fixed };
    private readonly Label status = new() { AutoSize = true, MaximumSize = new Size(1060, 0), Padding = new Padding(16), ForeColor = UiStyle.Muted };
    private readonly Label summary = new() { AutoSize = true, MaximumSize = new Size(1060, 0), Padding = new Padding(16), Font = UiStyle.Body, ForeColor = UiStyle.Muted };
    private readonly FlowLayoutPanel cardsPanel = new() { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = true, Padding = new Padding(0), BackColor = UiStyle.Canvas };
    private readonly Dictionary<string, QuotaCard> cards = [];
    private readonly Dictionary<string, MetricTile> metrics = [];
    private readonly Label accountHint = new() { AutoSize = true, ForeColor = UiStyle.Muted, Padding = new Padding(8) };
    private readonly TextBox search = new() { Width = 240, PlaceholderText = "搜索账号或别名" };
    private readonly ComboBox providers = new() { Width = 150, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox dataDir = new(), database = new(), cpamp = new(), adminKey = new() { UseSystemPasswordChar = true };
    private readonly NumericUpDown port = new() { Minimum = 1024, Maximum = 65535 }, statsSeconds = new() { Minimum = 15, Maximum = 3600 }, quotaSeconds = new() { Minimum = 30, Maximum = 7200 }, staleMinutes = new() { Minimum = 1, Maximum = 1440 };
    private readonly ComboBox mode = new() { DropDownStyle = ComboBoxStyle.DropDownList }, timezone = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox quotaSource = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox startup = new() { Text = "登录 Windows 时启动到托盘", AutoSize = true };
    private readonly ComboBox accountScripts = new() { Width = 380, DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = "Name", ValueMember = "Id" };
    private readonly Label bridgeAddress = new() { AutoSize = true };
    private readonly Dictionary<string, AccountEditor> editors = [];
    private DateTimeOffset loadedInventory;
    private bool exiting, saving, filtering, dirty;
    private int renderQueued;
    private readonly bool startHidden;
    public MainForm(BridgeRuntime runtime, ConfigStore store, bool startHidden)
    {
        this.runtime = runtime; this.store = store; this.startHidden = startHidden;
        Text = "CPAMP–BrimDeck Bridge"; Font = UiStyle.Body; ForeColor = UiStyle.Ink; Size = new Size(1180, 820); MinimumSize = new Size(980, 690); StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi; BackColor = UiStyle.Canvas; Icon = SystemIcons.Application;
        tabs.DrawItem += (_, e) => { var area = e.Bounds; bool active = e.Index == tabs.SelectedIndex; using var fill = new SolidBrush(active ? Color.White : UiStyle.Canvas); e.Graphics.FillRectangle(fill, area); TextRenderer.DrawText(e.Graphics, tabs.TabPages[e.Index].Text, UiStyle.Strong, area, active ? UiStyle.Accent : UiStyle.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter); if (active) { using var pen = new Pen(UiStyle.Accent, 3); e.Graphics.DrawLine(pen, area.Left + 20, area.Bottom - 2, area.Right - 20, area.Bottom - 2); } };
        Controls.Add(tabs);
        BuildHome(); BuildAccounts(); BuildSettings(); BuildIntegration();
        var menu = new ContextMenuStrip(); menu.Items.Add("打开设置", null, (_, _) => Restore());
        menu.Items.Add("立即刷新", null, async (_, _) => await RefreshNow()); menu.Items.Add("退出", null, (_, _) => { exiting = true; Close(); });
        tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "CPAMP–BrimDeck Bridge", Visible = true, ContextMenuStrip = menu };
        tray.DoubleClick += (_, _) => Restore();
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) Hide(); };
        FormClosing += (_, e) => { if (!exiting && e.CloseReason is not (CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing)) { e.Cancel = true; Hide(); tray.ShowBalloonTip(2000, Text, "程序继续在托盘运行；彻底停止请右键托盘图标选择退出。", ToolTipIcon.Info); } };
        FormClosed += (_, _) => { runtime.StateChanged -= RuntimeChanged; timer.Stop(); timer.Dispose(); tray.Visible = false; tray.Dispose(); };
        timer.Tick += (_, _) => RenderState();
        runtime.StateChanged += RuntimeChanged;
        VisibleChanged += (_, _) => { if (Visible && !exiting) { timer.Start(); RenderState(); } else timer.Stop(); };
        tabs.SelectedIndexChanged += (_, _) => { if (tabs.SelectedIndex == 1) FilterAccounts(); RenderState(); };
        Shown += async (_, _) => { if (startHidden) Hide(); await runtime.Start(); if (Visible) timer.Start(); RenderState(); };
    }
    private TabPage Page(string name) { var p = new TabPage(name) { Padding = new Padding(14), BackColor = BackColor, AutoScroll = true }; tabs.TabPages.Add(p); return p; }
    private static Button Button(string text, EventHandler action)
    {
        bool primary = text.StartsWith("保存") || text.StartsWith("立即刷新");
        var b = new Button { Text = text, AutoSize = true, Padding = new Padding(12, 7, 12, 7), Margin = new Padding(5), FlatStyle = FlatStyle.Flat, BackColor = primary ? UiStyle.Accent : Color.White, ForeColor = primary ? Color.White : UiStyle.Ink, Cursor = Cursors.Hand }; b.FlatAppearance.BorderColor = primary ? UiStyle.Accent : UiStyle.Line; b.Click += action; return b;
    }
    private static FlowLayoutPanel Flow() => new() { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(4) };
    private void BuildHome()
    {
        var p = Page("运行状态"); var panel = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(6) };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.Controls.Add(new Label { Text = "用量概览", Font = UiStyle.Title, AutoSize = true, Padding = new Padding(12, 14, 12, 4) });
        panel.Controls.Add(status);
        var tiles = new TableLayoutPanel { Dock = DockStyle.Top, Height = 252, ColumnCount = 4, RowCount = 2, Margin = new Padding(6) };
        for (int i = 0; i < 4; i++) tiles.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        for (int i = 0; i < 2; i++) tiles.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        string[] titles = ["今日 Token", "7天 Token", "今日费用", "7天费用", "今日请求", "成功请求", "失败请求", "成功率"];
        for (int i = 0; i < titles.Length; i++) { var tile = new MetricTile(titles[i]); tile.Set("—"); metrics.Add(titles[i], tile); tiles.Controls.Add(tile, i % 4, i / 4); }
        panel.Controls.Add(tiles); panel.Controls.Add(summary);
        var actions = Flow(); actions.Controls.Add(Button("立即刷新 / 测试数据源", async (_, _) => await RefreshNow())); actions.Controls.Add(Button("最小化到托盘", (_, _) => Hide()));
        panel.Controls.Add(actions);
        panel.Controls.Add(new Label { Text = "关闭窗口后继续在托盘运行。右键托盘图标可退出。", AutoSize = true, Padding = new Padding(16), ForeColor = UiStyle.Muted });
        p.Controls.Add(panel);
    }
    private void BuildAccounts()
    {
        var p = Page("账号管理");
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var filter = Flow(); providers.Items.Add("全部 Provider"); providers.SelectedIndex = 0;
        filter.Controls.Add(search); filter.Controls.Add(providers);
        filter.Controls.Add(new Label { Text = "账号额度", AutoSize = true, Font = UiStyle.Heading, Padding = new Padding(14, 3, 6, 3), ForeColor = UiStyle.Ink });
        filter.Controls.Add(new Label { Text = "开关控制 BrimDeck 展示", AutoSize = true, Font = UiStyle.Small, ForeColor = UiStyle.Muted, Padding = new Padding(12, 5, 0, 0) });
        search.TextChanged += (_, _) => FilterAccounts(); providers.SelectedIndexChanged += (_, _) => { if (!filtering) FilterAccounts(); };
        cardsPanel.ClientSizeChanged += (_, _) => LayoutCards();
        var actions = Flow(); actions.Controls.Add(Button("保存账号选择", async (_, _) => await SaveChoices()));
        actions.Controls.Add(Button("刷新额度", async (_, _) => await RefreshNow())); actions.Controls.Add(accountHint);
        layout.Controls.Add(filter, 0, 0); layout.Controls.Add(cardsPanel, 0, 1); layout.Controls.Add(actions, 0, 2); p.Controls.Add(layout);
    }
    private void FilterAccounts()
    {
        if (tabs.SelectedIndex != 1 || filtering) return;
        filtering = true; cardsPanel.SuspendLayout();
        try
        {
            string query = search.Text.Trim(), provider = providers.SelectedItem?.ToString() ?? "全部 Provider";
            var visible = editors.Values.Where(a => (provider == "全部 Provider" || a.Provider == provider) && (a.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || a.Alias.Contains(query, StringComparison.OrdinalIgnoreCase))).OrderBy(a => a.Order).ThenBy(a => a.Id).ToList();
            var ids = visible.Select(a => a.Id).ToHashSet();
            foreach (var id in cards.Keys.Where(id => !ids.Contains(id)).ToArray()) { var card = cards[id]; cardsPanel.Controls.Remove(card); card.Dispose(); cards.Remove(id); }
            int position = 0;
            foreach (var editor in visible)
            {
                if (!cards.TryGetValue(editor.Id, out var card))
                {
                    card = new QuotaCard(editor); card.Edited += (_, _) => { dirty = true; UpdateAccountHint(); };
                    cards.Add(editor.Id, card); cardsPanel.Controls.Add(card);
                }
                card.UpdateData(DateTimeOffset.UtcNow); cardsPanel.Controls.SetChildIndex(card, position++);
            }
            UpdateAccountHint(); LayoutCards();
        }
        finally { filtering = false; cardsPanel.ResumeLayout(); }
    }
    private void LayoutCards()
    {
        if (cards.Count == 0) return;
        // Reserve scrollbar width so adding a row never alternates between one and two columns.
        int width = Math.Max(400, cardsPanel.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 4);
        int columns = width >= 920 ? 2 : 1;
        int cardWidth = width / columns - 16;
        cardsPanel.SuspendLayout();
        foreach (var card in cards.Values) if (card.Width != cardWidth) card.Width = cardWidth;
        cardsPanel.ResumeLayout();
    }
    private void UpdateAccountHint() => accountHint.Text = $"已选择 {editors.Values.Count(a => a.Selected)} 个账号 · {(runtime.Config.QuotaSource == "official" ? "官方额度（仅勾选 Codex）" : "CPAMP 快照")} · 每 {runtime.Config.QuotaSeconds} 秒刷新{(dirty ? " · 有未保存更改" : "")}";
    private BridgeConfig Clone() => JsonSerializer.Deserialize<BridgeConfig>(JsonSerializer.Serialize(runtime.Config, Json.Options), Json.Options)!;
    private async Task SaveChoices()
    {
        try
        {
            var config = Clone();
            config.Accounts = editors.Values.Select(a => new AccountChoice { Id = a.Id, Selected = a.Selected, Alias = a.Alias.Trim(), Order = a.Order, Windows = [.. a.WindowIds] }).ToList();
            store.Save(config); await runtime.UpdateConfig(config); dirty = false; UpdateAccountHint(); FilterAccounts(); UpdateAccountScriptChoices(); await RefreshNow();
            MessageBox.Show(this, "账号、别名、窗口和顺序已保存。", Text);
        }
        catch (Exception) { MessageBox.Show(this, "保存失败，请检查别名长度和显示顺序。", Text); }
    }
    private void BuildSettings()
    {
        var p = Page("连接设置"); var table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(8) };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230)); table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Field(string label, Control control) { int r = table.RowCount++; control.Dock = DockStyle.Top; control.Margin = new Padding(5, 8, 5, 8); table.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(5, 12, 5, 4) }, 0, r); table.Controls.Add(control, 1, r); }
        var config = runtime.Config;
        dataDir.Text = config.DataDirectory; database.Text = config.DatabaseFile; cpamp.Text = config.CpampUrl; port.Value = config.Port; statsSeconds.Value = config.StatisticsSeconds; quotaSeconds.Value = config.QuotaSeconds; staleMinutes.Value = config.StaleMinutes;
        adminKey.PlaceholderText = config.ProtectedAdminKey.Length > 0 ? "已加密保存；留空保持原密钥" : "填写 CPAMP Admin Key；留空仅 SQLite 统计，无费用/账号发现";
        mode.Items.AddRange(["含今天的7个自然日（CPAMP请求监控）", "滚动168小时（CPAMP用量分析）"]); mode.SelectedIndex = config.SevenDayMode == "rolling" ? 1 : 0;
        quotaSource.Items.AddRange(["CPAMP 已有额度快照", "CPA 代理查询官方额度（仅勾选的 Codex）"]); quotaSource.SelectedIndex = config.QuotaSource == "official" ? 1 : 0;
        timezone.Items.Add("跟随 Windows 本地时区"); foreach (var zone in TimeZoneInfo.GetSystemTimeZones()) timezone.Items.Add(zone.Id);
        timezone.SelectedItem = config.TimezoneId.Length == 0 ? "跟随 Windows 本地时区" : config.TimezoneId;
        Field("CPAMP 数据目录（SQLite 降级）", dataDir); Field("数据库文件（空白自动识别）", database); Field("CPAMP 本机服务地址", cpamp); Field("CPAMP Admin Key", adminKey); Field("桥接端口", port); Field("统计刷新间隔（秒）", statsSeconds); Field("额度来源", quotaSource); Field("额度刷新间隔（秒）", quotaSeconds); Field("快照过期阈值（分钟）", staleMinutes); Field("7天统计模式", mode); Field("统计时区", timezone);
        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) startup.Checked = key?.GetValue("CPAMPBrimDeckBridge") is string;
        Field("开机启动（当前用户）", startup);
        var actions = Flow(); actions.Controls.Add(Button("保存并测试连接", async (_, _) => await SaveSettings()));
        actions.Controls.Add(Button("清除 CPAMP 管理密钥", async (_, _) => { var c = Clone(); c.ProtectedAdminKey = ""; store.Save(c); await runtime.UpdateConfig(c); adminKey.Clear(); adminKey.PlaceholderText = "未保存管理密钥"; await RefreshNow(); }));
        Field("", actions); p.Controls.Add(table);
    }
    private async Task SaveSettings()
    {
        if (saving) return; saving = true;
        try
        {
            var c = Clone(); c.DataDirectory = dataDir.Text.Trim(); c.DatabaseFile = database.Text.Trim(); c.CpampUrl = cpamp.Text.Trim().TrimEnd('/'); c.Port = (int)port.Value; c.StatisticsSeconds = (int)statsSeconds.Value; c.QuotaSeconds = (int)quotaSeconds.Value; c.StaleMinutes = (int)staleMinutes.Value; c.QuotaSource = quotaSource.SelectedIndex == 1 ? "official" : "cpamp";
            c.SevenDayMode = mode.SelectedIndex == 1 ? "rolling" : "calendar"; c.TimezoneId = timezone.SelectedIndex == 0 ? "" : timezone.SelectedItem?.ToString() ?? "";
            if (adminKey.Text.Trim().Length > 0) c.ProtectedAdminKey = SecretStore.Protect(adminKey.Text.Trim());
            store.Save(c); await runtime.UpdateConfig(c); adminKey.Clear(); adminKey.PlaceholderText = c.ProtectedAdminKey.Length > 0 ? "已加密保存；留空保持原密钥" : "填写 CPAMP Admin Key";
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (startup.Checked) key.SetValue("CPAMPBrimDeckBridge", "\"" + Environment.ProcessPath + "\" --tray"); else key.DeleteValue("CPAMPBrimDeckBridge", false);
            await RefreshNow(); MessageBox.Show(this, "设置已保存。连接结果请查看运行状态。", Text);
        }
        catch (Exception ex) { MessageBox.Show(this, ex is InvalidDataException ? ex.Message : "保存失败，请检查地址、路径和写入权限。", Text); }
        finally { saving = false; }
    }
    private void BuildIntegration()
    {
        var p = Page("BrimDeck 接入"); var stack = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        bridgeAddress.Padding = new Padding(8); stack.Controls.Add(bridgeAddress);
        stack.Controls.Add(Button("复制桥接地址", (_, _) => Copy($"http://127.0.0.1:{runtime.Config.Port}")));
        stack.Controls.Add(Button("复制桥接密钥", (_, _) => Copy(SecretStore.Read(runtime.Config.ProtectedBridgeKey))));
        stack.Controls.Add(Button("复制额度 Custom 脚本", (_, _) => Copy(Scripts.Read("BrimDeck-CPA-Quotas.js"))));
        stack.Controls.Add(Button("复制统计 Custom 脚本", (_, _) => Copy(Scripts.Read("BrimDeck-CPA-Statistics.js"))));
        stack.Controls.Add(new Label { Text = "独立账号来源：先在账号管理中保存选择，再选择账号生成脚本。", AutoSize = true, Padding = new Padding(8) });
        stack.Controls.Add(accountScripts); stack.Controls.Add(Button("复制单账号脚本", (_, _) => { if (accountScripts.SelectedValue is string id) Copy(Scripts.Account(id)); else MessageBox.Show(this, "请先选择一个已保存的账号", Text); }));
        stack.Controls.Add(Button("复制所选账号直连官方额度脚本（无需 Bridge）", (_, _) =>
        {
            if (accountScripts.SelectedValue is string id && editors.TryGetValue(id, out var editor) && editor.Provider == "codex" && editor.CredentialName.Length > 0) Copy(Scripts.DirectAccount(editor.CredentialName, editor.MemberEmail, editor.WorkspaceId));
            else MessageBox.Show(this, "请选择一个已有凭证文件的 Codex 账号", Text);
        }));
        stack.Controls.Add(Button("复制所选账号 CPAMP 快照脚本（无需 Bridge）", (_, _) =>
        {
            if (accountScripts.SelectedValue is string id && editors.TryGetValue(id, out var editor) && editor.Provider == "codex" && editor.CredentialName.Length > 0) Copy(Scripts.DirectAccount(editor.CredentialName, editor.MemberEmail, editor.WorkspaceId, true));
            else MessageBox.Show(this, "请选择一个已有凭证文件的 Codex 账号", Text);
        }));
        stack.Controls.Add(Button("复制 CPAMP 直连统计脚本（无需 Bridge）", (_, _) => Copy(Scripts.Read("BrimDeck-CPAMP-Direct-Statistics.js"))));
        stack.Controls.Add(Button("复制 CPA 原生统计脚本（无需 Bridge / CPAMP）", (_, _) => Copy(Scripts.Read("BrimDeck-CPA-Direct-Statistics.js"))));
        stack.Controls.Add(new Label { Text = "直连脚本：站点与密钥填写 CPA 地址 + CPA 管理密钥，或 CPAMP 地址 + CPAMP Admin Key。\nCPAMP 完整统计仅使用 CPAMP 地址及其 Admin Key。配置完成后可退出 Bridge；CPA / CPAMP 数据服务仍需运行。", AutoSize = true, MaximumSize = new Size(980, 0), Padding = new Padding(8) });
        stack.Controls.Add(Button("测试本机桥接 API", async (_, _) =>
        {
            try { using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) }; http.DefaultRequestHeaders.Authorization = new("Bearer", SecretStore.Read(runtime.Config.ProtectedBridgeKey)); using var r = await http.GetAsync($"http://127.0.0.1:{runtime.Config.Port}/health"); MessageBox.Show(this, r.IsSuccessStatusCode ? await r.Content.ReadAsStringAsync() : "测试失败 HTTP " + (int)r.StatusCode, Text); }
            catch { MessageBox.Show(this, "本机 API 未连接，请查看运行状态。", Text); }
        }));
        stack.Controls.Add(Button("打开中文使用教程", (_, _) => { string path = Path.Combine(AppContext.BaseDirectory, "使用教程.md"); if (File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); else MessageBox.Show(this, "请打开发布目录中的使用教程.md", Text); }));
        stack.Controls.Add(new Label { Text = "BrimDeck 设置 → 配额与统计 → 添加应用 → 配额来源选 Custom。\n创建“CPA 账号额度”和“CPA 用量统计”两个来源，分别粘贴脚本；两处填写相同桥接地址和桥接密钥。\n统计来源的环表示请求成功率。每个应用列只有一个环，取该来源最高百分比，全部应用合计最多7列。\n暂无额度时不返回虚假百分比，但 BrimDeck 可能为纯文字行绘制满环，请以行文字为准。", AutoSize = true, MaximumSize = new Size(980, 0), Padding = new Padding(8) });
        p.Controls.Add(stack);
    }
    private void Copy(string value) { try { Clipboard.SetText(value); } catch { MessageBox.Show(this, "剪贴板暂不可用，请重试", Text); } }
    private void UpdateAccountScriptChoices()
    {
        var selected = runtime.Config.Accounts.Where(a => a.Selected).OrderBy(a => a.Order).Select(a => new { a.Id, Name = Json.First(a.Alias, editors.GetValueOrDefault(a.Id)?.Name ?? "账号") }).ToList();
        string? previous = accountScripts.SelectedValue as string;
        accountScripts.DataSource = selected;
        if (previous is not null && selected.Any(a => a.Id == previous)) accountScripts.SelectedValue = previous;
    }
    private void RuntimeChanged(object? sender, EventArgs e)
    {
        if (!IsHandleCreated || IsDisposed || !Visible || Interlocked.Exchange(ref renderQueued, 1) != 0) return;
        try { BeginInvoke(() => { Interlocked.Exchange(ref renderQueued, 0); if (!IsDisposed && Visible) RenderState(); }); }
        catch (InvalidOperationException) { Interlocked.Exchange(ref renderQueued, 0); }
    }
    private void RenderState()
    {
        if (IsDisposed) return;
        var s = runtime.Statistics; var q = runtime.Inventory;
        status.Text = $"CPAMP  {(runtime.StatisticsError.Length == 0 ? "已连接" : runtime.StatisticsError)}    ·    SQLite  {runtime.DatabaseStatus}    ·    桥接 API  {(runtime.Listening ? "运行中" : runtime.ServerError)}";
        if (runtime.QuotaError.Length > 0 && runtime.QuotaError != "尚未读取") status.Text += "\n账号：" + runtime.QuotaError;
        if (s is not null)
        {
            var a = s.Today; var b = s.Last7Days;
            string Cost(WindowStats w) => w.EstimatedCost is { } money ? "$" + money.ToString("0.00") : "—";
            metrics["今日 Token"].Set(UiStyle.Compact(a.TotalTokens)); metrics["7天 Token"].Set(UiStyle.Compact(b.TotalTokens));
            metrics["今日费用"].Set(Cost(a)); metrics["7天费用"].Set(Cost(b)); metrics["今日请求"].Set(a.TotalRequests.ToString("N0"));
            metrics["成功请求"].Set(a.SuccessRequests.ToString("N0"), UiStyle.Green); metrics["失败请求"].Set(a.FailedRequests.ToString("N0"), a.FailedRequests > 0 ? UiStyle.Red : UiStyle.Ink);
            metrics["成功率"].Set(a.SuccessRate is { } rate ? rate.ToString("0.00") + "%" : "N/A");
            summary.Text = $"{(s.Stale ? "过期缓存  ·  " : "")}更新 {s.UpdatedAt.LocalDateTime:MM-dd HH:mm:ss}  ·  {s.Timezone}  ·  {(s.SevenDayMode == "rolling" ? "滚动168小时" : "含今天7个自然日")}\n已选 {runtime.Config.Accounts.Count(a => a.Selected)} 个额度账号";
        }
        else { foreach (var tile in metrics.Values) tile.Set("—"); summary.Text = "等待读取数据…"; }
        bridgeAddress.Text = $"桥接地址：http://127.0.0.1:{runtime.Config.Port}";
        if (q is not null)
        {
            bool changed = q.UpdatedAt != loadedInventory;
            loadedInventory = q.UpdatedAt;
            foreach (var a in q.Accounts)
            {
                if (!editors.TryGetValue(a.Id, out var editor))
                {
                    var choice = runtime.Config.Accounts.FirstOrDefault(c => c.Id == a.Id);
                    editor = new AccountEditor { Id = a.Id, Selected = choice?.Selected ?? false, Alias = choice?.Alias ?? "", Order = choice?.Order ?? editors.Count, WindowIds = choice?.Windows.ToList() ?? a.Windows.Where(w => w.Name.StartsWith("5h") || w.Name.StartsWith("Week") || w.Name.StartsWith("Month")).Select(w => w.Id).ToList() }; editors[a.Id] = editor;
                }
                editor.Name = a.Name; editor.Provider = a.Provider; editor.Status = a.Status; editor.Updated = a.UpdatedAt?.LocalDateTime.ToString("MM-dd HH:mm:ss") ?? "—"; editor.Windows = a.Windows; editor.CredentialName = a.CredentialName; editor.RefreshError = a.RefreshError;
                editor.MemberEmail = a.MemberEmail; editor.WorkspaceId = a.WorkspaceId;
            }
            foreach (var choice in runtime.Config.Accounts.Where(c => c.Selected && !editors.ContainsKey(c.Id))) editors[choice.Id] = new AccountEditor { Id = choice.Id, Selected = true, Name = "已移除或身份已变化", Alias = choice.Alias, Order = choice.Order, WindowIds = [..choice.Windows], Status = "需重新选择", Provider = "unknown" };
            var present = q.Accounts.Select(a => a.Id).ToHashSet();
            foreach (var editor in editors.Values.Where(a => !present.Contains(a.Id))) { editor.Status = "已移除 / 身份变化"; editor.Windows = []; }
            if (changed)
            {
                filtering = true;
                string previous = providers.SelectedItem?.ToString() ?? "全部 Provider"; providers.Items.Clear(); providers.Items.Add("全部 Provider"); foreach (var provider in editors.Values.Select(a => a.Provider).Distinct().Order()) providers.Items.Add(provider); providers.SelectedItem = providers.Items.Contains(previous) ? previous : "全部 Provider";
                filtering = false; UpdateAccountScriptChoices();
            }
            FilterAccounts();
        }
    }
    private async Task RefreshNow() { UseWaitCursor = true; try { await runtime.Refresh(true); RenderState(); } finally { UseWaitCursor = false; } }
    private void Restore() { Show(); WindowState = FormWindowState.Normal; Activate(); }
    public void CaptureForm(string path, int tab)
    {
        tabs.SelectedIndex = tab; RenderState(); PerformLayout();
        using var bitmap = new Bitmap(Width, Height); DrawToBitmap(bitmap, new Rectangle(Point.Empty, Size)); bitmap.Save(path);
    }
    public void ExitForTest() { exiting = true; Close(); }
}
