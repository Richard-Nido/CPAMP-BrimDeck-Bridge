using Bridge;
using BrimDeck.Core;
using System.Net;
using System.Text.Json;

public static class DirectContract
{
    sealed class Secrets(string key) : IProviderSecrets
    {
        public string Read(Guid id) => key;
        public void Write(Guid id,string value) => throw new NotSupportedException();
        public void Delete(Guid id) => throw new NotSupportedException();
    }
    sealed class Handler : HttpMessageHandler
    {
        public int OfficialStatus = 200;
        public int NativeStatus = 200;
        public int Requests;
        public string? LastAuthIndex;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req,CancellationToken ct)
        {
            Requests++;
            object body;
            if(req.RequestUri!.AbsolutePath.EndsWith("auth-files")) body=new {files=new[]{new {provider="codex",name="fixture.json",email="a@example.test",auth_index="rotated-index",id_token=new {chatgpt_account_id="space-a"}}}};
            else if(req.RequestUri.AbsolutePath.EndsWith("api-call"))
            {
                using var doc=JsonDocument.Parse(await req.Content!.ReadAsStringAsync(ct));LastAuthIndex=doc.RootElement.GetProperty("authIndex").GetString();
                body=new {status_code=OfficialStatus,body="{\"rate_limit\":{\"primary_window\":{\"used_percent\":35,\"limit_window_seconds\":18000,\"reset_after_seconds\":3600}}}"};
            }
            else if(req.RequestUri.AbsolutePath.EndsWith("analytics")) body=new {summary=new {total_calls=10,success_calls=9,failure_calls=1,total_tokens=1234567,total_cost=.123456m}};
            else if(req.RequestUri.AbsolutePath.EndsWith("quota-snapshots/query")) body=new {items=new[]{new {row_key="selected",windows=new[]{new {provider_window_id="five-hour",window_kind="five_hour",used_percent=6,remaining_percent=94,observed_at_ms=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}}}}};
            else body=new {total_requests=10,success_count=9,failure_count=1,total_tokens=10000};
            return new HttpResponseMessage(req.RequestUri.AbsolutePath.EndsWith("/usage") ? (HttpStatusCode)NativeStatus : HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(body))};
        }
    }
    static readonly List<object> Results=[];
    static int failures;
    static void Check(string name,bool pass){Results.Add(new{name,passed=pass});System.Console.WriteLine((pass?"PASS ":"FAIL ")+name);if(!pass)failures++;}
    static AppEntry Entry(string site,string script)=>new(){QuotaSource=ProviderId.Custom,Site=site,Script=script,AllowHttp=true};
    public static async Task<int> Run(string root,bool live)
    {
        try
        {
            var handler=new Handler();using var http=new HttpClient(handler);using var providers=new ConfiguredProviders(new Secrets("fixture-key"),http);
            var entry=Entry("http://127.0.0.1:8317",Scripts.DirectAccount("fixture.json","a@example.test","space-a"));
            var result=await providers.TestAsync(entry);
            Check("installed Jint direct official quota and pick fields",result.Snapshot.LiveQuota&&result.Snapshot.Metrics.Count==1&&result.Snapshot.Metrics[0].UsedPercent==35&&handler.LastAuthIndex=="rotated-index"&&handler.Requests==2);
            handler.OfficialStatus=429;result=await providers.TestAsync(entry);
            Check("installed Jint nested official429 keeps last-good stale",!result.Snapshot.LiveQuota&&result.Snapshot.IsStale&&result.Snapshot.Metrics[0].UsedPercent==35);
            handler.OfficialStatus=200;
            result=await providers.TestAsync(Entry("http://127.0.0.1:18317",Scripts.DirectAccount("fixture.json","a@example.test","space-a",true)));
            Check("installed Jint direct CPAMP snapshot source",result.Snapshot.LiveQuota&&result.Snapshot.Metrics.Count==1&&result.Snapshot.Metrics[0].UsedPercent==6);
            result=await providers.TestAsync(Entry("http://127.0.0.1:18317",Scripts.Read("BrimDeck-CPAMP-Direct-Statistics.js")));
            Check("installed Jint direct CPAMP parallel analytics eight metrics",result.Snapshot.LiveQuota&&result.Snapshot.Metrics.Count==8&&result.Snapshot.Metrics[7].UsedPercent==90);
            result=await providers.TestAsync(Entry("http://127.0.0.1:8317",Scripts.Read("BrimDeck-CPA-Direct-Statistics.js")));
            Check("installed Jint native CPA six metrics",result.Snapshot.LiveQuota&&result.Snapshot.Metrics.Count==6&&result.Snapshot.Metrics[4].UsedPercent==90);
            handler.NativeStatus=404;result=await providers.TestAsync(Entry("http://127.0.0.1:8317",Scripts.Read("BrimDeck-CPA-Direct-Statistics.js")));
            Check("installed Jint unsupported CPA statistics explicitly reported",!result.Snapshot.LiveQuota&&result.Snapshot.Status.Contains("不支持完整原生统计"));
            if(live) await Live(root);
        }
        catch(Exception ex){Check("direct contract unexpected "+ex.GetType().Name,false);}
        Directory.CreateDirectory(Path.Combine(root,"tests/output"));
        File.WriteAllText(Path.Combine(root,"tests/output/direct-brimdeck-contract.json"),JsonSerializer.Serialize(new{passed=failures==0,count=Results.Count,failures,results=Results},Bridge.Json.Options));
        return failures==0?0:1;
    }
    static async Task Live(string root)
    {
        var cfg=new ConfigStore(Path.Combine(root,"tests/live-state")).Load();
        using var client=new CpampClient(cfg);var inventory=await client.Quotas(DateTimeOffset.UtcNow,CancellationToken.None);
        var account=inventory.Accounts.First(a=>a.Provider=="codex"&&a.Status!="disabled"&&a.Windows.Any(w=>w.Name.StartsWith("5h")));
        using var handler=new HttpClientHandler {AllowAutoRedirect=false,UseProxy=false};using var http=new HttpClient(handler){Timeout=Timeout.InfiniteTimeSpan};
        var cpa=new ConfigStore(Path.Combine(root,"tests/output/cpa-state")).Load();
        using var direct=new ConfiguredProviders(new Secrets(SecretStore.Read(cpa.ProtectedAdminKey)),http);
        var read=await direct.TestAsync(Entry(cpa.CpampUrl,Scripts.DirectAccount(account.CredentialName,account.MemberEmail,account.WorkspaceId)));
        Check("LIVE CPA direct official quota without Bridge or CPAMP proxy",read.Snapshot.LiveQuota&&read.Snapshot.Metrics.Count>0&&read.Snapshot.Metrics.Any(m=>m.UsedPercent.HasValue));
        if(!read.Snapshot.LiveQuota) System.Console.WriteLine("Quota diagnostic: "+read.Snapshot.Status);
        read=await direct.TestAsync(Entry(cpa.CpampUrl,Scripts.Read("BrimDeck-CPA-Direct-Statistics.js")));
        Check("LIVE CPA8 missing full statistics reports unsupported without partial fallback",!read.Snapshot.LiveQuota&&read.Snapshot.Status.Contains("不支持完整原生统计"));
        using var cpamp=new ConfiguredProviders(new Secrets(SecretStore.Read(cfg.ProtectedAdminKey)),http);
        read=await cpamp.TestAsync(Entry(cfg.CpampUrl,Scripts.DirectAccount(account.CredentialName,account.MemberEmail,account.WorkspaceId,true)));
        Check("LIVE direct CPAMP snapshot and runtime merge without Bridge",read.Snapshot.LiveQuota&&read.Snapshot.Metrics.Any(m=>m.UsedPercent.HasValue));
        read=await cpamp.TestAsync(Entry(cfg.CpampUrl,Scripts.Read("BrimDeck-CPAMP-Direct-Statistics.js")));
        Check("LIVE CPAMP direct full statistics without Bridge",read.Snapshot.LiveQuota&&read.Snapshot.Metrics.Count==8);
        // Native responses may change as requests arrive. Compare one fixed range to the official aggregation engine.
        var stats=await client.Statistics(DateTimeOffset.UtcNow,CancellationToken.None);
        var dashboard=await client.Dashboard(stats.Today.StartMs,stats.Today.EndMs,CancellationToken.None);
        var expected=CpampClient.ParseStats(dashboard.GetProperty("today"),stats.Today.StartMs,stats.Today.EndMs,"official");
        Check("LIVE CPAMP fixed-range reconciliation",stats.Today.TotalTokens==expected.TotalTokens&&stats.Today.TotalRequests==expected.TotalRequests&&stats.Today.EstimatedCost==expected.EstimatedCost);
        cfg.Accounts=[new AccountChoice{Id=account.Id,Selected=true,Windows=account.Windows.Select(w=>w.Id).ToList()}];cfg.QuotaSource="official";
        using var official=new CpampClient(cfg);var fresh=await official.Quotas(DateTimeOffset.UtcNow,CancellationToken.None);
        var selected=fresh.Accounts.Single(a=>a.Id==account.Id);
        Check("LIVE Bridge client selected official refresh",selected.RefreshError is null&&selected.Windows.Any(w=>w.Source=="CPA official wham/usage"&&!w.Stale));
    }
}
