// 来源名称：CPA 账号额度。只返回桥接设置中勾选的账号和窗口。
async function fetchUsage(ctx) {
    if (!ctx.site || !ctx.secrets || !ctx.secrets.key) throw new Error("请填写桥接地址和桥接密钥");
    const response = await ctx.http.get(ctx.site.replace(/\/+$/, "") + "/v1/quotas", {
        headers: { Authorization: "Bearer " + ctx.secrets.key }, timeout: 5000
    });
    if (response.status === 401) throw new Error("桥接密钥无效，请重新复制");
    if (response.status !== 200) throw new Error("桥接额度不可用，HTTP " + response.status);
    const data = await response.json();
    if (!data || !Array.isArray(data.accounts) || !["ok", "stale"].includes(data.status)) throw new Error("桥接额度字段缺失");
    if (!data.accounts.length) return [{ title: "尚未选择账号", value: "请在桥接程序中勾选", note: "无可用额度百分比" }];
    const rows = [];
    for (const account of data.accounts) {
        if (typeof account.name !== "string" || !Array.isArray(account.windows)) throw new Error("桥接账号字段缺失");
        const unavailable = !["ready", "active", "available"].includes(account.status);
        if (!account.windows.length) {
            rows.push({ title: account.name, value: "额度未知", note: unavailable ? "凭证状态：" + account.status : "未选择窗口或暂无快照" });
            continue;
        }
        for (const window of account.windows) {
            if (typeof window.name !== "string") throw new Error("额度窗口字段缺失");
            const stale = data.stale || window.stale;
            const row = { title: account.name + " · " + window.name, value: "额度未知", note: (stale ? "过期快照 · " : "快照 · ") + (window.updatedAt || "更新时间未知") };
            if (unavailable) row.note = "凭证 " + account.status + " · " + row.note;
            if (window.status === "invalid_percent") { row.value = "额度数据异常"; ctx.log("额度百分比异常，已省略进度环数值"); }
            else if (window.status === "known") {
                const used = window.usedPercent, remaining = window.remainingPercent;
                if (typeof used !== "number" || !isFinite(used) || used < 0 || used > 100 || typeof remaining !== "number" || !isFinite(remaining) || remaining < 0 || remaining > 100 || Math.abs(used + remaining - 100) > 0.1) throw new Error("额度百分比无效");
                row.percent = used; row.value = "已使用 " + used.toFixed(1) + "%"; row.suffix = "剩余 " + remaining.toFixed(1) + "%";
            }
            if (window.resetAt) row.resetAt = "重置 " + window.resetAt;
            rows.push(row);
        }
    }
    if (rows.length > 100) throw new Error("超过 BrimDeck 的100行限制，请减少账号或使用独立账号来源");
    return rows;
}
