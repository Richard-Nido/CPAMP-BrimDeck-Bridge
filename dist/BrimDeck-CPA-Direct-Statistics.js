// 无需 Bridge 或 CPAMP；站点填 CPA 本机根地址，密钥填 CPA 管理密钥。
// 只读 /usage，不消费 /usage/queue。CPA 未提供费用和历史完整性，不伪造今日/7天费用。
async function fetchUsage(ctx) {
    const site = String(ctx.site || "").replace(/\/+$/, "");
    if (!/^https?:\/\/(127\.0\.0\.1|localhost|\[::1\])(?::\d{1,5})?$/i.test(site)) throw new Error("站点必须是 CPA 本机回环根地址");
    if (!ctx.secrets || !ctx.secrets.key) throw new Error("请填写 CPA 管理密钥");
    const response = await ctx.http.get(site + "/v0/management/usage", { headers: { Authorization: "Bearer " + ctx.secrets.key }, timeout: 6500 });
    if (response.status === 404 || response.status === 405) throw new Error("当前 CPA 版本不支持完整原生统计接口 /usage；请使用 CPAMP 直连统计脚本");
    if (response.status !== 200) throw new Error("CPA 原生统计 HTTP " + response.status);
    const raw = await response.json();
    const data = raw && (raw.usage || raw);
    function number(v) { if (typeof v !== "number" || !isFinite(v) || v < 0) throw new Error("CPA 统计字段缺失或无效；检查 usage-statistics-enabled"); return v; }
    const total = number(data && data.total_requests), success = number(data.success_count), failed = number(data.failure_count), tokens = number(data.total_tokens);
    if (total !== success + failed) throw new Error("CPA 请求分类与总数不一致");
    const tokenText = tokens >= 1e9 ? (tokens / 1e9).toFixed(2) + "B" : tokens >= 1e6 ? (tokens / 1e6).toFixed(2) + "M" : tokens >= 1e3 ? (tokens / 1e3).toFixed(2) + "K" : String(tokens);
    const note = "CPA 当前保留的原生统计，非完整历史";
    const rows = [
        { title: "累计 Token", value: tokenText, note }, { title: "累计请求", value: String(total), note },
        { title: "累计成功", value: String(success), note }, { title: "累计失败", value: String(failed), note }
    ];
    const rate = { title: "请求成功率", value: "N/A", note };
    if (total > 0) { rate.percent = success / total * 100; rate.value = rate.percent.toFixed(2) + "%"; }
    rows.push(rate, { title: "费用 / 今日与7天", value: "CPA 接口未提供", note: "需要这些指标时使用 CPAMP 直连统计脚本" });
    return rows;
}
