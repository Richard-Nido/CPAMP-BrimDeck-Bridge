using System.Text.Json;
using System.Text.RegularExpressions;

namespace Bridge;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        string? Value(string key) { int index = Array.IndexOf(args, key); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }
        string stateDir = Value("--state-dir") ?? Path.Combine(AppContext.BaseDirectory, "state");
        bool diagnostic = args.Contains("--diagnose"), serve = args.Contains("--serve"), uiSmoke = args.Contains("--ui-smoke");
        try
        {
            var store = new ConfigStore(stateDir); var config = store.Load();
            if (Value("--import-key-file") is { } keyFile)
            {
                string content = File.ReadAllText(keyFile);
                var candidates = Regex.Matches(content, @"[A-Za-z0-9_\-]{20,}");
                if (candidates.Count != 1) throw new InvalidDataException("密钥文件格式不明确，请在界面手动填写");
                config.ProtectedAdminKey = SecretStore.Protect(candidates[0].Value); store.Save(config);
            }
            if (Value("--port") is { } testPort) config.Port = int.Parse(testPort);
            if (diagnostic) return Diagnose(config, Value("--output") ?? Path.Combine(stateDir, "diagnostic.json")).GetAwaiter().GetResult();
            if (serve) return Serve(config, Value("--seconds") is { } seconds ? int.Parse(seconds) : 120).GetAwaiter().GetResult();
            // One normal interactive process per Windows user. Diagnostic modes never run at login.
            using var mutex = new Mutex(true, @"Local\CPAMPBrimDeckBridge-" + System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value, out bool first);
            if (!first) { MessageBox.Show("桥接程序已运行，请从系统托盘打开。", "CPAMP–BrimDeck Bridge"); return 0; }
            var runtime = new BridgeRuntime(config);
            using var form = new MainForm(runtime, store, args.Contains("--tray"));
            if (uiSmoke)
            {
                var timer = new System.Windows.Forms.Timer { Interval = 12000 };
                timer.Tick += (_, _) => { timer.Stop(); string output = Value("--output") ?? stateDir; Directory.CreateDirectory(output); form.CaptureForm(Path.Combine(output, "ui-home.png"), 0); form.CaptureForm(Path.Combine(output, "ui-accounts.png"), 1); form.CaptureForm(Path.Combine(output, "ui-settings.png"), 2); form.CaptureForm(Path.Combine(output, "ui-integration.png"), 3); form.ExitForTest(); timer.Dispose(); };
                timer.Start();
            }
            Application.Run(form); runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex)
        {
            // No unfiltered exception details, URLs with secrets, request bodies, or accounts are logged.
            string error = ex is InvalidDataException ? ex.Message : "启动失败。请检查配置文件、当前用户加密密钥、目录写入权限和端口占用。";
            if (diagnostic || serve || uiSmoke) { Directory.CreateDirectory(stateDir); File.WriteAllText(Path.Combine(stateDir, "startup-error.txt"), error + "\n" + ex.GetType().Name); }
            else MessageBox.Show(error, "CPAMP–BrimDeck Bridge", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
    private static async Task<int> Serve(BridgeConfig config, int seconds)
    {
        await using var runtime = new BridgeRuntime(config); await runtime.Start();
        await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 3600))); return runtime.Listening ? 0 : 1;
    }
    private static async Task<int> Diagnose(BridgeConfig config, string output)
    {
        using var client = new CpampClient(config); var now = DateTimeOffset.UtcNow;
        var stats = await client.Statistics(now, CancellationToken.None); var quotas = await client.Quotas(now, CancellationToken.None);
        var dashboard = await client.Dashboard(stats.Today.StartMs, stats.Today.EndMs, CancellationToken.None);
        var comparison = CpampClient.ParseStats(dashboard.Get("today"), stats.Today.StartMs, stats.Today.EndMs, "CPAMP dashboard/summary");
        var weekDashboard = await client.Dashboard(stats.Last7Days.StartMs, stats.Last7Days.EndMs, CancellationToken.None);
        var weekComparison = CpampClient.ParseStats(weekDashboard.Get("today"), stats.Last7Days.StartMs, stats.Last7Days.EndMs, "CPAMP dashboard/summary (explicit 7-day interval)");
        bool matches = stats.Today.TotalTokens == comparison.TotalTokens && stats.Today.TotalRequests == comparison.TotalRequests && stats.Today.SuccessRequests == comparison.SuccessRequests && stats.Today.FailedRequests == comparison.FailedRequests && Math.Abs((stats.Today.EstimatedCost ?? 0) - (comparison.EstimatedCost ?? 0)) < .00000001m;
        bool weekMatches = stats.Last7Days.TotalTokens == weekComparison.TotalTokens && Math.Abs((stats.Last7Days.EstimatedCost ?? 0) - (weekComparison.EstimatedCost ?? 0)) < .00000001m;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        var report = new { generatedAt = now, stats, dashboardToday = comparison, dashboardWeek = weekComparison, dashboardMatches = matches, weekMatches, accountCount = quotas.Accounts.Count, providers = quotas.Accounts.GroupBy(a => a.Provider).ToDictionary(g => g.Key, g => g.Count()), credentialStates = quotas.Accounts.GroupBy(a => a.Status).ToDictionary(g => g.Key, g => g.Count()), windows = quotas.Accounts.SelectMany(a => a.Windows).GroupBy(w => new { w.Name, w.Status, w.Stale }).Select(g => new { g.Key.Name, g.Key.Status, g.Key.Stale, count = g.Count() }) };
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, Json.Options));
        return matches && weekMatches ? 0 : 2;
    }
}
