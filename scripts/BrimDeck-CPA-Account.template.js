// 请在桥接程序“BrimDeck 接入”页生成单账号脚本，本标识不包含任何密钥。
// __ACCOUNT_ID__ 由程序替换为已选账号的安全标识。
async function fetchUsage(ctx) {
    const accountId = "__ACCOUNT_ID__";
    if (!/^[a-f0-9]{64}$/.test(accountId)) throw new Error("请在桥接程序中生成单账号脚本");
    if (!ctx.site || !ctx.secrets || !ctx.secrets.key) throw new Error("请填写桥接地址和桥接密钥");
    const response = await ctx.http.get(ctx.site.replace(/\/+$/, "") + "/v1/quotas/" + accountId, {
        headers: { Authorization: "Bearer " + ctx.secrets.key }, timeout: 5000
    });
    if (response.status === 401) throw new Error("桥接密钥无效");
    if (response.status === 404) throw new Error("账号已取消选择或身份已变化，请重新选择并生成脚本");
    if (response.status !== 200) throw new Error("桥接额度不可用，HTTP " + response.status);
    const data = await response.json();
    if (!data || !Array.isArray(data.accounts) || data.accounts.length !== 1 || !["ok", "stale"].includes(data.status)) throw new Error("账号额度字段缺失");
    const account = data.accounts[0];
    if (!Array.isArray(account.windows) || typeof account.name !== "string") throw new Error("账号字段缺失");
    if (!account.windows.length) return [{ title: account.name, value: "额度未知", note: "未选择窗口或暂无快照 · " + account.status }];
    const rows = account.windows.map(window => {
        const row = { title: account.name + " · " + window.name, value: "额度未知", note: (data.stale || window.stale ? "过期快照 · " : "快照 · ") + (window.updatedAt || "更新时间未知") + " · " + account.status };
        if (window.status === "known") {
            const used = window.usedPercent, remaining = window.remainingPercent;
            if (typeof used !== "number" || !isFinite(used) || used < 0 || used > 100 || typeof remaining !== "number" || !isFinite(remaining) || remaining < 0 || remaining > 100 || Math.abs(used + remaining - 100) > 0.1) throw new Error("额度百分比无效");
            row.percent = used; row.value = "已使用 " + used.toFixed(1) + "%"; row.suffix = "剩余 " + remaining.toFixed(1) + "%";
        } else if (window.status === "invalid_percent") row.value = "额度数据异常";
        if (window.resetAt) row.resetAt = "重置 " + window.resetAt;
        return row;
    });
    if (rows.length > 100) throw new Error("超过 BrimDeck 的100行限制");
    return rows;
}
