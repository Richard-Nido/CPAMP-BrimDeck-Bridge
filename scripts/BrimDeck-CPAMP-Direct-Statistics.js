// 无需 Bridge；站点填 CPAMP 本机根地址，密钥填 CPAMP Admin Key。
// 固定中国标准时间 UTC+8；7天默认含今天7个自然日，rolling 为滚动168小时。
async function fetchUsage(ctx) {
    const SEVEN_DAY_MODE = "calendar";
    const UTC_OFFSET_MINUTES = 480;
    const TIME_ZONE = "Asia/Shanghai";
    const site = String(ctx.site || "").replace(/\/+$/, "");
    if (!/^https?:\/\/(127\.0\.0\.1|localhost|\[::1\])(?::\d{1,5})?$/i.test(site)) throw new Error("站点必须是 CPAMP 本机回环根地址");
    if (!ctx.secrets || !ctx.secrets.key) throw new Error("请填写 CPAMP Admin Key");
    if (!["calendar", "rolling"].includes(SEVEN_DAY_MODE)) throw new Error("7天模式无效");
    const end = new Date(ctx.now || Date.now()).getTime();
    if (!isFinite(end)) throw new Error("当前时间无效");
    const day = 86400000, offset = UTC_OFFSET_MINUTES * 60000;
    const today = Math.floor((end + offset) / day) * day - offset;
    const week = SEVEN_DAY_MODE === "rolling" ? end - 7 * day : today - 6 * day;
    async function query(start) {
        const response = await ctx.http.post(site + "/v0/management/monitoring/analytics", {
            headers: { Authorization: "Bearer " + ctx.secrets.key, "Content-Type": "application/json" }, timeout: 6500,
            body: { from_ms: start, to_ms: end, now_ms: end, time_zone: TIME_ZONE, filters: { include_failed: true }, include: { summary: true, summary_profile: "core" } }
        });
        if (response.status !== 200) throw new Error("CPAMP 统计 HTTP " + response.status);
        const data = await response.json();
        if (!data || !data.summary) throw new Error("CPAMP summary 字段缺失");
        return data.summary;
    }
    const values = await Promise.all([query(today), query(week)]);
    function number(v) { if (typeof v !== "number" || !isFinite(v) || v < 0) throw new Error("CPAMP 统计字段缺失或无效"); return v; }
    function tokens(v) { v = number(v); return v >= 1e9 ? (v / 1e9).toFixed(2) + "B" : v >= 1e6 ? (v / 1e6).toFixed(2) + "M" : v >= 1e3 ? (v / 1e3).toFixed(2) + "K" : String(v); }
    function cost(v) { return v == null ? "不可用" : "$" + number(v).toFixed(2); }
    for (const v of values) {
        if (number(v.total_calls) !== number(v.success_calls) + number(v.failure_calls)) throw new Error("CPAMP 请求分类与总数不一致");
        number(v.total_tokens);
    }
    const a = values[0], b = values[1];
    const rows = [
        { title: "今日 Token", value: tokens(a.total_tokens), note: TIME_ZONE },
        { title: "7天 Token", value: tokens(b.total_tokens), note: SEVEN_DAY_MODE === "rolling" ? "滚动168小时" : "含今天7个自然日" },
        { title: "今日费用", value: cost(a.total_cost) }, { title: "7天费用", value: cost(b.total_cost) },
        { title: "今日请求", value: String(a.total_calls) }, { title: "今日成功", value: String(a.success_calls) }, { title: "今日失败", value: String(a.failure_calls) }
    ];
    const rate = { title: "请求成功率", value: "N/A", note: "CPAMP 统计" };
    if (a.total_calls > 0) { rate.percent = a.success_calls / a.total_calls * 100; rate.value = rate.percent.toFixed(2) + "%"; }
    rows.push(rate); return rows;
}
