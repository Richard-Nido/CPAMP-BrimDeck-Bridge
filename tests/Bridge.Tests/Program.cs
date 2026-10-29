using Bridge;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

static class Test
{
    static readonly List<object> Results = [];
    static string Root = "";
    static int failures;
    static void Check(string name, bool result)
    {
        Results.Add(new { name, passed = result }); Console.WriteLine((result ? "PASS " : "FAIL ") + name); if (!result) failures++;
    }
    static void Throws(string name, Action action) { try { action(); Check(name, false); } catch { Check(name, true); } }
    static JsonElement E(string text) => JsonDocument.Parse(text).RootElement.Clone();
    static int FreePort() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); int p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }
    static async Task Main(string[] args)
    {
        Root = Path.GetFullPath(args[0]); string output = Path.Combine(Root, "tests/output"); Directory.CreateDirectory(output);
        try { await Run(args[1]); }
        catch (Exception ex) { Check("unhandled " + ex.GetType().Name + " " + ex.Message, false); Console.WriteLine(ex.StackTrace); }
        await File.WriteAllTextAsync(Path.Combine(output, "dotnet-tests.json"), JsonSerializer.Serialize(new { passed = failures == 0, count = Results.Count, failures, results = Results }, Bridge.Json.Options));
        Environment.ExitCode = failures == 0 ? 0 : 1;
    }
    static async Task Run(string python)
    {
        var cfg = new BridgeConfig { TimezoneId = "China Standard Time" };
        var bounds = TimeWindows.Get(DateTimeOffset.Parse("2026-11-01T02:00:00Z"), cfg);
        Check("calendar crosses month UTC/local boundary", bounds.Today == DateTimeOffset.Parse("2026-10-31T16:00:00Z").ToUnixTimeMilliseconds() && bounds.Week == DateTimeOffset.Parse("2026-10-25T16:00:00Z").ToUnixTimeMilliseconds());
        bounds = TimeWindows.Get(DateTimeOffset.Parse("2027-01-01T00:01:00Z"), cfg);
        Check("calendar crosses year", bounds.Week == DateTimeOffset.Parse("2026-12-25T16:00:00Z").ToUnixTimeMilliseconds());
        cfg.SevenDayMode = "rolling"; bounds = TimeWindows.Get(DateTimeOffset.Parse("2026-11-01T02:00:00Z"), cfg);
        Check("rolling exactly 168h", bounds.End - bounds.Week == 168L * 3600 * 1000);
        cfg.SevenDayMode = "calendar"; cfg.TimezoneId = "Eastern Standard Time"; bounds = TimeWindows.Get(DateTimeOffset.Parse("2026-11-02T12:00:00Z"), cfg);
        Check("DST calendar midnight", bounds.Week == DateTimeOffset.Parse("2026-10-27T04:00:00Z").ToUnixTimeMilliseconds() && bounds.Today == DateTimeOffset.Parse("2026-11-02T05:00:00Z").ToUnixTimeMilliseconds());
        cfg.TimezoneId = "";
        var no = CpampClient.ParseStats(E("{\"total_calls\":0,\"success_calls\":0,\"failure_calls\":0,\"total_tokens\":0,\"total_cost\":0}"), 0, 1, "fixture");
        Check("zero requests N/A and zero tokens", no.TotalTokens == 0 && no.SuccessRate is null);
        foreach (var pair in new[] { (3L, 0L), (0L, 3L), (250L, 6L) })
        {
            var s = CpampClient.ParseStats(E(JsonSerializer.Serialize(new { total_calls = pair.Item1 + pair.Item2, success_calls = pair.Item1, failure_calls = pair.Item2, total_tokens = 123456789012345L, total_cost = .1234567890123456789m })), 0, 1, "fixture");
            Check($"success/failure {pair.Item1}/{pair.Item2}", s.SuccessRate == pair.Item1 * 100m / (pair.Item1 + pair.Item2));
            Check("decimal cost exact and incomplete prices explicit", s.EstimatedCost == .1234567890123456789m && !s.CostComplete);
        }
        Throws("missing JSON not zero", () => CpampClient.ParseStats(E("{}"), 0, 1, "fixture"));
        Throws("unknown classification rejected", () => CpampClient.ParseStats(E("{\"total_calls\":3,\"success_calls\":1,\"failure_calls\":1,\"total_tokens\":5}"), 0, 1, "fixture"));
        var now = DateTimeOffset.UtcNow;
        JsonElement W(decimal? used, decimal? remaining, bool old = false) => E(JsonSerializer.Serialize(new { used_percent = used, remaining_percent = remaining, window_kind = "five_hour", provider_window_id = "five-hour", model_scope_kind = "all", observed_at_ms = now.AddMinutes(old ? -20 : 0).ToUnixTimeMilliseconds(), availability = "active", cycle_end_ms = now.AddHours(1).ToUnixTimeMilliseconds() }));
        Check("remaining65 becomes used35", CpampClient.ParseWindow(W(null, 65), now, 15).UsedPercent == 35);
        Check("used35 not inverted", CpampClient.ParseWindow(W(35, null), now, 15).UsedPercent == 35);
        foreach (decimal p in new[] { 0m, 100m }) Check("quota endpoint " + p, CpampClient.ParseWindow(W(p, null), now, 15).UsedPercent == p);
        Check("unknown quota stays null", CpampClient.ParseWindow(W(null, null), now, 15).Status == "unknown");
        Check("invalid quota no silent clamp", CpampClient.ParseWindow(W(120, null), now, 15).Status == "invalid_percent");
        Check("inconsistent remaining rejected", CpampClient.ParseWindow(W(35, 35), now, 15).UsedPercent is null);
        Check("old snapshot stale", CpampClient.ParseWindow(W(35, 65, true), now, 15).Stale);
        Check("old config defaults to CPAMP snapshots", new BridgeConfig().QuotaSource == "cpamp");
        var mixed = E(JsonSerializer.Serialize(new { used_percent = 6, remaining_percent = 94, observed_at_ms = now.ToUnixTimeMilliseconds(),
            field_sources = new { quota = new { observed_at_ms = now.AddMinutes(-20).ToUnixTimeMilliseconds() } }, window_kind = "five_hour", provider_window_id = "five-hour", model_scope_kind = "family", model_scope_key = "codex_main",
            cycle_end_ms = now.AddHours(-1).ToUnixTimeMilliseconds(), current_cycle = new { scheduled_end_ms = now.AddHours(1).ToUnixTimeMilliseconds() } }));
        var mixedWindow = CpampClient.ParseWindow(mixed, now, 15);
        Check("CPAMP quota field timestamp wins over fresh metadata", mixedWindow.UpdatedAt?.ToUnixTimeMilliseconds() == now.AddMinutes(-20).ToUnixTimeMilliseconds() && mixedWindow.Stale);
        Check("CPAMP current cycle reset wins over legacy cycle end", mixedWindow.ResetAt?.ToUnixTimeMilliseconds() == now.AddHours(1).ToUnixTimeMilliseconds());
        var official = E("{\"rate_limit\":{\"primary_window\":{\"used_percent\":6,\"limit_window_seconds\":18000,\"reset_after_seconds\":3600},\"secondary_window\":{\"used_percent\":17,\"limit_window_seconds\":604800,\"reset_after_seconds\":86400}}}");
        var officialWindows = CodexQuota.Parse(official, now, 15);
        Check("official percentages and stable CPAMP scope IDs", officialWindows.Count == 2 && officialWindows[0].Id == "five-hour|family|codex_main" && officialWindows[0].UsedPercent == 6);
        Check("official relative reset uses observation time", officialWindows[0].ResetAt == DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds() + 3600));
        var runtimeFile = E(JsonSerializer.Serialize(new { quota = new { observed_at = now.ToString("O"), signals = new Dictionary<string,string> {
            ["X-Codex-Active-Limit"]="premium",["X-Codex-Primary-Used-Percent"]="6",["X-Codex-Primary-Window-Minutes"]="300",["X-Codex-Primary-Reset-At"]=now.AddHours(1).ToUnixTimeSeconds().ToString() } } }));
        var runtimeWindows = CodexQuota.Runtime(runtimeFile, now, 15);
        Check("CPAMP runtime header quota exact percent", runtimeWindows.Count == 1 && runtimeWindows[0].UsedPercent == 6);
        Check("newer runtime replaces stale persisted same scope", CodexQuota.Merge([mixedWindow], runtimeWindows)[0].UsedPercent == 6 && !CodexQuota.Merge([mixedWindow], runtimeWindows)[0].Stale);
        Check("older runtime never overwrites newer snapshot", CodexQuota.Merge([runtimeWindows[0] with { UsedPercent = 7, UpdatedAt = now.AddSeconds(1) }],runtimeWindows)[0].UsedPercent == 7);
        Check("inactive lifecycle never revived", CodexQuota.Merge([mixedWindow with { Status = "inactive_window" }],runtimeWindows)[0].Status == "inactive_window");
        Check("legacy all-scope window selection ID retained",CodexQuota.Merge([mixedWindow with {Id="five-hour|all|",UsedPercent=1}],runtimeWindows) is { Count:1 } legacy && legacy[0].Id=="five-hour|all|"&&legacy[0].UsedPercent==6);
        Check("runtime fills missing snapshot percent timestamp",CodexQuota.Merge([mixedWindow with {UpdatedAt=null,UsedPercent=null}],runtimeWindows)[0].UsedPercent==6);
        Check("main window selection survives source scope transition",BridgeRuntime.SameCodexWindow("five-hour|all|","five-hour|family|codex_main"));
        Check("scoped additional windows never become main windows",!BridgeRuntime.SameCodexWindow("five-hour|family|spark","five-hour|family|codex_main"));
        Check("member conflicts fail closed", CpampClient.Member(E("{\"account\":\"a@example.test\",\"email\":\"b@example.test\"}")) == "");
        Check("safe stable ID conceals account", CpampClient.SafeId("codex", "secret@example.test").Length == 64 && CpampClient.SafeId("codex", "x") == CpampClient.SafeId("codex", "x"));
        var q = new QuotaSnapshot(now, [new("a", "duplicate", "codex", "ready", now, [new("5h", "5h", 35,65,now.AddHours(1),now,false,"known","active","fixture"),new("week", "Week",72,28,now.AddDays(1),now,false,"known","active","fixture")]), new("b","duplicate","codex","disabled",now,[]) ]);
        cfg.Accounts = [new() { Id="b", Selected=true, Alias="乙", Order=0 },new() {Id="a",Selected=true,Alias="甲",Order=1,Windows=["5h"]}];
        var selected = BridgeRuntime.Selected(q,cfg);
        Check("partial selection ordering alias duplicate names", selected.Accounts[0].Name == "乙" && selected.Accounts[1].Name == "甲");
        Check("5h only selection", selected.Accounts[1].Windows.Count == 1 && selected.Accounts[1].Windows[0].Name == "5h");
        cfg.Accounts[1].Windows.Add("week"); Check("5h and weekly selection", BridgeRuntime.Selected(q,cfg).Accounts[1].Windows.Count == 2);
        cfg.Accounts[0].Selected=false; Check("unselect supported", BridgeRuntime.Selected(q,cfg).Accounts.Count == 1);
        cfg.Accounts[1].Id="new-identity"; Check("reauthorization does not cross attach", BridgeRuntime.Selected(q,cfg).Accounts[0].Status == "missing_identity_reselect_required");
        var store = new ConfigStore(Path.Combine(Root,"tests/output/config"));
        cfg.ProtectedBridgeKey=SecretStore.Protect("bridge-test-secret"); cfg.ProtectedAdminKey=SecretStore.Protect("admin-test-secret");
        store.Save(cfg); var loaded=store.Load();
        Check("DPAPI roundtrip distinct secrets",SecretStore.Read(loaded.ProtectedBridgeKey)=="bridge-test-secret" && SecretStore.Read(loaded.ProtectedAdminKey)=="admin-test-secret");
        Check("config persistence selections",loaded.Accounts[1].Alias=="甲" && loaded.Accounts[1].Windows.Count==2);
        Check("config no plaintext secret",!File.ReadAllText(Path.Combine(store.DirectoryPath,"settings.json")).Contains("test-secret"));
        cfg.CpampUrl="http://192.168.1.2:18317"; Throws("remote CPAMP configuration rejected",cfg.Validate); cfg.CpampUrl="http://127.0.0.1:18317";
        await Sqlite(python);
        await ServerTests();
        CardTests();
    }
    static async Task Sqlite(string python)
    {
        var psi=new ProcessStartInfo(python) { RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false,CreateNoWindow=true };
        psi.ArgumentList.Add("-u");psi.ArgumentList.Add(Path.Combine(Root,"tests/sqlite_fixture.py")); psi.ArgumentList.Add(Path.Combine(Root,"tests/output/fixture.sqlite"));
        using var process=Process.Start(psi)!; Check("SQLite fixture ready",await process.StandardOutput.ReadLineAsync()=="ready");
        async Task Send(object command) { await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command));await process.StandardInput.FlushAsync();if(await process.StandardOutput.ReadLineAsync()!="ok")throw new Exception("fixture protocol"); }
        var cfg=new BridgeConfig {DatabaseFile=Path.Combine(Root,"tests/output/fixture.sqlite")};
        using(var db=ReadOnlyDb.Open(cfg))
        {
            Check("SQLite empty zero",db.Statistics(100,200).TotalRequests==0);
            await Send(new {action="insert",time=100,tokens=10,failed=0});await Send(new {action="insert",time=199,tokens=20,failed=1,model="deleted-account-model-b"});await Send(new {action="insert",time=200,tokens=1000,failed=0});
            var s=db.Statistics(100,200);Check("SQLite WAL live + half-open exact",s.TotalTokens==30 && s.TotalRequests==2 && s.FailedRequests==1);
            Check("retry same request_id retained official event semantics",s.TotalRequests==2);
            Check("SQLite no pricing fake zero",s.EstimatedCost is null);
            Throws("SQLite write protection",()=>db.Query("delete from usage_events returning timestamp_ms"));
            await Send(new {action="lock"}); Check("SQLite simultaneous WAL writer read",db.Statistics(100,200).TotalTokens==30); await Send(new {action="unlock"});
            await Send(new {action="insert",time=150,tokens=5,failed=2}); Throws("SQLite unknown status rejected",()=>db.Statistics(100,200));
            await Send(new {action="clear"});await Send(new {action="archive"});Throws("SQLite archived missing history rejected",()=>db.Statistics(100,200));
        }
        await Send(new {action="clear"});await Send(new {action="journal_delete"});
        using(var db=ReadOnlyDb.Open(cfg))
        {
            await Send(new {action="lock"});Throws("SQLite temporary exclusive lock fails explicitly",()=>db.Statistics(100,200));await Send(new {action="unlock"});
            Check("SQLite lock recovery",db.Statistics(100,200).TotalRequests==0);
        }
        await process.StandardInput.WriteLineAsync("{\"action\":\"exit\"}");await process.WaitForExitAsync();
        cfg.DatabaseFile=Path.Combine(Root,"tests/output/moved.sqlite");Throws("database moved error",()=>ReadOnlyDb.Open(cfg));
    }
    static async Task ServerTests()
    {
        int mockPort=FreePort(), bridgePort=FreePort(); int fail=0, requests=0, officialRequests=0, officialStatus=200, snapshotPercent=35; bool runtimeTelemetry=false; string officialIndex="changed-index";
        var builder=WebApplication.CreateSlimBuilder();builder.Logging.ClearProviders();builder.WebHost.UseKestrel(o=>o.Listen(IPAddress.Loopback,mockPort));
        await using var mock=builder.Build();
        mock.MapPost("/v0/management/monitoring/analytics",async (HttpContext c)=>
        { Interlocked.Increment(ref requests); if(fail!=0){c.Response.StatusCode=fail;return;} await c.Response.WriteAsJsonAsync(new {summary=new {total_calls=10,success_calls=9,failure_calls=1,total_tokens=123456,total_cost=.123456789m}}); });
        mock.MapGet("/v0/management/auth-files",()=>new {files=new[] {new {provider="codex",name="fixture.json",auth_index="changed-index",account="a@example.test",email="a@example.test",status="ready",disabled=false,
            quota=runtimeTelemetry?new {observed_at=DateTimeOffset.UtcNow.ToString("O"),signals=new Dictionary<string,string>{["X-Codex-Primary-Used-Percent"]="47",["X-Codex-Primary-Window-Minutes"]="300",["X-Codex-Primary-Reset-After-Seconds"]="3600"}}:null}}});
        mock.MapPost("/v0/management/api-call",async(HttpContext c)=>
        { Interlocked.Increment(ref officialRequests); using var d=await JsonDocument.ParseAsync(c.Request.Body); officialIndex=d.RootElement.Get("authIndex").Str();
          await c.Response.WriteAsJsonAsync(new {status_code=officialStatus,body="{\"rate_limit\":{\"primary_window\":{\"used_percent\":12,\"limit_window_seconds\":18000,\"reset_after_seconds\":3600}}}"}); });
        mock.MapPost("/v0/management/quota-snapshots/query",async(HttpContext c)=>
        {using var doc=await JsonDocument.ParseAsync(c.Request.Body);string row=doc.RootElement.Get("accounts")[0].Get("row_key").Str();await c.Response.WriteAsJsonAsync(new {items=new[] {new {row_key=row,account_key="stable-cpamp-key",windows=new[] {new {provider_window_id="five-hour",window_kind="five_hour",model_scope_kind="all",used_percent=snapshotPercent,remaining_percent=100-snapshotPercent,observed_at_ms=(runtimeTelemetry?DateTimeOffset.UtcNow.AddMinutes(-1):DateTimeOffset.UtcNow).ToUnixTimeMilliseconds(),availability="active"}}}}});});
        await mock.StartAsync();
        var cfg=new BridgeConfig {CpampUrl=$"http://127.0.0.1:{mockPort}",Port=bridgePort,ProtectedAdminKey=SecretStore.Protect("fixture-admin"),ProtectedBridgeKey=SecretStore.Protect("fixture-bridge")};
        await using var runtime=new BridgeRuntime(cfg);await runtime.Start();await runtime.Refresh(true);
        for(int i=0;runtime.Statistics is null&&i<50;i++)await Task.Delay(100);
        using var http=new HttpClient(new HttpClientHandler {UseProxy=false}){BaseAddress=new Uri($"http://127.0.0.1:{bridgePort}"),Timeout=TimeSpan.FromSeconds(3)};
        Check("missing bridge key 401",(await http.GetAsync("/health")).StatusCode==HttpStatusCode.Unauthorized);
        http.DefaultRequestHeaders.Authorization=new("Bearer","wrong");Check("wrong bridge key 401",(await http.GetAsync("/v1/stats")).StatusCode==HttpStatusCode.Unauthorized);
        http.DefaultRequestHeaders.Authorization=new("Bearer","fixture-bridge");
        Check("health authenticated",(await http.GetAsync("/health")).StatusCode==HttpStatusCode.OK);
        using(var req=new HttpRequestMessage(HttpMethod.Get,"/health")){req.Headers.Add("Origin","https://example.test");Check("cross site Origin denied",(await http.SendAsync(req)).StatusCode==HttpStatusCode.Forbidden);}
        using(var req=new HttpRequestMessage(HttpMethod.Get,"/health")){req.Headers.Host="example.test:"+bridgePort;Check("DNS rebinding Host denied",(await http.SendAsync(req)).StatusCode==HttpStatusCode.Forbidden);}
        Check("no mutation route",(await http.PostAsync("/v1/stats",new StringContent(""))).StatusCode==HttpStatusCode.MethodNotAllowed);
        string text=await http.GetStringAsync("/v1/stats");Check("API no management secrets",!text.Contains("fixture-admin")&&!text.Contains("fixture-bridge"));
        Check("empty selection API empty accounts",E(await http.GetStringAsync("/v1/quotas")).Get("accounts").GetArrayLength()==0);
        Check("unselected account endpoint 404",(await http.GetAsync("/v1/quotas/unknown")).StatusCode==HttpStatusCode.NotFound);
        int before=requests;await Task.WhenAll(Enumerable.Range(0,15).Select(_=>http.GetStringAsync("/v1/stats")));Check("concurrent clients share cached stats",before==requests);
        await Task.Delay(250);long wakes=runtime.BackgroundWakeups;await Task.Delay(2200);Check("background scheduler does not poll every second",runtime.BackgroundWakeups==wakes);
        Check("background delay follows next refresh deadline",runtime.NextRefreshDelay(DateTimeOffset.UtcNow)>TimeSpan.FromSeconds(10));
        Check("fresh statistics reuse snapshot instance",ReferenceEquals(runtime.Statistics,runtime.Statistics));
        Check("fresh quotas reuse snapshot instance",ReferenceEquals(runtime.Inventory,runtime.Inventory));
        Check("HTTP JSON compact",!text.Contains("\n  "));
        fail=500;await runtime.Refresh(true);Check("source 500 retains stale snapshot",runtime.Statistics is {Stale:true}&&runtime.Statistics.Today.TotalTokens==123456);
        fail=401;await runtime.Refresh(true);Check("source 401 explicit error",runtime.StatisticsError.Contains("401"));
        fail=0;await runtime.Refresh(true);Check("source reconnect clears stale",runtime.Statistics is {Stale:false});
        Check("quota official snapshot used35",runtime.Inventory?.Accounts[0].Windows[0].UsedPercent==35);
        var c=JsonSerializer.Deserialize<BridgeConfig>(JsonSerializer.Serialize(cfg,Bridge.Json.Options),Bridge.Json.Options)!;c.Accounts=[new(){Id=runtime.Inventory!.Accounts[0].Id,Selected=true,Alias="测试账号",Windows=[runtime.Inventory.Accounts[0].Windows[0].Id]}];await runtime.UpdateConfig(c);
        var quota=E(await http.GetStringAsync("/v1/quotas"));Check("selected quota alias through HTTP",quota.Get("accounts")[0].Get("name").Str()=="测试账号");
        string safe=c.Accounts[0].Id;Check("single selected account route",(await http.GetAsync("/v1/quotas/"+safe)).StatusCode==HttpStatusCode.OK);
        Check("snapshot mode never calls official endpoint",officialRequests==0);
        c.QuotaSource="official";await runtime.UpdateConfig(c);await runtime.Refresh(true);
        for(int i=0;runtime.Inventory is null&&i<50;i++)await Task.Delay(50);
        Check("official mode queries only selected with current auth index",officialRequests>0&&officialIndex=="changed-index"&&runtime.Inventory?.Accounts[0].Windows[0].UsedPercent==12);
        Check("saved legacy window selection survives official source switch through HTTP",E(await http.GetStringAsync("/v1/quotas")).Get("accounts")[0].Get("windows").GetArrayLength()==1);
        officialStatus=429;await runtime.Refresh(true);
        Check("official429 retains last official value as stale per account",runtime.Inventory?.Accounts[0] is { RefreshError:not null } a && a.RefreshError.Contains("429")&&a.Windows[0].UsedPercent==12&&a.Windows[0].Stale);
        Check("health reports partial per-account official failures",E(await http.GetStringAsync("/health")).Get("quotas").Str()=="partial");
        officialStatus=200;await runtime.Refresh(true);Check("official reconnect clears per-account error",runtime.Inventory?.Accounts[0].RefreshError is null&&runtime.Inventory?.Accounts[0].Windows[0].Stale==false);
        c.Accounts[0].Selected=false;await runtime.UpdateConfig(c);int officialBefore=officialRequests;await runtime.Refresh(true);
        Check("official unselected accounts never queried",officialRequests==officialBefore);
        c.QuotaSource="cpamp";c.Accounts[0].Selected=true;await runtime.UpdateConfig(c);await runtime.Refresh(true);
        Check("switch back to CPAMP restores snapshots",runtime.Inventory?.Accounts[0].Windows[0].UsedPercent==35);
        runtimeTelemetry=true;
        using(var mergedClient=new CpampClient(c))
        {
            var mergedInventory=await mergedClient.Quotas(DateTimeOffset.UtcNow,CancellationToken.None);
            Check("client automatically merges newer auth-files quota into persisted snapshot",mergedInventory.Accounts[0].Windows.Count==1&&mergedInventory.Accounts[0].Windows[0].UsedPercent==47&&mergedInventory.Accounts[0].Windows[0].Source=="CPAMP auth-files quota");
        }
        runtimeTelemetry=false;
        var rotated=JsonSerializer.Deserialize<BridgeConfig>(JsonSerializer.Serialize(c,Bridge.Json.Options),Bridge.Json.Options)!;
        rotated.ProtectedBridgeKey=SecretStore.Protect("rotated-bridge");await runtime.UpdateConfig(rotated);
        Check("cached authentication old key rejected after rotation",(await http.GetAsync("/health")).StatusCode==HttpStatusCode.Unauthorized);
        http.DefaultRequestHeaders.Authorization=new("Bearer","rotated-bridge");Check("cached authentication new key accepted",(await http.GetAsync("/health")).StatusCode==HttpStatusCode.OK);
        Check("listener only loopback",System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Where(a=>a.Port==bridgePort).All(a=>IPAddress.IsLoopback(a.Address)));
        await runtime.DisposeAsync(); // explicit close tested before scope disposal below; guard expected in runtime.
        var automaticConfig=new BridgeConfig {CpampUrl=cfg.CpampUrl,Port=FreePort(),QuotaSeconds=30,ProtectedAdminKey=cfg.ProtectedAdminKey,ProtectedBridgeKey=cfg.ProtectedBridgeKey};
        await using(var automatic=new BridgeRuntime(automaticConfig))
        {
            await automatic.Start();for(int i=0;automatic.Inventory is null&&i<50;i++)await Task.Delay(50);
            snapshotPercent=43;var deadline=DateTimeOffset.UtcNow.AddSeconds(34);
            while(automatic.Inventory?.Accounts[0].Windows[0].UsedPercent!=43&&DateTimeOffset.UtcNow<deadline)await Task.Delay(200);
            Check("background automatically observes updated CPAMP snapshot without manual refresh",automatic.Inventory?.Accounts[0].Windows[0].UsedPercent==43);
        }
        try {await http.GetAsync("/health");Check("exit closes HTTP",false);}catch(HttpRequestException){Check("exit closes HTTP",true);}
    }
    static void CardTests()
    {
        Exception? error=null;
        var thread=new Thread(()=>
        {
            try
            {
                Application.EnableVisualStyles();
                var now=DateTimeOffset.UtcNow;
                var editor=new AccountEditor {Id="fixture",Name="示例账号 A",Provider="codex",Status="active",Updated="10-28 12:00",Windows=[new("5h","5h · codex_main",35,65,now.AddHours(3),now,false,"known","active","fixture"),new("week","Week · codex_main",5,95,now.AddDays(6),now,false,"known","active","fixture")],WindowIds=["5h","week"]};
                // Fixed-pixel fixture: the real MainForm handles DPI; this checks card geometry independently of runner font scaling.
                using var host=new Form {AutoScaleMode=AutoScaleMode.None,Width=1120,Height=520,BackColor=UiStyle.Canvas,Text="卡片布局测试 · 示例数据",Font=UiStyle.Body};
                using var card=new QuotaCard(editor) {Location=new Point(10,40),Width=525};host.Controls.Add(card);
                host.Controls.Add(new Label {Text="布局测试 · 以下为合成示例数据",AutoSize=true,Location=new Point(15,10)});
                card.EditForTest(true,"工作账号",3);card.WindowForTest("week").IsSelected=false;
                Check("card edits selection alias order and windows",editor.Selected&&editor.Alias=="工作账号"&&editor.Order==3&&editor.WindowIds.SequenceEqual(["5h"]));
                editor.Windows=editor.Windows.Select(w=>w with {UsedPercent=50,RemainingPercent=50}).ToList();card.UpdateData(now);
                Check("card refresh preserves unsaved edits",editor.Selected&&editor.Alias=="工作账号"&&editor.Order==3&&!card.WindowForTest("week").IsSelected);
                var second=new AccountEditor {Id="fixture-b",Name="示例账号 B",Provider="codex",Status="active",Updated="10-28 12:00",Windows=[new("5h","5h · codex_main",3,97,now.AddHours(2),now,false,"known","active","fixture"),new("week","Week · codex_main",0,100,now.AddDays(6),now,false,"known","active","fixture")],WindowIds=["5h","week"]};
                using var card2=new QuotaCard(second) {Location=new Point(550,40),Width=525};host.Controls.Add(card2);
                host.ShowInTaskbar=false;host.StartPosition=FormStartPosition.Manual;host.Location=new Point(-20000,-20000);host.Show();Application.DoEvents();host.PerformLayout();
                Check("cards do not overlap in two-column layout",card.Right<card2.Left&&card.Top==card2.Top&&card.Width==card2.Width);
                Check("friendly quota windows",UiStyle.WindowName("Week · codex_main")=="周限额");
                Check("relative reset labels",UiStyle.ResetText(now.AddHours(3),now)=="3 小时后"&&UiStyle.ResetText(now.AddDays(6),now)=="6 天后");
                using var metric=new MetricTile("今日Token"){Width=260,Height=110};metric.PerformLayout();Check("metric value uses card width",metric.ValueWidth==228);
                using var bitmap=new Bitmap(host.Width,host.Height);host.DrawToBitmap(bitmap,new Rectangle(Point.Empty,host.Size));bitmap.Save(Path.Combine(Root,"tests/output/cards-fixture.png"));
            }
            catch(Exception ex){error=ex;}
        });thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();if(error is not null)throw error;
    }
}
