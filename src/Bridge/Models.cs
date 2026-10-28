using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bridge;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static readonly JsonSerializerOptions HttpOptions = new(JsonSerializerDefaults.Web);
    public static JsonElement Get(this JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) ? v : default;
    public static string Str(this JsonElement e) => e.ValueKind == JsonValueKind.String ? e.GetString()!.Trim() : e.ValueKind == JsonValueKind.Number ? e.GetRawText() : "";
    public static long Long(this JsonElement e) => e.TryGetInt64(out var n) ? n : throw new InvalidDataException("CPAMP 统计字段缺失或无效");
    public static decimal? Dec(this JsonElement e) => e.ValueKind == JsonValueKind.Number && e.TryGetDecimal(out var n) ? n : null;
    public static bool Flag(this JsonElement e) => e.ValueKind == JsonValueKind.True;
    public static string First(params string[] a) => a.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "";
}

public sealed record WindowStats(long TotalTokens, decimal? EstimatedCost, long TotalRequests, long SuccessRequests, long FailedRequests,
    long StartMs, long EndMs, string Source)
{
    public decimal? SuccessRate => TotalRequests == 0 ? null : SuccessRequests * 100m / TotalRequests;
    public string Currency => "USD";
    public bool CostComplete => false;
    public string CostStatus => EstimatedCost.HasValue ? "known_estimate_completeness_unverified" : "unavailable";
    public string CostNote => "CPAMP 已知费用估算；完整性未确认，不等于订阅扣款";
}
public sealed record StatsSnapshot(DateTimeOffset UpdatedAt, string Timezone, string SevenDayMode, WindowStats Today, WindowStats Last7Days)
{
    public string Status { get; init; } = "ok";
    public bool Stale { get; init; }
    public string? Error { get; init; }
}
public sealed record QuotaWindow(string Id, string Name, decimal? UsedPercent, decimal? RemainingPercent, DateTimeOffset? ResetAt,
    DateTimeOffset? UpdatedAt, bool Stale, string Status, string Availability, string Source);
public sealed record QuotaAccount(string Id, string Name, string Provider, string Status, DateTimeOffset? UpdatedAt, List<QuotaWindow> Windows)
{
    public bool QuotaSupported => Windows.Count > 0;
}
public sealed record QuotaSnapshot(DateTimeOffset UpdatedAt, List<QuotaAccount> Accounts)
{
    public string Status { get; init; } = "ok";
    public bool Stale { get; init; }
    public string? Error { get; init; }
}
public sealed class AccountChoice
{
    public string Id { get; set; } = "";
    public bool Selected { get; set; }
    public string Alias { get; set; } = "";
    public List<string> Windows { get; set; } = [];
    public int Order { get; set; }
}
public sealed class BridgeConfig
{
    public string DataDirectory { get; set; } = @"D:\CPA\cpa-manager-plus_v1.14.9_windows_amd64\data";
    public string DatabaseFile { get; set; } = "";
    public string CpampUrl { get; set; } = "http://127.0.0.1:18317";
    public int Port { get; set; } = 18731;
    public int StatisticsSeconds { get; set; } = 60;
    public int QuotaSeconds { get; set; } = 120;
    public int StaleMinutes { get; set; } = 15;
    public string SevenDayMode { get; set; } = "calendar";
    public string TimezoneId { get; set; } = "";
    public string ProtectedAdminKey { get; set; } = "";
    public string ProtectedBridgeKey { get; set; } = "";
    public List<AccountChoice> Accounts { get; set; } = [];
    public void Validate()
    {
        if (Port is < 1024 or > 65535 || StatisticsSeconds is < 15 or > 3600 || QuotaSeconds is < 30 or > 7200 || StaleMinutes is < 1 or > 1440)
            throw new InvalidDataException("端口或刷新间隔超出允许范围");
        if (SevenDayMode is not ("calendar" or "rolling")) throw new InvalidDataException("7 天模式无效");
        if (TimezoneId.Length > 0) _ = TimeZoneInfo.FindSystemTimeZoneById(TimezoneId);
        var uri = new Uri(CpampUrl);
        if (uri.Scheme != "http" && uri.Scheme != "https" || !uri.IsLoopback || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.AbsolutePath != "/")
            throw new InvalidDataException("CPAMP 地址必须是本机回环根地址，例如 http://127.0.0.1:18317");
        if (Accounts.Select(a => a.Id).Distinct().Count() != Accounts.Count) throw new InvalidDataException("账号选择存在重复标识");
        foreach (var a in Accounts) if (a.Alias.Length > 64) throw new InvalidDataException("账号别名最多 64 字符");
    }
}
public static class TimeWindows
{
    public static (long Today, long Week, long End, string Zone) Get(DateTimeOffset now, BridgeConfig config)
    {
        var tz = config.TimezoneId.Length == 0 ? TimeZoneInfo.Local : TimeZoneInfo.FindSystemTimeZoneById(config.TimezoneId);
        var date = TimeZoneInfo.ConvertTime(now, tz).Date;
        long Midnight(DateTime value)
        {
            // Resolve rare midnight DST gaps forward; choose the first occurrence of an ambiguous midnight.
            while (tz.IsInvalidTime(value)) value = value.AddMinutes(1);
            if (tz.IsAmbiguousTime(value)) return new DateTimeOffset(value, tz.GetAmbiguousTimeOffsets(value).Max()).ToUnixTimeMilliseconds();
            return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(value, tz)).ToUnixTimeMilliseconds();
        }
        string zone = TimeZoneInfo.TryConvertWindowsIdToIanaId(tz.Id, out var iana) ? iana : tz.Id;
        return (Midnight(date), config.SevenDayMode == "rolling" ? now.AddHours(-168).ToUnixTimeMilliseconds() : Midnight(date.AddDays(-6)), now.ToUnixTimeMilliseconds(), zone);
    }
}
