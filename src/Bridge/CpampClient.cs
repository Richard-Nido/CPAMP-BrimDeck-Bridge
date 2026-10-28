using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Bridge;

public sealed class CpampClient : IDisposable
{
    private readonly HttpClient http;
    private readonly BridgeConfig config;
    public CpampClient(BridgeConfig config)
    {
        this.config = config;
        config.Validate();
        http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
        { BaseAddress = new Uri(config.CpampUrl), Timeout = TimeSpan.FromSeconds(8), MaxResponseContentBufferSize = 8_000_000 };
        var key = SecretStore.Read(config.ProtectedAdminKey);
        if (key.Length > 0) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }
    private async Task<JsonElement> Call(string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        if (!response.IsSuccessStatusCode) throw new IOException($"CPAMP HTTP {(int)response.StatusCode}；请检查服务和管理密钥");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return doc.RootElement.Clone();
    }
    public static WindowStats ParseStats(JsonElement e, long start, long end, string source)
    {
        long total = e.Get("total_calls").Long(), success = e.Get("success_calls").Long(), failed = e.Get("failure_calls").Long(), tokens = e.Get("total_tokens").Long();
        if (total < 0 || success < 0 || failed < 0 || tokens < 0 || checked(success + failed) != total) throw new InvalidDataException("CPAMP 请求分类与总数不一致");
        decimal? cost = e.Get("total_cost").Dec();
        if (cost < 0) throw new InvalidDataException("CPAMP 费用无效");
        return new(tokens, cost, total, success, failed, start, end, source);
    }
    public async Task<StatsSnapshot> Statistics(DateTimeOffset now, CancellationToken ct)
    {
        var (today, week, end, zone) = TimeWindows.Get(now, config);
        if (config.ProtectedAdminKey.Length == 0)
        {
            return await Task.Run(() =>
            {
                using var db = ReadOnlyDb.Open(config);
                return new StatsSnapshot(now, zone, config.SevenDayMode, db.Statistics(today, end), db.Statistics(week, end));
            }, ct);
        }
        async Task<WindowStats> Query(long start)
        {
            var response = await Call("/v0/management/monitoring/analytics", new
            {
                from_ms = start, to_ms = end, now_ms = end, time_zone = zone,
                filters = new { include_failed = true }, include = new { summary = true, summary_profile = "core" }
            }, ct);
            return ParseStats(response.Get("summary"), start, end, "CPAMP monitoring/analytics v1.14.9");
        }
        var a = Query(today); var b = Query(week);
        await Task.WhenAll(a, b);
        return new(now, zone, config.SevenDayMode, await a, await b);
    }
    public static string SafeId(string provider, string identity) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("cpamp-bridge-v1\0" + provider + "\0" + identity))).ToLowerInvariant();
    public static string Member(JsonElement file)
    {
        var members = new[] { "accountSnapshot", "account_snapshot", "account", "email" }.Select(n => file.Get(n).Str().Trim(' '))
            .Where(n => Regex.IsMatch(n, @"^[\x21-\x7e]+@[\x21-\x7e]+$") && n.Count(c => c == '@') == 1).Select(n => n.ToLowerInvariant()).Distinct().ToList();
        return members.Count == 1 ? members[0] : "";
    }
    private static string Workspace(JsonElement file)
    {
        var values = new HashSet<string>(); bool invalid = false;
        void Visit(JsonElement value, int depth)
        {
            if (depth > 6 || value.ValueKind != JsonValueKind.Object) return;
            foreach (string key in new[] { "chatgpt_account_id", "chatgptAccountId", "account_id", "accountId" })
            {
                var field = value.Get(key);
                if (field.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) continue;
                if (field.ValueKind is not (JsonValueKind.Number or JsonValueKind.String)) { invalid = true; continue; }
                var text = field.Str().Trim(' '); if (text.Length > 0) values.Add(text);
            }
            // Only metadata objects, never decode or retrieve OAuth tokens for this purpose.
            foreach (string key in new[] { "metadata", "attributes", "id_token", "idToken" }) Visit(value.Get(key), depth + 1);
        }
        Visit(file, 0); return !invalid && values.Count == 1 ? values.First() : "";
    }
    public static QuotaWindow ParseWindow(JsonElement w, DateTimeOffset now, int staleMinutes)
    {
        var used = w.Get("used_percent").Dec(); var remaining = w.Get("remaining_percent").Dec();
        string state = "known";
        if (used is < 0 or > 100 || remaining is < 0 or > 100 || (used.HasValue && remaining.HasValue && Math.Abs(used.Value + remaining.Value - 100) > .1m))
        { state = "invalid_percent"; used = remaining = null; }
        else if (!used.HasValue && remaining.HasValue) used = 100 - remaining;
        else if (used.HasValue && !remaining.HasValue) remaining = 100 - used;
        if (!used.HasValue && state == "known") state = "unknown";
        DateTimeOffset? Date(JsonElement e) => e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var n) && n > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(n) : null;
        var observed = Date(w.Get("observed_at_ms"));
        var percentSource = w.Get("field_sources").Get("used_percent").Get("observed_at_ms");
        if (percentSource.ValueKind != JsonValueKind.Number) percentSource = w.Get("field_sources").Get("remaining_percent").Get("observed_at_ms");
        var percentObserved = Date(percentSource) ?? observed;
        var reset = Date(w.Get("cycle_end_ms"));
        string availability = w.Get("availability").Str();
        bool stale = w.Get("stale").Flag() || percentObserved is null || now - percentObserved > TimeSpan.FromMinutes(staleMinutes) || percentObserved > now.AddMinutes(1) || reset <= now;
        if (availability is not ("" or "active" or "available")) state = "inactive_window";
        string kind = w.Get("window_kind").Str(), scope = w.Get("model_scope_key").Str();
        string name = kind switch { "five_hour" => "5h", "weekly" => "Week", "monthly" => "Month", _ => kind };
        if (scope.Length > 0) name += " · " + scope;
        string id = w.Get("provider_window_id").Str() + "|" + w.Get("model_scope_kind").Str() + "|" + scope;
        return new(id, name, used, remaining, reset, percentObserved, stale, state, availability, w.Get("source").Str());
    }
    public async Task<QuotaSnapshot> Quotas(DateTimeOffset now, CancellationToken ct)
    {
        if (config.ProtectedAdminKey.Length == 0) throw new InvalidDataException("账号发现需要 CPAMP Admin Key，请在连接设置中填写");
        var root = await Call("/v0/management/auth-files", null, ct);
        var files = root.Get("files");
        if (files.ValueKind != JsonValueKind.Array) throw new InvalidDataException("CPAMP 账号列表格式不兼容");
        var list = new List<(string Provider, string Name, string Status, string FallbackId, object Target)>();
        foreach (var file in files.EnumerateArray())
        {
            string provider = Json.First(file.Get("provider").Str(), file.Get("type").Str()).ToLowerInvariant().Replace('_', '-');
            if (provider is "x-ai" or "grok") provider = "xai";
            string physical = file.Get("name").Str(), auth = file.Get("auth_index").Str();
            string member = provider == "codex" ? Member(file) : Json.First(file.Get("account").Str(), file.Get("email").Str());
            string workspace = provider == "codex" ? Workspace(file) : "";
            string name = Json.First(file.Get("label").Str(), file.Get("email").Str(), file.Get("account").Str(), physical, provider);
            string status = file.Get("disabled").Flag() ? "disabled" : file.Get("unavailable").Flag() ? "unavailable" : Json.First(file.Get("status").Str(), "unknown");
            // Fallback ties selection to a physical credential and a member fingerprint, never array position/auth_index alone.
            string fallback = SafeId(provider, physical + "\0" + member + "\0" + workspace);
            list.Add((provider, name, status, fallback, new
            {
                account_snapshot = member, auth_label_snapshot = file.Get("label").Str(), auth_file_snapshot = physical,
                auth_provider_snapshot = provider, auth_account_id_snapshot = workspace,
                auth_project_id_snapshot = provider == "codex" ? "" : file.Get("project_id").Str(), auth_index = auth, source = physical
            }));
        }
        var accounts = new List<QuotaAccount>();
        string[] supported = ["codex", "claude", "antigravity", "kimi", "xai", "devin", "meta"];
        var query = list.Where(a => supported.Contains(a.Provider)).ToList();
        var results = new Dictionary<string, JsonElement>();
        // Official server batches at most 200 accounts per query.
        foreach (var batch in query.Chunk(200))
        {
            var response = await Call("/v0/management/quota-snapshots/query", new
            { now_ms = now.ToUnixTimeMilliseconds(), include_inactive = false, accounts = batch.Select(a => new { row_key = a.FallbackId, provider = a.Provider, account = a.Target }).ToArray() }, ct);
            if (response.Get("items").ValueKind != JsonValueKind.Array) throw new InvalidDataException("CPAMP 额度快照格式不兼容");
            foreach (var item in response.Get("items").EnumerateArray()) results[item.Get("row_key").Str()] = item;
        }
        foreach (var a in list)
        {
            var windows = new List<QuotaWindow>(); string id = a.FallbackId;
            if (results.TryGetValue(a.FallbackId, out var item))
            {
                string identity = item.Get("account_key").Str();
                if (identity.Length > 0) id = SafeId(a.Provider, identity);
                if (item.Get("windows").ValueKind == JsonValueKind.Array)
                    windows = item.Get("windows").EnumerateArray().Select(w => ParseWindow(w, now, config.StaleMinutes)).ToList();
            }
            var observed = windows.Where(w => w.UpdatedAt.HasValue).Select(w => w.UpdatedAt).DefaultIfEmpty().Max();
            accounts.Add(new(id, a.Name, a.Provider, a.Status, observed, windows));
        }
        // Same official stable account may have multiple physical files. Merge once, keep a conservative status.
        return new(now, accounts.GroupBy(a => a.Id).Select(g => g.First() with
        { Status = g.All(a => a.Status is "ready" or "active" or "available") ? g.First().Status : g.FirstOrDefault(a => a.Status is "disabled" or "unavailable")?.Status ?? g.First().Status,
            Windows = g.SelectMany(a => a.Windows).GroupBy(w => w.Id).Select(w => w.OrderByDescending(x => x.UpdatedAt).First()).ToList() }).ToList());
    }
    public async Task<JsonElement> Dashboard(long start, long end, CancellationToken ct) => await Call($"/v0/management/dashboard/summary?today_start_ms={start}&now_ms={end}", null, ct);
    public void Dispose() => http.Dispose();
}
