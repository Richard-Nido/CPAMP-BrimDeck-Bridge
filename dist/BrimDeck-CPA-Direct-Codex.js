// 无需 Bridge。站点填 CPA 本机根地址（通常 http://127.0.0.1:8317），密钥填 CPA 管理密钥。
// 也可填 CPAMP 地址 + CPAMP Admin Key，由 CPAMP 转发给 CPA。
// 选择器字段均精确匹配；每次重新查 auth_index，禁止按列表位置选账号。
async function fetchUsage(ctx) {
    const ACCOUNT = /*ACCOUNT*/{ name: "请填写凭证文件名.json", email: "", workspace: "" };
    const QUOTA_SOURCE = "official"; // official：CPA官方查询；cpamp：仅CPAMP已有快照（站点必须填CPAMP）。
    const site = String(ctx.site || "").replace(/\/+$/, "");
    if (!/^https?:\/\/(127\.0\.0\.1|localhost|\[::1\])(?::\d{1,5})?$/i.test(site)) throw new Error("站点必须是 CPA 本机回环根地址");
    if (!ctx.secrets || !ctx.secrets.key) throw new Error("请填写 CPA 管理密钥，不是模型 API Key");
    if (!ACCOUNT.name && !ACCOUNT.email) throw new Error("请填写指定账号的凭证文件名或邮箱");
    if (!["official", "cpamp"].includes(QUOTA_SOURCE)) throw new Error("额度来源必须是 official 或 cpamp");
    const headers = { Authorization: "Bearer " + ctx.secrets.key };
    const listing = await ctx.http.get(site + "/v0/management/auth-files", {
        headers, timeout: 2000, pick: { path: "files", fields: ["name", "provider", "type", "email", "account", "auth_index", "authIndex", "disabled", "id_token", "account_id", "chatgpt_account_id", "label", "quota"] }
    });
    if (listing.status !== 200) throw new Error("CPA 账号列表 HTTP " + listing.status + "，检查站点及对应管理密钥");
    const raw = await listing.json();
    const files = Array.isArray(raw) ? raw : raw && raw.files;
    if (!Array.isArray(files)) throw new Error("CPA 账号列表格式不兼容");
    function workspace(file) {
        const values = [];
        for (const obj of [file, file.id_token]) {
            if (!obj || typeof obj !== "object") continue;
            for (const key of ["chatgpt_account_id", "account_id"]) {
                if (typeof obj[key] === "string" && obj[key].trim()) values.push(obj[key].trim());
            }
        }
        const ids = Array.from(new Set(values));
        if (ids.length > 1) throw new Error("账号 workspace 身份冲突，请检查凭证");
        return ids[0] || "";
    }
    const matches = files.filter(file => String(file.provider || file.type).toLowerCase() === "codex" &&
        (!ACCOUNT.name || file.name === ACCOUNT.name) &&
        (!ACCOUNT.email || String(file.email || file.account || "").toLowerCase() === ACCOUNT.email.toLowerCase()) &&
        (!ACCOUNT.workspace || workspace(file) === ACCOUNT.workspace));
    if (matches.length !== 1) throw new Error(matches.length ? "账号选择不唯一，请补充凭证文件名或 workspace" : "指定账号不存在或身份已变化，请检查选择器");
    const file = matches[0];
    if (typeof file.account === "string" && file.account.includes("@") && typeof file.email === "string" && file.email.includes("@") && file.account.toLowerCase() !== file.email.toLowerCase()) throw new Error("账号邮箱身份冲突，请检查凭证");
    if (QUOTA_SOURCE === "cpamp") return await snapshots(file);
    if (file.disabled === true) throw new Error("指定账号已禁用");
    const authIndex = file.auth_index == null ? file.authIndex : file.auth_index;
    if (authIndex == null || String(authIndex).trim() === "") throw new Error("指定账号缺少 auth_index");
    const upstreamHeaders = { Authorization: "Bearer $TOKEN$", "Content-Type": "application/json", "User-Agent": "codex-tui/0.149.1" };
    const accountId = workspace(file);
    if (accountId) upstreamHeaders["Chatgpt-Account-Id"] = accountId;
    const response = await ctx.http.post(site + "/v0/management/api-call", {
        headers: { ...headers, "Content-Type": "application/json" }, timeout: 6500,
        body: { authIndex: String(authIndex), method: "GET", url: "https://chatgpt.com/backend-api/wham/usage", header: upstreamHeaders }
    });
    if (response.status !== 200) throw new Error("CPA 额度代理 HTTP " + response.status);
    const envelope = await response.json();
    const status = envelope.status_code == null ? envelope.statusCode : envelope.status_code;
    if (status !== 200) throw new Error("Codex 官方额度 HTTP " + status + (status === 429 ? "，请降低刷新频率" : ""));
    const data = typeof envelope.body === "string" ? JSON.parse(envelope.body) : envelope.body;
    if (!data || typeof data !== "object") throw new Error("官方额度响应格式不兼容");
    const rows = [];
    function add(limit, label) {
        if (!limit) return;
        for (const slot of ["primary_window", "secondary_window"]) {
            const w = limit[slot];
            if (w == null) continue;
            const used = w.used_percent;
            if (typeof used !== "number" || !isFinite(used) || used < 0 || used > 100) throw new Error("官方额度百分比缺失或无效");
            const duration = w.limit_window_seconds;
            const name = duration === 18000 ? "5h" : duration === 604800 ? "周限额" : duration >= 2419200 && duration <= 2678400 ? "月限额" : (typeof duration === "number" && duration > 0 ? duration / 3600 + "h" : "窗口 " + slot);
            let reset = "";
            const seconds = typeof w.reset_at === "number" ? w.reset_at - Date.now() / 1000 : w.reset_after_seconds;
            if (typeof seconds === "number" && isFinite(seconds)) reset = seconds <= 0 ? "等待官方更新" : seconds >= 86400 ? Math.floor(seconds / 86400) + " 天后" : seconds >= 3600 ? Math.floor(seconds / 3600) + " 小时后" : Math.max(1, Math.ceil(seconds / 60)) + " 分钟后";
            rows.push({ title: label + " · " + name, value: (100 - used).toFixed(1) + "%", suffix: "剩余", percent: used, note: "官方实时 · 已用 " + used.toFixed(1) + "%", resetAt: reset });
        }
    }
    add(data.rate_limit, "Codex");
    add(data.code_review_rate_limit, "代码审查");
    if (Array.isArray(data.additional_rate_limits)) {
        for (const extra of data.additional_rate_limits) add(extra.rate_limit, String(extra.limit_name || extra.metered_feature || "附加额度"));
    }
    if (!rows.length) throw new Error("官方响应没有可识别额度窗口");
    if (rows.length > 100) throw new Error("额度窗口超过 BrimDeck 100 行限制");
    return rows;

    async function snapshots(file) {
        const now = new Date(ctx.now || Date.now()).getTime();
        const auth = file.auth_index == null ? file.authIndex : file.auth_index;
        const members = [file.account, file.email].filter(v => typeof v === "string" && /^[\x21-\x7e]+@[\x21-\x7e]+$/.test(v)).map(v => v.toLowerCase());
        const member = Array.from(new Set(members));
        if (member.length > 1) throw new Error("账号邮箱身份冲突");
        const response = await ctx.http.post(site + "/v0/management/quota-snapshots/query", {
            headers: { ...headers, "Content-Type": "application/json" }, timeout: 6500,
            body: { now_ms: now, include_inactive: true, accounts: [{ row_key: "selected", provider: "codex", account: {
                account_snapshot: member[0] || "", auth_label_snapshot: file.label || "", auth_file_snapshot: file.name,
                auth_provider_snapshot: "codex", auth_account_id_snapshot: workspace(file), auth_index: auth == null ? "" : String(auth), source: file.name
            } }] }
        });
        if (response.status !== 200) throw new Error("CPAMP 额度快照 HTTP " + response.status + "，此模式需填写 CPAMP 地址和 Admin Key");
        const data = await response.json();
        if (!data || !Array.isArray(data.items)) throw new Error("CPAMP 快照格式不兼容");
        const items = data.items.filter(item => item.row_key === "selected");
        if (items.length !== 1 || !Array.isArray(items[0].windows)) throw new Error("CPAMP 未返回指定账号快照");
        const windows = items[0].windows.slice();
        const quota = file.quota;
        const runtimeAt = quota && typeof quota.observed_at === "string" ? new Date(quota.observed_at).getTime() : NaN;
        const signals = quota && quota.signals || {};
        const active = String(signals["X-Codex-Active-Limit"] || "").toLowerCase();
        function observedAt(w) {
            const source = w.field_sources && (w.field_sources.quota || w.field_sources.used_percent || w.field_sources.remaining_percent);
            return source && typeof source.observed_at_ms === "number" ? source.observed_at_ms : w.observed_at_ms;
        }
        function numeric(value) { return (typeof value === "number" || typeof value === "string" && value.trim()) && isFinite(Number(value)) ? Number(value) : null; }
        if (isFinite(runtimeAt) && runtimeAt <= now + 60000 && ["", "premium", "codex", "codex_main"].includes(active)) {
            for (const slot of ["Primary", "Secondary"]) {
                const minutes = numeric(signals["X-Codex-" + slot + "-Window-Minutes"]);
                const used = numeric(signals["X-Codex-" + slot + "-Used-Percent"]);
                if (minutes === null || minutes <= 0 || used === null || used < 0 || used > 100) continue;
                const seconds = minutes * 60;
                const kind = seconds === 18000 ? "five_hour" : seconds === 604800 ? "weekly" : seconds >= 2419200 && seconds <= 2678400 ? "monthly" : seconds + "s";
                const id = kind === "five_hour" ? "five-hour" : kind === "weekly" ? "weekly" : kind === "monthly" ? "monthly" : slot.toLowerCase() + "_window-" + seconds;
                const absolute = numeric(signals["X-Codex-" + slot + "-Reset-At"]);
                const after = numeric(signals["X-Codex-" + slot + "-Reset-After-Seconds"]);
                const reset = absolute !== null ? absolute * 1000 : after !== null ? runtimeAt + after * 1000 : undefined;
                const fresh = { provider_window_id: id, window_kind: kind, model_scope_kind: "family", model_scope_key: "codex_main", used_percent: used, remaining_percent: 100 - used,
                    cycle_end_ms: reset, observed_at_ms: runtimeAt, availability: "active", source: "CPAMP auth-files quota" };
                const index = windows.findIndex(w => w.provider_window_id === id && (w.model_scope_kind === "family" && w.model_scope_key === "codex_main" || w.model_scope_kind === "all" && !w.model_scope_key));
                if (index < 0) windows.push(fresh);
                else if ((!windows[index].availability || ["active", "available"].includes(windows[index].availability)) && (typeof observedAt(windows[index]) !== "number" || runtimeAt > observedAt(windows[index]))) windows[index] = fresh;
            }
        }
        const visible = windows.filter(w => w.availability !== "inactive");
        if (!visible.length) return [{ title: "Codex", value: "暂无额度快照", note: "CPAMP 尚无此账号的有效额度" }];
        if (visible.length > 100) throw new Error("额度窗口超过 BrimDeck 100 行限制");
        return visible.map(w => {
            const kind = w.window_kind;
            const name = kind === "five_hour" ? "5h" : kind === "weekly" ? "周限额" : kind === "monthly" ? "月限额" : String(kind || w.provider_window_id);
            const used = typeof w.used_percent === "number" ? w.used_percent : typeof w.remaining_percent === "number" ? 100 - w.remaining_percent : null;
            const remaining = typeof w.remaining_percent === "number" ? w.remaining_percent : used == null ? null : 100 - used;
            const observed = observedAt(w);
            const reset = w.current_cycle && w.current_cycle.scheduled_end_ms || w.cycle_end_ms;
            const stale = w.stale || typeof observed !== "number" || now - observed > 15 * 60000 || observed > now + 60000 || typeof reset === "number" && reset <= now;
            const row = { title: "Codex · " + name + (w.model_scope_key && w.model_scope_key !== "codex_main" ? " · " + w.model_scope_key : ""), value: "暂无额度", note: (w.source === "CPAMP auth-files quota" ? "CPAMP 运行时额度" : "CPAMP 快照") + (stale ? " · 过期" : "") + (file.disabled === true ? " · 账号已禁用" : "") };
            if (typeof observed === "number" && isFinite(observed)) row.note += " · " + new Date(observed).toISOString().replace("T", " ").slice(0,19) + " UTC";
            if (used !== null && (typeof used !== "number" || !isFinite(used) || used < 0 || used > 100 || !isFinite(remaining) || remaining < 0 || remaining > 100 || Math.abs(used + remaining - 100) > 0.1)) throw new Error("CPAMP 额度百分比无效");
            if (used !== null && (!w.availability || ["active", "available"].includes(w.availability))) { row.percent = used; row.value = remaining.toFixed(1) + "%"; row.suffix = "剩余"; }
            if (typeof reset === "number") { const seconds = (reset - now) / 1000; row.resetAt = seconds <= 0 ? "等待新快照" : seconds >= 86400 ? Math.floor(seconds / 86400) + " 天后" : seconds >= 3600 ? Math.floor(seconds / 3600) + " 小时后" : Math.max(1,Math.ceil(seconds / 60)) + " 分钟后"; }
            return row;
        });
    }
}
