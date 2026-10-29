import fs from 'node:fs';
import vm from 'node:vm';
import assert from 'node:assert/strict';
const results = [];
const read = name => fs.readFileSync(new URL('../scripts/' + name, import.meta.url), 'utf8');
const marker = '/*ACCOUNT*/{ name: "请填写凭证文件名.json", email: "", workspace: "" }';
const source = read('BrimDeck-CPA-Direct-Codex.js').replace(marker, JSON.stringify({name:'fixture.json',email:'a@example.test',workspace:'space-a'}));
const account = {name:'fixture.json',provider:'codex',email:'a@example.test',auth_index:'current-index',id_token:{chatgpt_account_id:'space-a'}};
const payload = {rate_limit:{primary_window:{used_percent:35,limit_window_seconds:18000,reset_after_seconds:3600},secondary_window:{used_percent:72,limit_window_seconds:604800,reset_after_seconds:86400}}};
const clone = x => JSON.parse(JSON.stringify(x));
async function test(name, fn) { try { await fn(); results.push({name,passed:true}); console.log('PASS '+name); } catch(e) { results.push({name,passed:false,error:String(e)}); console.log('FAIL '+name+' '+e); } }
async function run(script, route, site='http://127.0.0.1:8317', key='fixture-management') {
    const calls=[];
    async function request(method,url,options) {
        assert.equal(options.headers.Authorization,'Bearer fixture-management');
        assert(options.timeout <= 6500);
        calls.push({method,url,options});
        const reply = await route(method,url,options,calls.length);
        return {status:reply.status ?? 200,json:()=>reply.body};
    }
    const ctx={site,secrets:{key},now:'2026-11-01T00:01:00Z',http:{get:(...a)=>request('GET',...a),post:(...a)=>request('POST',...a)},log:()=>{}};
    const rows = await vm.runInNewContext(script+';fetchUsage(ctx)',{ctx},{timeout:1000});
    assert(Array.isArray(rows) && rows.length > 0 && rows.length <= 100);
    for(const r of rows) { assert.equal(typeof r.title,'string');assert.equal(typeof r.value,'string'); if('percent' in r) assert(Number.isFinite(r.percent)&&r.percent>=0&&r.percent<=100); }
    return {rows,calls};
}
function quotaRoute(files=[account],body=payload,inner=200,outer=200) {
    return (method,url,options) => {
        if(method==='GET') {assert(url.endsWith('/v0/management/auth-files'));assert(options.pick.fields.includes('auth_index'));return {body:files};}
        assert(url.endsWith('/v0/management/api-call'));assert.equal(options.body.authIndex,files[0].auth_index);assert.equal(options.body.method,'GET');
        assert.equal(options.body.url,'https://chatgpt.com/backend-api/wham/usage');assert.equal(options.body.header.Authorization,'Bearer $TOKEN$');assert.equal(options.body.header['Chatgpt-Account-Id'],'space-a');
        return {status:outer,body:{status_code:inner,body:JSON.stringify(body)}};
    };
}
await test('direct quota two requests correct credential and used percentage',async()=>{const {rows,calls}=await run(source,quotaRoute());assert.equal(calls.length,2);assert.equal(rows[0].percent,35);assert.equal(rows[0].value,'65.0%');assert.equal(rows[1].percent,72);});
await test('direct resolves rotated auth_index every refresh',async()=>{const next={...account,auth_index:'rotated-index'};const {calls}=await run(source,quotaRoute([next]));assert.equal(calls[1].options.body.authIndex,'rotated-index');});
await test('direct monthly window classified by actual duration',async()=>{const p=clone(payload);p.rate_limit.primary_window.limit_window_seconds=2592000;p.rate_limit.secondary_window=null;const {rows}=await run(source,quotaRoute([account],p));assert(rows[0].title.includes('月限额'));assert.equal(rows.length,1);});
const snapshotScript=source.replace('const QUOTA_SOURCE = "official";','const QUOTA_SOURCE = "cpamp";');
await test('direct snapshot mode reads CPAMP without official call',async()=>{const {rows,calls}=await run(snapshotScript,(method,url,opts)=>{if(method==='GET')return{body:[account]};assert(url.endsWith('/quota-snapshots/query'));assert.equal(opts.body.accounts[0].account.auth_account_id_snapshot,'space-a');assert.equal(opts.body.accounts[0].account.account_snapshot,'a@example.test');return{body:{items:[{row_key:'selected',windows:[{provider_window_id:'five-hour',window_kind:'five_hour',model_scope_key:'codex_main',used_percent:6,remaining_percent:94,observed_at_ms:Date.parse('2026-11-01T00:00:00Z'),cycle_end_ms:Date.parse('2026-11-01T01:00:00Z')}]}]}};},'http://127.0.0.1:18317');assert.equal(calls.length,2);assert.equal(rows[0].percent,6);assert(!rows[0].note.includes('过期'));});
await test('direct snapshot quota field timestamp preserves staleness',async()=>{const {rows}=await run(snapshotScript,(method)=>method==='GET'?{body:[account]}:{body:{items:[{row_key:'selected',windows:[{window_kind:'weekly',used_percent:17,observed_at_ms:Date.parse('2026-11-01T00:01:00Z'),field_sources:{quota:{observed_at_ms:Date.parse('2026-10-31T23:00:00Z')}}}]}]}});assert(rows[0].note.includes('过期'));assert.equal(rows[0].percent,17);});
await test('direct snapshot empty data is unknown without fake ring',async()=>{const {rows}=await run(snapshotScript,method=>method==='GET'?{body:[account]}:{body:{items:[{row_key:'selected',windows:[]}]}});assert(!('percent' in rows[0]));});
await test('direct snapshot cannot silently use CPA official if unsupported',()=>assert.rejects(()=>run(snapshotScript,method=>method==='GET'?{body:[account]}:{status:404,body:{}})));
const telemetryAccount={...account,quota:{observed_at:'2026-11-01T00:00:30Z',signals:{'X-Codex-Active-Limit':'premium','X-Codex-Primary-Window-Minutes':'300','X-Codex-Primary-Used-Percent':'9','X-Codex-Primary-Reset-After-Seconds':'3600'}}};
const persisted={provider_window_id:'five-hour',window_kind:'five_hour',model_scope_kind:'family',model_scope_key:'codex_main',used_percent:6,observed_at_ms:Date.parse('2026-10-31T23:00:00Z'),availability:'active'};
await test('direct CPAMP merges newer runtime same-window telemetry',async()=>{const {rows}=await run(snapshotScript,method=>method==='GET'?{body:[telemetryAccount]}:{body:{items:[{row_key:'selected',windows:[persisted]}]}});assert.equal(rows.length,1);assert.equal(rows[0].percent,9);assert(rows[0].note.includes('运行时'));assert(!rows[0].note.includes('过期'));});
await test('direct CPAMP inactive lifecycle is not revived',async()=>{const {rows}=await run(snapshotScript,method=>method==='GET'?{body:[telemetryAccount]}:{body:{items:[{row_key:'selected',windows:[{...persisted,availability:'inactive'}]}]}});assert(!('percent' in rows[0]));});
await test('direct CPAMP keeps newer persisted quota over old runtime',async()=>{const {rows}=await run(snapshotScript,method=>method==='GET'?{body:[telemetryAccount]}:{body:{items:[{row_key:'selected',windows:[{...persisted,observed_at_ms:Date.parse('2026-11-01T00:00:45Z')}]}]}});assert.equal(rows[0].percent,6);});
await test('direct code-review and additional quota remain separate',async()=>{const p=clone(payload);p.code_review_rate_limit=p.rate_limit;p.additional_rate_limits=[{limit_name:'Spark',rate_limit:p.rate_limit}];const {rows}=await run(source,quotaRoute([account],p));assert.equal(rows.length,6);assert(rows[2].title.includes('代码审查'));assert(rows[4].title.includes('Spark'));});
for(const [name,files] of [['missing',[]],['ambiguous',[account,account]],['disabled',[{...account,disabled:true}]],['workspace changed',[{...account,id_token:{chatgpt_account_id:'space-b'}}]],['missing auth index',[{...account,auth_index:null}]]]) {
    await test('direct '+name+' fails before official request',async()=>{let calls=0;await assert.rejects(()=>run(source,()=>{calls++;return{body:files};}));assert.equal(calls,1);});
}
for(const status of [401,403,429,500]) await test('official nested HTTP '+status+' rejects',()=>assert.rejects(()=>run(source,quotaRoute([account],payload,status))));
await test('outer API HTTP 401 rejects',()=>assert.rejects(()=>run(source,quotaRoute([account],payload,200,401))));
await test('unknown official fields do not become zero',()=>assert.rejects(()=>run(source,quotaRoute([account],{}))));
await test('invalid official percentage rejects',async()=>{const p=clone(payload);p.rate_limit.primary_window.used_percent=120;await assert.rejects(()=>run(source,quotaRoute([account],p)));});
await test('timeout propagates to BrimDeck last-good cache',()=>assert.rejects(()=>run(source,()=>{throw new Error('timeout');})));
for(const site of ['https://example.com','http://127.0.0.1:8317/path','http://localhost@evil.test','http://127.0.0.1:8317?key=x']) await test('local management key never sent to '+site,async()=>{let sent=false;await assert.rejects(()=>run(source,()=>{sent=true;return {};},site));assert(!sent);});
const cpamp=read('BrimDeck-CPAMP-Direct-Statistics.js');
const summary={total_calls:10,success_calls:9,failure_calls:1,total_tokens:1234567,total_cost:.123456};
await test('CPAMP two parallel read-only analytics queries retain eight metrics',async()=>{const {rows,calls}=await run(cpamp,(method,url,opts)=>{assert.equal(method,'POST');assert(url.endsWith('/monitoring/analytics'));assert.equal(opts.body.filters.include_failed,true);return{body:{summary}};},'http://127.0.0.1:18317');assert.equal(calls.length,2);assert.equal(rows.length,8);assert.equal(rows[0].value,'1.23M');assert.equal(rows[2].value,'$0.12');assert.equal(rows[7].percent,90);const starts=calls.map(c=>new Date(c.options.body.from_ms).toISOString());assert(starts.includes('2026-10-31T16:00:00.000Z'));assert(starts.includes('2026-10-25T16:00:00.000Z'));assert(calls.every(c=>c.options.body.to_ms===Date.parse('2026-11-01T00:01:00Z')));});
await test('CPAMP missing cost is unavailable and zero requests is N/A',async()=>{const {rows}=await run(cpamp,()=>({body:{summary:{total_calls:0,success_calls:0,failure_calls:0,total_tokens:0}}}));assert.equal(rows[2].value,'不可用');assert.equal(rows[7].value,'N/A');assert(!('percent' in rows[7]));});
await test('CPAMP missing fields fail',()=>assert.rejects(()=>run(cpamp,()=>({body:{summary:{}}}))));
const native=read('BrimDeck-CPA-Direct-Statistics.js');
await test('CPA missing full statistics reports unsupported without fallback calls',async()=>{let count=0;await assert.rejects(()=>run(native,()=>{count++;return{status:404,body:{}};}),/不支持完整原生统计/);assert.equal(count,1);});
for(const nested of [false,true]) await test('CPA native '+(nested?'legacy envelope':'flat response')+' is read-only',async()=>{const data={total_requests:10,success_count:9,failure_count:1,total_tokens:10000};const {rows,calls}=await run(native,(method,url)=>{assert.equal(method,'GET');assert(url.endsWith('/management/usage'));assert(!url.includes('queue'));return{body:nested?{usage:data}:data};});assert.equal(calls.length,1);assert.equal(rows[0].value,'10.00K');assert.equal(rows[4].percent,90);assert.equal(rows[5].value,'CPA 接口未提供');});
await test('CPA invalid classification fails',()=>assert.rejects(()=>run(native,()=>({body:{total_requests:2,success_count:2,failure_count:2,total_tokens:0}}))));
for(const script of [cpamp,native]) for(const status of [401,500]) await test('statistics HTTP '+status+' rejects',()=>assert.rejects(()=>run(script,()=>({status,body:{}}))));
fs.mkdirSync(new URL('./output/',import.meta.url),{recursive:true});
fs.writeFileSync(new URL('./output/direct-script-tests.json',import.meta.url),JSON.stringify({passed:results.every(r=>r.passed),count:results.length,results},null,2));
process.exitCode=results.every(r=>r.passed)?0:1;
