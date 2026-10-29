using System.Globalization;
using System.Text.Json;

namespace Bridge;

public static class CodexQuota
{
    public const string UsageUrl = "https://chatgpt.com/backend-api/wham/usage";
    public static List<QuotaWindow> Parse(JsonElement payload, DateTimeOffset observed, int staleMinutes)
    {
        var windows = new List<QuotaWindow>();
        void Add(JsonElement limit, string prefix, string scopeKind, string scope)
        {
            foreach (string slot in new[] { "primary_window", "secondary_window" })
            {
                var w = limit.Get(slot);
                if (w.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) continue;
                var duration = w.Get("limit_window_seconds").Dec();
                string kind = duration switch { 18000 => "five_hour", 604800 => "weekly", >= 2419200 and <= 2678400 => "monthly", _ => "window" };
                string id = kind switch { "five_hour" => "five-hour", "weekly" => "weekly", "monthly" => "monthly", _ => slot + "-" + duration?.ToString(CultureInfo.InvariantCulture) };
                var reset = w.Get("reset_at").Dec();
                if (!reset.HasValue && w.Get("reset_after_seconds").Dec() is { } seconds) reset = observed.ToUnixTimeSeconds() + seconds;
                var normalized = JsonSerializer.SerializeToElement(new
                {
                    provider_window_id = prefix + id, window_kind = kind == "window" ? (duration?.ToString(CultureInfo.InvariantCulture) + "s") : kind,
                    model_scope_kind = scopeKind, model_scope_key = scope, used_percent = w.Get("used_percent").Dec(),
                    cycle_end_ms = reset * 1000, observed_at_ms = observed.ToUnixTimeMilliseconds(), availability = "active", source = "CPA official wham/usage"
                });
                windows.Add(CpampClient.ParseWindow(normalized, observed, staleMinutes));
            }
        }
        Add(payload.Get("rate_limit"), "", "family", "codex_main");
        Add(payload.Get("code_review_rate_limit"), "code-review-", "feature", "code_review");
        // Additional families keep their own identity; never relabel them as the main quota.
        var additional = payload.Get("additional_rate_limits");
        if (additional.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (var entry in additional.EnumerateArray())
            {
                string feature = Json.First(entry.Get("metered_feature").Str(), entry.Get("limit_name").Str(), "additional");
                string key = System.Text.RegularExpressions.Regex.Replace(feature.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
                Add(entry.Get("rate_limit"), key + "-" + index++ + "-", "feature", feature);
            }
        }
        if (windows.Count == 0 || windows.All(w => w.Status != "known")) throw new InvalidDataException("官方响应没有可识别的额度窗口或百分比");
        return windows;
    }

    public static List<QuotaWindow> Runtime(JsonElement file, DateTimeOffset now, int staleMinutes)
    {
        var quota = file.Get("quota");
        if (!DateTimeOffset.TryParse(quota.Get("observed_at").Str(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var observed) || observed > now.AddMinutes(1)) return [];
        var signals = quota.Get("signals");
        string active = signals.Get("X-Codex-Active-Limit").Str().ToLowerInvariant();
        // Unknown/Spark headers are not evidence about the main family.
        if (active is not ("" or "premium" or "codex" or "codex_main")) return [];
        decimal? Number(string key) => decimal.TryParse(signals.Get(key).Str(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;
        object? Window(string slot)
        {
            decimal? minutes = Number("X-Codex-" + slot + "-Window-Minutes");
            if (minutes is null or <= 0) return null;
            return new { used_percent = Number("X-Codex-" + slot + "-Used-Percent"), limit_window_seconds = minutes * 60,
                reset_at = Number("X-Codex-" + slot + "-Reset-At"), reset_after_seconds = Number("X-Codex-" + slot + "-Reset-After-Seconds") };
        }
        var payload = JsonSerializer.SerializeToElement(new { rate_limit = new { primary_window = Window("Primary"), secondary_window = Window("Secondary") } });
        if (payload.Get("rate_limit").Get("primary_window").ValueKind == JsonValueKind.Null && payload.Get("rate_limit").Get("secondary_window").ValueKind == JsonValueKind.Null) return [];
        // Malformed runtime telemetry must not hide an otherwise valid persisted snapshot.
        if (payload.Get("rate_limit").Get("primary_window").Get("used_percent").Dec() is not (>= 0 and <= 100) && payload.Get("rate_limit").Get("secondary_window").Get("used_percent").Dec() is not (>= 0 and <= 100)) return [];
        return Parse(payload, observed, staleMinutes).Select(w => w with
        { Source = "CPAMP auth-files quota", Stale = w.Stale || now - observed > TimeSpan.FromMinutes(staleMinutes) || w.ResetAt <= now }).ToList();
    }

    public static List<QuotaWindow> Merge(List<QuotaWindow> snapshots, List<QuotaWindow> runtime)
    {
        // API lifecycle and explicit scopes remain authoritative. Only replace a matching active family.
        var result = snapshots.ToList();
        foreach (var fresh in runtime)
        {
            int index = result.FindIndex(w => w.Id == fresh.Id);
            // Old CPAMP main windows used an implicit all scope. Keep their saved selection ID.
            if (index < 0 && fresh.Id.EndsWith("|family|codex_main", StringComparison.Ordinal))
            {
                string providerWindow = fresh.Id.Split('|')[0];
                index = result.FindIndex(w => w.Id == providerWindow + "|all|");
            }
            if (index < 0) { result.Add(fresh); continue; }
            var old = result[index];
            if (old.Status != "inactive_window" && (old.UpdatedAt is null || fresh.UpdatedAt > old.UpdatedAt) && fresh.Status == "known") result[index] = fresh with { Id = old.Id };
        }
        return result;
    }
}
