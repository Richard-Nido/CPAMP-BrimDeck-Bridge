using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Bridge;

public sealed class BridgeRuntime : IAsyncDisposable
{
    public BridgeConfig Config { get; private set; }
    private StatsSnapshot? stats;
    private QuotaSnapshot? quotas;
    private readonly SemaphoreSlim statsGate = new(1), quotaGate = new(1);
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim scheduleChanged = new(0, 1);
    private readonly object clientLock = new();
    private CpampClient? client;
    private byte[]? serverAuthorization;
    private Task? loop;
    private WebApplication? server;
    private int disposed;
    public string StatisticsError { get; private set; } = "尚未读取";
    public string QuotaError { get; private set; } = "尚未读取";
    public string ServerError { get; private set; } = "";
    public string DatabaseStatus { get; private set; } = "尚未检查";
    public DateTimeOffset LastStatisticsAttempt { get; private set; }
    public DateTimeOffset LastQuotaAttempt { get; private set; }
    public bool Listening => server is not null;
    public event EventHandler? StateChanged;
    internal long BackgroundWakeups { get; private set; }
    public BridgeRuntime(BridgeConfig config) { Config = config; }
    public StatsSnapshot? Statistics
    {
        get
        {
            var s = stats; if (s is null) return null;
            bool expired = DateTimeOffset.UtcNow - s.UpdatedAt > TimeSpan.FromSeconds(Config.StatisticsSeconds * 2 + 10) || TimeWindows.Get(DateTimeOffset.UtcNow, Config).Today != s.Today.StartMs;
            if (!expired && StatisticsError.Length == 0) return s;
            return s with { Stale = true, Status = "stale", Error = StatisticsError.Length > 0 ? StatisticsError : "缓存等待刷新" };
        }
    }
    public QuotaSnapshot? Inventory
    {
        get
        {
            var q = quotas; if (q is null) return null;
            var now = DateTimeOffset.UtcNow;
            bool expired = now - q.UpdatedAt > TimeSpan.FromSeconds(Config.QuotaSeconds * 2 + 10);
            bool stale = expired || QuotaError.Length > 0;
            List<QuotaAccount>? changedAccounts = null;
            for (int ai = 0; ai < q.Accounts.Count; ai++)
            {
                var a = q.Accounts[ai]; List<QuotaWindow>? changedWindows = null;
                for (int wi = 0; wi < a.Windows.Count; wi++)
                {
                    var w = a.Windows[wi];
                    if (w.Stale || !(stale || w.UpdatedAt is null || now - w.UpdatedAt > TimeSpan.FromMinutes(Config.StaleMinutes) || w.ResetAt <= now)) continue;
                    changedWindows ??= [.. a.Windows]; changedWindows[wi] = w with { Stale = true };
                }
                if (changedWindows is null) continue;
                changedAccounts ??= [.. q.Accounts]; changedAccounts[ai] = a with { Windows = changedWindows };
            }
            if (!stale && changedAccounts is null) return q;
            return q with { Stale = stale, Status = stale ? "stale" : "ok", Error = QuotaError.Length > 0 ? QuotaError : expired ? "缓存等待刷新" : null, Accounts = changedAccounts ?? q.Accounts };
        }
    }
    public static QuotaSnapshot Selected(QuotaSnapshot q, BridgeConfig config, string? id = null)
    {
        var output = new List<QuotaAccount>();
        foreach (var choice in config.Accounts.Where(a => a.Selected && (id == null || a.Id == id)).OrderBy(a => a.Order).ThenBy(a => a.Id))
        {
            var account = q.Accounts.FirstOrDefault(a => a.Id == choice.Id);
            if (account is null)
            {
                output.Add(new(choice.Id, Json.First(choice.Alias, "已移除账号"), "unknown", "missing_identity_reselect_required", null, []));
                continue;
            }
            output.Add(account with { Name = Json.First(choice.Alias, account.Name), Windows = account.Windows.Where(w => choice.Windows.Any(id => id == w.Id || account.Provider == "codex" && SameCodexWindow(id, w.Id))).ToList() });
        }
        return q with { Accounts = output };
    }
    public static bool SameCodexWindow(string first, string second)
    {
        if (first == second) return true;
        var a = first.Split('|'); var b = second.Split('|');
        if (a.Length != 3 || b.Length != 3 || a[0] != b[0] || a[0] is not ("five-hour" or "weekly" or "monthly")) return false;
        bool Main(string[] p) => p[1] == "all" && p[2] == "" || p[1] == "family" && p[2] == "codex_main";
        return Main(a) && Main(b);
    }
    public async Task UpdateConfig(BridgeConfig config)
    {
        config.Validate();
        await statsGate.WaitAsync(); await quotaGate.WaitAsync();
        try
        {
            var old = Config;
            bool sourceChanged = old.CpampUrl != config.CpampUrl || old.DataDirectory != config.DataDirectory || old.DatabaseFile != config.DatabaseFile || old.ProtectedAdminKey != config.ProtectedAdminKey;
            bool windowChanged = old.SevenDayMode != config.SevenDayMode || old.TimezoneId != config.TimezoneId;
            Config = config;
            lock (clientLock) { client?.Dispose(); client = null; }
            if (sourceChanged || windowChanged) { stats = null; LastStatisticsAttempt = default; StatisticsError = "设置已更新，等待读取"; }
            if (sourceChanged || old.QuotaSource != config.QuotaSource) { quotas = null; LastQuotaAttempt = default; QuotaError = "设置已更新，等待读取"; }
            else if (!old.Accounts.Where(a => a.Selected).Select(a => a.Id).Order().SequenceEqual(config.Accounts.Where(a => a.Selected).Select(a => a.Id).Order())) LastQuotaAttempt = default;
            if (old.Port != config.Port || old.ProtectedBridgeKey != config.ProtectedBridgeKey)
            { await StopServer(); await StartServer(); }
        }
        finally { quotaGate.Release(); statsGate.Release(); }
        WakeScheduler(); NotifyChanged();
    }
    public async Task Start()
    {
        await StartServer();
        loop = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    BackgroundWakeups++;
                    await Refresh(false);
                    var next = NextRefreshDelay(DateTimeOffset.UtcNow);
                    await scheduleChanged.WaitAsync(next, stop.Token);
                }
            }
            catch (OperationCanceledException) { }
        });
    }
    internal TimeSpan NextRefreshDelay(DateTimeOffset now)
    {
        var statsDue = LastStatisticsAttempt.AddSeconds(Config.StatisticsSeconds);
        var quotaDue = LastQuotaAttempt.AddSeconds(Config.QuotaSeconds);
        var due = statsDue < quotaDue ? statsDue : quotaDue;
        return TimeSpan.FromMilliseconds(Math.Clamp((due - now).TotalMilliseconds, 100, int.MaxValue));
    }
    private CpampClient Client { get { lock (clientLock) return client ??= new CpampClient(Config); } }
    private void WakeScheduler() { if (disposed != 0) return; try { scheduleChanged.Release(); } catch (SemaphoreFullException) { } }
    private void NotifyChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
    private static string SafeError(Exception ex) => ex switch
    {
        OperationCanceledException => "读取超时或已取消",
        HttpRequestException => "无法连接 CPAMP，请检查服务地址",
        System.Text.Json.JsonException => "CPAMP JSON 格式不兼容",
        IOException or InvalidDataException or System.Security.Cryptography.CryptographicException => ex.Message.Length < 150 ? ex.Message : "读取失败，请检查连接设置",
        _ => "读取失败，请检查版本兼容性和连接设置"
    };
    public async Task Refresh(bool force)
    {
        async Task Stats()
        {
            if (!await statsGate.WaitAsync(0)) return;
            try
            {
                if (!force && DateTimeOffset.UtcNow - LastStatisticsAttempt < TimeSpan.FromSeconds(Config.StatisticsSeconds)) return;
                LastStatisticsAttempt = DateTimeOffset.UtcNow;
                stats = await Client.Statistics(DateTimeOffset.UtcNow, stop.Token); StatisticsError = "";
                try { using var db = ReadOnlyDb.Open(Config); DatabaseStatus = "正常 · 只读"; }
                catch { DatabaseStatus = "不可用 · 当前统计通过官方 API"; }
            }
            catch (Exception ex) { StatisticsError = SafeError(ex); }
            finally { statsGate.Release(); }
        }
        async Task Quotas()
        {
            if (!await quotaGate.WaitAsync(0)) return;
            try
            {
                if (!force && DateTimeOffset.UtcNow - LastQuotaAttempt < TimeSpan.FromSeconds(Config.QuotaSeconds)) return;
                LastQuotaAttempt = DateTimeOffset.UtcNow;
                quotas = await Client.Quotas(DateTimeOffset.UtcNow, stop.Token, quotas); QuotaError = "";
            }
            catch (Exception ex) { QuotaError = SafeError(ex); }
            finally { quotaGate.Release(); }
        }
        await Task.WhenAll(Stats(), Quotas());
        if (force) WakeScheduler();
        NotifyChanged();
    }
    private async Task StartServer()
    {
        WebApplication? starting = null;
        try
        {
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
            builder.Logging.ClearProviders();
            builder.WebHost.UseKestrel(o => { o.Listen(IPAddress.Loopback, Config.Port); o.Limits.MaxRequestBodySize = 0; o.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5); o.Limits.MaxConcurrentConnections = 32; o.AddServerHeader = false; });
            var app = builder.Build(); starting = app;
            var expected = Encoding.UTF8.GetBytes("Bearer " + SecretStore.Read(Config.ProtectedBridgeKey));
            serverAuthorization = expected;
            app.Use(async (ctx, next) =>
            {
                ctx.Response.Headers.CacheControl = "no-store"; ctx.Response.Headers.XContentTypeOptions = "nosniff";
                if (ctx.Connection.RemoteIpAddress is not { } address || !IPAddress.IsLoopback(address) || ctx.Request.Host.Host != "127.0.0.1" || ctx.Request.Host.Port != Config.Port || ctx.Request.Headers.ContainsKey("Origin") || ctx.Request.Headers["Sec-Fetch-Site"] == "cross-site")
                { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsJsonAsync(new { error = "local_clients_only" }); return; }
                var supplied = Encoding.UTF8.GetBytes(ctx.Request.Headers.Authorization.ToString());
                if (!CryptographicOperations.FixedTimeEquals(expected, supplied)) { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsJsonAsync(new { error = "unauthorized" }); return; }
                if (ctx.Request.Method != "GET") { ctx.Response.StatusCode = 405; return; }
                await next(ctx);
            });
            app.MapGet("/health", () =>
            {
                int accountErrors = quotas?.Accounts.Count(a => a.RefreshError is { Length: > 0 }) ?? 0;
                return Results.Json(new { status = StatisticsError.Length == 0 && QuotaError.Length == 0 && accountErrors == 0 ? "ok" : "degraded", statistics = StatisticsError.Length == 0 ? "ok" : "unavailable", quotas = QuotaError.Length > 0 ? "unavailable" : accountErrors > 0 ? "partial" : "ok", quotaAccountErrors = accountErrors, quotaSource = Config.QuotaSource, sqlite = DatabaseStatus, version = "1.2.0" }, Json.HttpOptions);
            });
            app.MapGet("/v1/stats", () => Statistics is { } value ? Results.Json(value, Json.HttpOptions) : Results.Json(new { status = "error", error = StatisticsError }, statusCode: 503));
            IResult Quotas(string? id)
            {
                if (Inventory is not { } q) return Results.Json(new { status = "error", error = QuotaError }, statusCode: 503);
                var output = Selected(q, Config, id);
                if (id != null && output.Accounts.Count == 0) return Results.Json(new { status = "error", error = "selected_account_not_found" }, statusCode: 404);
                return Results.Json(output, Json.HttpOptions);
            }
            app.MapGet("/v1/quotas", () => Quotas(null));
            app.MapGet("/v1/quotas/{id}", (string id) => Quotas(id));
            await app.StartAsync(stop.Token); server = app; starting = null; ServerError = "";
        }
        catch { if (starting is not null) await starting.DisposeAsync(); if (serverAuthorization is not null) CryptographicOperations.ZeroMemory(serverAuthorization); serverAuthorization = null; ServerError = "本机 API 启动失败：端口可能被占用，请在连接设置更换端口"; }
    }
    private async Task StopServer()
    {
        if (server is { } app) { server = null; using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3)); await app.StopAsync(timeout.Token); await app.DisposeAsync(); }
        if (serverAuthorization is not null) CryptographicOperations.ZeroMemory(serverAuthorization); serverAuthorization = null;
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Cancel(); if (loop is not null) await loop;
        await StopServer();
        await statsGate.WaitAsync(); await quotaGate.WaitAsync();
        try { lock (clientLock) { client?.Dispose(); client = null; } }
        finally { quotaGate.Release(); statsGate.Release(); }
        stop.Dispose(); scheduleChanged.Dispose();
    }
}
