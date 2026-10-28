using Bridge;
using BrimDeck.Core;
using System.Net;
using System.Text.Json;
using System.Diagnostics;

sealed class Secrets(string key) : IProviderSecrets
{
    public string Read(Guid id) => key;
    public void Write(Guid id,string value) => throw new NotSupportedException();
    public void Delete(Guid id) => throw new NotSupportedException();
}
sealed class Fake : HttpMessageHandler
{
    public int Status=200;
    public string Body="{}";
    public bool Disconnect;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req,CancellationToken ct)
    {
        if(Disconnect)throw new HttpRequestException("fixture disconnect");
        return Task.FromResult(new HttpResponseMessage((HttpStatusCode)Status){Content=new StringContent(Body)});
    }
}
static class Program
{
    static readonly List<object> results=[];static int failures;
    static void Check(string name,bool pass){results.Add(new{name,passed=pass});Console.WriteLine((pass?"PASS ":"FAIL ")+name);if(!pass)failures++;}
    static AppEntry Entry(string script)=>new(){QuotaSource=ProviderId.Custom,Site="http://127.0.0.1:18731",Script=script};
    static async Task Main(string[] args)
    {
        string root=Path.GetFullPath(args[0]);
        try
        {
            var fake=new Fake();using var http=new HttpClient(fake);using var providers=new ConfiguredProviders(new Secrets("fixture-key"),http);
            string statsScript=Scripts.Read("BrimDeck-CPA-Statistics.js"),quotaScript=Scripts.Read("BrimDeck-CPA-Quotas.js");
            var s=new StatsSnapshot(DateTimeOffset.UtcNow,"Asia/Shanghai","calendar",new(1234567,.123456m,10,9,1,0,1,"fixture"),new(8640000,9.56m,10,9,1,0,1,"fixture"));
            fake.Body=JsonSerializer.Serialize(s,Bridge.Json.Options);var entry=Entry(statsScript);
            var read=await providers.TestAsync(entry);Check("installed ConfiguredProviders Jint parser statistics eight rows",read.Snapshot.LiveQuota&&read.Snapshot.Metrics.Count==8);
            Check("installed parser percent only success rate",read.Snapshot.Metrics.Count(m=>m.UsedPercent.HasValue)==1&&read.Snapshot.Metrics[7].UsedPercent==90);
            fake.Status=401;var error=await providers.TestAsync(entry);Check("installed native401 keeps stale previous",!error.Snapshot.LiveQuota&&error.Snapshot.IsStale&&error.Snapshot.Metrics.Count==8);
            fake.Status=500;error=await providers.TestAsync(entry);Check("installed native500 error",!error.Snapshot.LiveQuota&&error.Snapshot.IsStale);
            fake.Status=200;fake.Disconnect=true;error=await providers.TestAsync(entry);Check("installed disconnected failure",!error.Snapshot.LiveQuota);fake.Disconnect=false;
            fake.Body="{}";error=await providers.TestAsync(entry);Check("installed missing fields failure",!error.Snapshot.LiveQuota);
            fake.Body=JsonSerializer.Serialize(s,Bridge.Json.Options);read=await providers.TestAsync(entry);Check("installed reconnect success",read.Snapshot.LiveQuota&&!read.Snapshot.IsStale);
            var q=new QuotaSnapshot(DateTimeOffset.UtcNow,[new("safe","测试账号","codex","active",DateTimeOffset.UtcNow,[new("five-hour","5h",35,65,DateTimeOffset.UtcNow.AddHours(1),DateTimeOffset.UtcNow,false,"known","active","fixture"),new("weekly","Week",72,28,DateTimeOffset.UtcNow.AddDays(1),DateTimeOffset.UtcNow,false,"known","active","fixture")])]);
            fake.Body=JsonSerializer.Serialize(q,Bridge.Json.Options);read=await providers.TestAsync(Entry(quotaScript));
            Check("installed quota used35 used72 retained",read.Snapshot.LiveQuota&&read.Snapshot.Metrics.Count==2&&read.Snapshot.Metrics[0].UsedPercent==35&&read.Snapshot.Metrics[1].UsedPercent==72);
            Check("installed quota source success not mixed",read.Snapshot.Metrics.All(m=>!m.Label.Contains("成功")));
            fake.Body="{\"status\":\"ok\",\"accounts\":[]}";read=await providers.TestAsync(Entry(quotaScript));Check("installed empty selection nonempty text",read.Snapshot.LiveQuota&&read.Snapshot.Metrics.Count==1&&read.Snapshot.Metrics[0].UsedPercent is null);
            fake.Body=JsonSerializer.Serialize(q,Bridge.Json.Options);read=await providers.TestAsync(Entry(Scripts.Account(new string('a',64))));Check("installed generated independent script",read.Snapshot.LiveQuota&&read.Snapshot.Metrics[0].UsedPercent==35);
            Check("installed native maximum application columns seven",AppPresets.MaximumApps==7);
            await Live(root,args[1]);
        }
        catch(Exception ex){Check("unhandled "+ex.GetType().Name+" "+ex.Message,false);Console.WriteLine(ex.StackTrace);}
        await File.WriteAllTextAsync(Path.Combine(root,"tests/output/brimdeck-contract.json"),JsonSerializer.Serialize(new{passed=failures==0,count=results.Count,failures,results},Bridge.Json.Options));
        Environment.ExitCode=failures==0?0:1;
    }
    static async Task Live(string root,string exe)
    {
        var liveStore=new ConfigStore(Path.Combine(root,"tests/live-state"));var cfg=liveStore.Load();
        using var client=new CpampClient(cfg);var q=await client.Quotas(DateTimeOffset.UtcNow,CancellationToken.None);
        var accounts=q.Accounts.Where(a=>a.Provider=="codex"&&a.Windows.Any(w=>w.Name.StartsWith("5h"))&&a.Windows.Any(w=>w.Name.StartsWith("Week"))).Take(2).ToList();
        Check("real CPAMP two Codex accounts have5h/weekly snapshots",accounts.Count==2);
        cfg.Accounts=accounts.Select((a,i)=>new AccountChoice {Id=a.Id,Selected=true,Alias="验证Codex-"+(i+1),Order=i,Windows=a.Windows.Where(w=>w.Name.StartsWith("5h")||w.Name.StartsWith("Week")).Select(w=>w.Id).ToList()}).ToList();cfg.Port=18735;
        var testStore=new ConfigStore(Path.Combine(root,"tests/output/live-selected-state"));testStore.Save(cfg);
        var start=new ProcessStartInfo(exe){UseShellExecute=false,CreateNoWindow=true};foreach(var a in new[]{"--serve","--seconds","25","--state-dir",testStore.DirectoryPath})start.ArgumentList.Add(a);
        using var process=Process.Start(start)!;
        using var http=new HttpClient(new HttpClientHandler{UseProxy=false}){Timeout=TimeSpan.FromSeconds(5)};string key=SecretStore.Read(cfg.ProtectedBridgeKey);http.DefaultRequestHeaders.Authorization=new("Bearer",key);
        for(int i=0;i<50;i++){try{using var r=await http.GetAsync("http://127.0.0.1:18735/v1/quotas");if(r.StatusCode==HttpStatusCode.OK)break;}catch(HttpRequestException){}await Task.Delay(150);}
        string json=await http.GetStringAsync("http://127.0.0.1:18735/v1/quotas");using var document=JsonDocument.Parse(json);
        var sourceAccounts=document.RootElement.GetProperty("accounts");
        Check("published EXE real subset selection2 of14",sourceAccounts.GetArrayLength()==2&&q.Accounts.Count>2);
        using var providers=new ConfiguredProviders(new Secrets(key),http);
        var entry=Entry(Scripts.Read("BrimDeck-CPA-Quotas.js"));entry.Site="http://127.0.0.1:18735";
        var read=await providers.TestAsync(entry);var expected=sourceAccounts.EnumerateArray().SelectMany(a=>a.GetProperty("windows").EnumerateArray()).Select(w=>w.GetProperty("usedPercent").GetDouble()).ToArray();
        Check("published EXE→installed Jint→native quota parser real values",read.Snapshot.LiveQuota&&read.Snapshot.Metrics.Select(m=>m.UsedPercent).SequenceEqual(expected.Select(x=>(double?)x)));
        entry=Entry(Scripts.Read("BrimDeck-CPA-Statistics.js"));entry.Site="http://127.0.0.1:18735";read=await providers.TestAsync(entry);
        Check("published EXE→installed statistics script test connection",read.Snapshot.LiveQuota&&read.Snapshot.Metrics.Count==8);
        entry=Entry(Scripts.Account(accounts[0].Id));entry.Site="http://127.0.0.1:18735";read=await providers.TestAsync(entry);
        Check("published EXE independent real selected account source",read.Snapshot.LiveQuota&&read.Snapshot.Metrics.Count==2);
        // Retain numeric evidence with test aliases and safe ids; no emails, tokens, or credential filenames.
        await File.WriteAllTextAsync(Path.Combine(root,"tests/output/live-quotas.json"),json);
        var stats=await http.GetStringAsync("http://127.0.0.1:18735/v1/stats");await File.WriteAllTextAsync(Path.Combine(root,"tests/output/live-stats.json"),stats);
        await process.WaitForExitAsync();Check("published EXE graceful headless shutdown",process.ExitCode==0);
    }
}
