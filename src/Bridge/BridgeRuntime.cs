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
    public BridgeRuntime(BridgeConfig config) { Config = config; }
    public StatsSnapshot? Statistics
    {
        get
        {
            var s = stats; if (s is null) return null;
            bool expired = DateTimeOffset.UtcNow - s.UpdatedAt > TimeSpan.FromSeconds(Config.StatisticsSeconds * 2 + 10) || TimeWindows.Get(DateTimeOffset.UtcNow, Config).Today != s.Today.StartMs;
            return s with { Stale = expired || StatisticsError.Length > 0, Status = expired || StatisticsError.Length > 0 ? "stale" : "ok", Error = StatisticsError.Length > 0 ? StatisticsError : expired ? "缓存等待刷新" : null };
        }
    }
    public QuotaSnapshot? Inventory
    {
        get
        {
            var q = quotas; if (q is null) return null;
            var now = DateTimeOffset.UtcNow;
            bool expired = now - q.UpdatedAt > TimeSpan.FromSeconds(Config.QuotaSeconds * 2 + 10);
            return q with { Stale = expired || QuotaError.Length > 0, Status = expired || QuotaError.Length > 0 ? "stale" : "ok", Error = QuotaError.Length > 0 ? QuotaError : expired ? "缓存等待刷新" : null,
                Accounts = q.Accounts.Select(a => a with { Windows = a.Windows.Select(w => w with { Stale = w.Stale || expired || QuotaError.Length > 0 || w.UpdatedAt is null || now - w.UpdatedAt > TimeSpan.FromMinutes(Config.StaleMinutes) || w.ResetAt <= now }).ToList() }).ToList() };
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
            output.Add(account with { Name = Json.First(choice.Alias, account.Name), Windows = account.Windows.Where(w => choice.Windows.Contains(w.Id)).ToList() });
        }
        return q with { Accounts = output };
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
            if (sourceChanged || windowChanged) { stats = null; LastStatisticsAttempt = default; StatisticsError = "设置已更新，等待读取"; }
            if (sourceChanged) { quotas = null; LastQuotaAttempt = default; QuotaError = "设置已更新，等待读取"; }
            if (old.Port != config.Port || old.ProtectedBridgeKey != config.ProtectedBridgeKey)
            { await StopServer(); await StartServer(); }
        }
        finally { quotaGate.Release(); statsGate.Release(); }
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
                    await Refresh(false);
                    await Task.Delay(1000, stop.Token);
                }
            }
            catch (OperationCanceledException) { }
        });
    }
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
                using var client = new CpampClient(Config);
                stats = await client.Statistics(DateTimeOffset.UtcNow, stop.Token); StatisticsError = "";
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
                using var client = new CpampClient(Config);
                quotas = await client.Quotas(DateTimeOffset.UtcNow, stop.Token); QuotaError = "";
            }
            catch (Exception ex) { QuotaError = SafeError(ex); }
            finally { quotaGate.Release(); }
        }
        await Task.WhenAll(Stats(), Quotas());
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
            app.Use(async (ctx, next) =>
            {
                ctx.Response.Headers.CacheControl = "no-store"; ctx.Response.Headers.XContentTypeOptions = "nosniff";
                if (ctx.Connection.RemoteIpAddress is not { } address || !IPAddress.IsLoopback(address) || ctx.Request.Host.Host != "127.0.0.1" || ctx.Request.Host.Port != Config.Port || ctx.Request.Headers.ContainsKey("Origin") || ctx.Request.Headers["Sec-Fetch-Site"] == "cross-site")
                { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsJsonAsync(new { error = "local_clients_only" }); return; }
                var expected = Encoding.UTF8.GetBytes("Bearer " + SecretStore.Read(Config.ProtectedBridgeKey));
                var supplied = Encoding.UTF8.GetBytes(ctx.Request.Headers.Authorization.ToString());
                if (!CryptographicOperations.FixedTimeEquals(expected, supplied)) { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsJsonAsync(new { error = "unauthorized" }); return; }
                if (ctx.Request.Method != "GET") { ctx.Response.StatusCode = 405; return; }
                await next(ctx);
            });
            app.MapGet("/health", () => Results.Json(new { status = StatisticsError.Length == 0 && QuotaError.Length == 0 ? "ok" : "degraded", statistics = StatisticsError.Length == 0 ? "ok" : "unavailable", quotas = QuotaError.Length == 0 ? "ok" : "unavailable", sqlite = DatabaseStatus, version = "1.0.0" }, Json.Options));
            app.MapGet("/v1/stats", () => Statistics is { } value ? Results.Json(value, Json.Options) : Results.Json(new { status = "error", error = StatisticsError }, statusCode: 503));
            IResult Quotas(string? id)
            {
                if (Inventory is not { } q) return Results.Json(new { status = "error", error = QuotaError }, statusCode: 503);
                var output = Selected(q, Config, id);
                if (id != null && output.Accounts.Count == 0) return Results.Json(new { status = "error", error = "selected_account_not_found" }, statusCode: 404);
                return Results.Json(output, Json.Options);
            }
            app.MapGet("/v1/quotas", () => Quotas(null));
            app.MapGet("/v1/quotas/{id}", (string id) => Quotas(id));
            await app.StartAsync(stop.Token); server = app; starting = null; ServerError = "";
        }
        catch { if (starting is not null) await starting.DisposeAsync(); ServerError = "本机 API 启动失败：端口可能被占用，请在连接设置更换端口"; }
    }
    private async Task StopServer()
    {
        if (server is { } app) { server = null; using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3)); await app.StopAsync(timeout.Token); await app.DisposeAsync(); }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Cancel(); if (loop is not null) await loop;
        await StopServer(); stop.Dispose();
    }
}
