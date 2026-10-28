// 来源名称：CPA 用量统计。站点地址填写桥接地址，密钥填写独立桥接密钥。
// 此来源的折叠环表示请求成功率。数值来自 CPAMP。
async function fetchUsage(ctx) {
    if (!ctx.site || !ctx.secrets || !ctx.secrets.key) throw new Error("请填写桥接地址和桥接密钥");
    const response = await ctx.http.get(ctx.site.replace(/\/+$/, "") + "/v1/stats", {
        headers: { Authorization: "Bearer " + ctx.secrets.key }, timeout: 5000
    });
    if (response.status === 401) throw new Error("桥接密钥无效，请重新复制");
    if (response.status !== 200) throw new Error("桥接统计不可用，HTTP " + response.status);
    const data = await response.json();
    if (!data || !data.today || !data.last7Days || !data.updatedAt || !["ok", "stale"].includes(data.status)) throw new Error("桥接统计字段缺失");
    function number(value) {
        if (typeof value !== "number" || !isFinite(value) || value < 0) throw new Error("桥接统计数值无效");
        return value;
    }
    function tokens(value) {
        value = number(value);
        return value >= 1e9 ? (value / 1e9).toFixed(2) + "B" : value >= 1e6 ? (value / 1e6).toFixed(2) + "M" : value >= 1e3 ? (value / 1e3).toFixed(2) + "K" : String(value);
    }
    function cost(window) {
        if (window.estimatedCost === null) return "不可用";
        const amount = number(window.estimatedCost);
        if (typeof window.currency !== "string" || !window.currency) throw new Error("费用币种缺失");
        return (window.currency === "USD" ? "$" : window.currency + " ") + amount.toFixed(2);
    }
    const a = data.today, b = data.last7Days;
    const total = number(a.totalRequests), success = number(a.successRequests), failed = number(a.failedRequests);
    if (total !== success + failed) throw new Error("请求分类与总数不一致");
    const cache = data.stale ? "过期缓存 · " : "";
    const updated = new Date(data.updatedAt);
    const updateText = isNaN(updated.getTime()) ? "" : ("0" + updated.getHours()).slice(-2) + ":" + ("0" + updated.getMinutes()).slice(-2);
    const rows = [
        { title: "今日 Token", value: tokens(a.totalTokens), note: cache + data.timezone },
        { title: "7天 Token", value: tokens(b.totalTokens), note: cache + (data.sevenDayMode === "rolling" ? "滚动168小时" : "含今天7个自然日") },
        { title: "今日费用", value: cost(a), note: cache },
        { title: "7天费用", value: cost(b), note: cache },
        { title: "今日请求", value: String(total), note: cache },
        { title: "今日成功", value: String(success), note: cache },
        { title: "今日失败", value: String(failed), note: cache }
    ];
    const rate = { title: "请求成功率", value: "N/A", note: cache, resetAt: updateText ? "更新 " + updateText : "" };
    if (total > 0) {
        const percent = number(a.successRate);
        if (percent > 100 || Math.abs(percent - success / total * 100) > 0.001) throw new Error("成功率无效");
        rate.percent = percent; rate.value = percent.toFixed(2) + "%";
    } else if (a.successRate !== null) throw new Error("零请求成功率应为 N/A");
    rows.push(rate);
    return rows;
}
