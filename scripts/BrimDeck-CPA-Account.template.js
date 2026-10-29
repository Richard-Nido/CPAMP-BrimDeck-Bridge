// 单账号 Custom 来源。由桥接程序替换安全账号标识，不含密钥。
async function fetchUsage(ctx) {
    const accountId = "__ACCOUNT_ID__";
    if (!/^[a-f0-9]{64}$/.test(accountId)) throw new Error("请在桥接程序中生成单账号脚本");
    if (!ctx.site || !ctx.secrets || !ctx.secrets.key) throw new Error("请填写桥接地址和桥接密钥");
    const response = await ctx.http.get(ctx.site.replace(/\/+$/, "") + "/v1/quotas/" + accountId, {
        headers: { Authorization: "Bearer " + ctx.secrets.key }, timeout: 5000
    });
    if (response.status === 401) throw new Error("桥接密钥无效，请重新复制");
    if (response.status === 404) throw new Error("账号已取消选择或身份已变化，请重新选择");
    if (response.status !== 200) throw new Error("桥接额度不可用，HTTP " + response.status);
    const data = await response.json();
    if (!data || !Array.isArray(data.accounts) || data.accounts.length !== 1 || !["ok", "stale"].includes(data.status)) throw new Error("账号额度字段缺失");
    const now = ctx.now ? new Date(ctx.now).getTime() : Date.now();
    function windowName(name) {
        return name.replace("Week", "周限额").replace("Month", "月限额").replace(" · codex_main", "").replace("claude_gpt", "Claude / GPT").replace("gemini", "Gemini");
    }
    function resetText(value) {
        const ms = new Date(value).getTime() - now;
        if (!isFinite(ms)) return "";
        if (ms <= 0) return "等待更新";
        if (ms >= 86400000) return Math.floor(ms / 86400000) + " 天后";
        if (ms >= 3600000) return Math.floor(ms / 3600000) + " 小时后";
        return Math.max(1, Math.ceil(ms / 60000)) + " 分钟后";
    }
    function updatedText(value) {
        const date = new Date(value);
        if (!isFinite(date.getTime())) return "";
        return (date.getMonth() + 1) + "/" + date.getDate() + " " + ("0" + date.getHours()).slice(-2) + ":" + ("0" + date.getMinutes()).slice(-2);
    }
    function statusName(value) { return value === "disabled" ? "已禁用" : value === "unavailable" ? "暂不可用" : value === "missing_identity_reselect_required" ? "已移除" : "状态待确认"; }
    const rows = [];
    for (const account of data.accounts) {
        if (typeof account.name !== "string" || !Array.isArray(account.windows)) throw new Error("桥接账号字段缺失");
        const unavailable = !["ready", "active", "available"].includes(account.status);
        if (!account.windows.length) {
            rows.push({ title: account.name, value: "暂无额度", note: account.refreshError || (unavailable ? statusName(account.status) : "未选择窗口或暂无快照") });
            continue;
        }
        for (const window of account.windows) {
            if (typeof window.name !== "string") throw new Error("额度窗口字段缺失");
            const stale = data.stale || window.stale;
            const flags = [];
            if (account.refreshError) flags.push(account.refreshError);
            if (unavailable) flags.push(statusName(account.status));
            if (stale) flags.push("过期 · " + updatedText(window.updatedAt));
            const row = { title: account.name + " · " + windowName(window.name), value: "暂无额度", note: flags.join(" · ") };
            if (window.status === "invalid_percent") { row.value = "额度数据异常"; ctx.log("额度百分比异常，已省略进度环数值"); }
            else if (window.status === "known") {
                const used = window.usedPercent, remaining = window.remainingPercent;
                if (typeof used !== "number" || !isFinite(used) || used < 0 || used > 100 || typeof remaining !== "number" || !isFinite(remaining) || remaining < 0 || remaining > 100 || Math.abs(used + remaining - 100) > 0.1) throw new Error("额度百分比无效");
                row.percent = used; row.value = remaining.toFixed(1) + "%"; row.suffix = "剩余";
                row.note = "已用 " + used.toFixed(1) + "%" + (row.note ? " · " + row.note : "");
            }
            if (window.resetAt) row.resetAt = resetText(window.resetAt);
            rows.push(row);
        }
    }
    if (rows.length > 100) throw new Error("超过 BrimDeck 的100行限制，请减少账号或使用独立账号来源");
    return rows;
}
