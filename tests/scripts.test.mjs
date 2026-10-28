import fs from 'node:fs';
import vm from 'node:vm';
import assert from 'node:assert/strict';
const results=[];
async function test(name,fn){try{await fn();results.push({name,passed:true});console.log('PASS '+name);}catch(error){results.push({name,passed:false,error:String(error)});console.log('FAIL '+name+' '+error);}}
const read=n=>fs.readFileSync(new URL('../scripts/'+n,import.meta.url),'utf8');
const scripts={stats:read('BrimDeck-CPA-Statistics.js'),quota:read('BrimDeck-CPA-Quotas.js'),account:read('BrimDeck-CPA-Account.template.js').replaceAll('__ACCOUNT_ID__','a'.repeat(64))};
const stat={status:'ok',updatedAt:'2026-10-28T07:00:00Z',timezone:'Asia/Shanghai',sevenDayMode:'calendar',today:{totalTokens:1250000,estimatedCost:1.23456789,currency:'USD',totalRequests:256,successRequests:250,failedRequests:6,successRate:250/256*100},last7Days:{totalTokens:8640000,estimatedCost:9.56,currency:'USD'}};
const quota={status:'ok',accounts:[{name:'中文账号甲',status:'active',windows:[{name:'5h',usedPercent:35,remainingPercent:65,status:'known',updatedAt:'2026-10-28T07:00:00Z',resetAt:'2026-10-28T08:00:00Z'}]}]};
const clone=x=>JSON.parse(JSON.stringify(x));
async function execute(script,payload,{status=200,fail=false}={}){
 let requests=0;
 const ctx={site:'http://127.0.0.1:18731/',secrets:{key:'fixture-bridge'},log:()=>{},http:{get:async(url,options)=>{requests++;assert.equal(options.headers.Authorization,'Bearer fixture-bridge');assert(options.timeout<10000);assert(url.startsWith('http://127.0.0.1:18731/v1/'));if(fail)throw new Error('connection timeout');return{status,json:()=>payload};}}};
 const sandbox=vm.createContext({ctx}); const rows=await vm.runInContext(script+';fetchUsage(ctx)',sandbox,{timeout:1000});assert.equal(requests,1);validate(rows);return rows;
}
function validate(rows){assert(Array.isArray(rows)&&rows.length>0&&rows.length<=100);for(const row of rows){assert.equal(typeof row.title,'string');assert(row.title.trim());for(const key of ['value','suffix','note','resetAt'])if(key in row)assert.equal(typeof row[key],'string');if('percent'in row)assert(typeof row.percent==='number'&&Number.isFinite(row.percent)&&row.percent>=0&&row.percent<=100);}}
await test('statistics 8 rows Chinese format only success percent',async()=>{const r=await execute(scripts.stats,stat);assert.equal(r.length,8);assert.equal(r[0].value,'1.25M');assert.equal(r[2].value,'$1.23');assert.equal(r.filter(x=>'percent'in x).length,1);assert.equal(r[7].percent,250/256*100);});
await test('zero requests N/A no fake100',async()=>{const s=clone(stat);Object.assign(s.today,{totalRequests:0,successRequests:0,failedRequests:0,successRate:null,totalTokens:0});const r=await execute(scripts.stats,s);assert.equal(r[0].value,'0');assert.equal(r[7].value,'N/A');assert(!('percent'in r[7]));});
await test('missing prices not fake zero',async()=>{const s=clone(stat);s.today.estimatedCost=null;assert.equal((await execute(scripts.stats,s))[2].value,'不可用');});
await test('snapshot used35 remaining65',async()=>{const r=await execute(scripts.quota,quota);assert.equal(r[0].percent,35);assert(r[0].suffix.includes('65.0%'));});
for(const p of [0,100])await test('quota boundary '+p,async()=>{const q=clone(quota);Object.assign(q.accounts[0].windows[0],{usedPercent:p,remainingPercent:100-p});assert.equal((await execute(scripts.quota,q))[0].percent,p);});
await test('partial multiple accounts windows preserved',async()=>{const q=clone(quota);q.accounts.push({...clone(q.accounts[0]),name:'乙'});q.accounts[0].windows.push({...q.accounts[0].windows[0],name:'Week'});assert.equal((await execute(scripts.quota,q)).length,3);});
await test('quota never returns success rate',async()=>{const r=await execute(scripts.quota,quota);assert(r.every(x=>!x.title.includes('成功')));});
await test('blank selection nonempty text no fabricated percentage',async()=>{const r=await execute(scripts.quota,{status:'ok',accounts:[]});assert.equal(r.length,1);assert(!('percent'in r[0]));});
await test('unknown account quota no fabricated number',async()=>{const q=clone(quota);q.accounts[0].windows[0].status='unknown';assert(!('percent'in(await execute(scripts.quota,q))[0]));});
await test('stale data explicitly labelled',async()=>{const q=clone(quota);q.stale=true;assert((await execute(scripts.quota,q))[0].note.includes('过期'));const s=clone(stat);s.stale=true;assert((await execute(scripts.stats,s))[0].note.includes('过期'));});
await test('disabled credential explicitly labelled',async()=>{const q=clone(quota);q.accounts[0].status='disabled';assert((await execute(scripts.quota,q))[0].note.includes('disabled'));});
await test('invalid percent reported without ring',async()=>{const q=clone(quota);q.accounts[0].windows[0].status='invalid_percent';const r=await execute(scripts.quota,q);assert(!('percent'in r[0]));assert(r[0].value.includes('异常'));});
for(const [key,payload]of [['stats',stat],['quota',quota],['account',quota]]){
 await test(key+' HTTP401 reject',async()=>{await assert.rejects(()=>execute(scripts[key],payload,{status:401}));});
 await test(key+' HTTP500 reject',async()=>{await assert.rejects(()=>execute(scripts[key],payload,{status:500}));});
 await test(key+' disconnected/timeout reject',async()=>{await assert.rejects(()=>execute(scripts[key],payload,{fail:true}));});
 await test(key+' missing fields reject',async()=>{await assert.rejects(()=>execute(scripts[key],{}));});
}
await test('independent account safe ID works',async()=>{const r=await execute(scripts.account,quota);assert.equal(r[0].percent,35);assert(!scripts.account.includes('fixture-bridge'));});
await test('unexpanded account template rejects',async()=>{await assert.rejects(()=>execute(read('BrimDeck-CPA-Account.template.js'),quota));});
await test('invalid raw quota rejects no silent clamp',async()=>{const q=clone(quota);q.accounts[0].windows[0].usedPercent=120;await assert.rejects(()=>execute(scripts.quota,q));});
fs.mkdirSync(new URL('./output/',import.meta.url),{recursive:true});fs.writeFileSync(new URL('./output/script-tests.json',import.meta.url),JSON.stringify({passed:results.every(r=>r.passed),count:results.length,results},null,2));
process.exitCode=results.every(r=>r.passed)?0:1;
