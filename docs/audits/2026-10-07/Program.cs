using System.Diagnostics;
using System.Net;
using System.Text.Json;
using F1Hue.Core;
using F1Hue.Infrastructure;
using Microsoft.Data.Sqlite;

static JsonElement J(string s) => JsonSerializer.Deserialize<JsonElement>(s);
static void Out(string probe, object result) => Console.WriteLine(JsonSerializer.Serialize(new { probe, result }, JsonDefaults.Options));
if(args.Contains("--archives")) {
    foreach(var scenario in ReplayCatalog.Scenarios) {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try {
            var rows=await ReplayCatalog.ArchiveAsync(scenario.Id,deadline.Token);
            Out("official-archive",new{ scenario.Id, events=rows.Length, flags=rows.Select(x=>x.Flag.ToString()).Distinct().ToArray() });
        } catch(Exception error) { Out("official-archive",new{ scenario.Id,error=error.Message });Environment.ExitCode=1; }
    }
    return;
}
var root = Path.Combine(Path.GetTempPath(), "f1hue-audit-probe-" + Guid.NewGuid());
Directory.CreateDirectory(root);
try {
    var snapshot = J("""{"SessionInfo":{"Key":1,"Name":"Race","Type":"Race"},"SessionStatus":{"Status":"Started"},"TrackStatus":{"Status":"4"},"RaceControlMessages":{"Messages":{"1":{"Category":"SafetyCar","Message":"SAFETY CAR IN THIS LAP","Utc":"2026-10-07T12:00:00Z"}}}}""");
    var parser = new F1Parser();
    parser.Snapshot(snapshot);
    var initial = parser.State.LastFlag;
    parser.Update("RaceControlMessages", snapshot.GetProperty("RaceControlMessages"));
    Out("snapshot-sc-ending", new { initial, afterDelta = parser.State.LastFlag });

    var migration = Path.Combine(root,"migration");
    using (var s = new Store(migration)) {}
    using (var db = new SqliteConnection($"Data Source={migration}/f1-hue.sqlite3;Pooling=False")) {
        db.Open(); using var cmd=db.CreateCommand();cmd.CommandText="PRAGMA user_version=99";cmd.ExecuteNonQuery();
        using (var s = new Store(migration)) { s.Save(new AppSettings { SchemaVersion = 99 }); }
        cmd.CommandText="PRAGMA user_version"; Out("schema-downgrade", new { before=99, after=cmd.ExecuteScalar() });
    }

    using (var s = new Store(Path.Combine(root,"journal"))) {
        var feed = new F1Feed(s); var delivered=0; feed.Event += e=> {if(e.Kind=="flag")delivered++;};
        feed.Simulate("SessionStatus", J("""{"Status":"Started"}"""));
        using var db=new SqliteConnection($"Data Source={s.DirectoryPath}/f1-hue.sqlite3;Pooling=False");db.Open();
        using var cmd=db.CreateCommand();cmd.CommandText="CREATE TRIGGER audit_disk_failure BEFORE INSERT ON events BEGIN SELECT RAISE(FAIL,'audit simulated journal write failure'); END;";cmd.ExecuteNonQuery();
        string? error=null;try{feed.Simulate("TrackStatus",J("""{"Status":"5"}"""));}catch(Exception e){error=e.GetType().Name;}
        cmd.CommandText="DROP TRIGGER audit_disk_failure";cmd.ExecuteNonQuery();
        feed.Simulate("TrackStatus",J("""{"Status":"5"}"""));
        Out("journal-failure",new { error, deliveredFlags=delivered, feed.State.Connected, feed.State.LastFlag, feed.State.LastError });
    }

    using (var s = new Store(Path.Combine(root,"single"))) {
        var handler=new OneLight(); using var client=new HueClient(new MemoryVault(),(_,_,_)=>new HttpClient(handler){BaseAddress=new Uri("https://192.168.1.10/")});
        var output=new HueOutput(client,s);var settings=new AppSettings{ LightIds=[OneLight.Id] };
        await output.ApplyAsync(RaceFlag.GREEN,settings.Effects[RaceFlag.GREEN],settings,CancellationToken.None);
        handler.Calls.Clear();handler.IgnoreWrites=true;
        await output.RestoreAsync(CancellationToken.None);
        Out("single-restore",new { requests=handler.Calls, stillGreen=handler.Green, recoveryDeleted=s.Get<JsonElement?>("pending_restore") is null });
    }

    using(var s=new Store(Path.Combine(root,"perf"))) {
        double[] Bench(){var times=new List<double>();for(var i=0;i<31;i++){var sw=Stopwatch.StartNew();s.Read();s.Events(limit:60);s.Sessions();if(i>0)times.Add(sw.Elapsed.TotalMilliseconds);}return times.Order().ToArray();}
        var empty=Bench();
        using(var db=new SqliteConnection($"Data Source={s.DirectoryPath}/f1-hue.sqlite3;Pooling=False")){
            db.Open();using var tx=db.BeginTransaction();using var cmd=db.CreateCommand();cmd.Transaction=tx;
            cmd.CommandText="INSERT INTO events(session_key,received_at,body) VALUES($s,$at,$body)";
            var session=cmd.Parameters.AddWithValue("$s","1");cmd.Parameters.AddWithValue("$at",DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$body",JsonSerializer.Serialize(new RaceEvent("flag","GREEN","1","Audit race","Race",DateTimeOffset.UtcNow,0),JsonDefaults.Options));
            for(var i=0;i<100000;i++){session.Value=(i/500).ToString();cmd.ExecuteNonQuery();}tx.Commit();
        }
        var full=Bench();Out("state-db-cost-ms",new{ emptyP50=empty[15],fullP50=full[15],fullP95=full[28],rows=100000,iterations=30 });
    }
} finally { Directory.Delete(root,true); }

sealed class MemoryVault : IBridgeVault {
    public BridgeCredentials? Read()=>new("192.168.1.10","audit-non-secret",CertificatePin:"audit-pin");
    public void Save(BridgeCredentials c){} public void Clear(){}
}
sealed class OneLight : HttpMessageHandler {
    public const string Id="11111111-1111-1111-1111-111111111111";
    public bool Green,IgnoreWrites; public List<string> Calls=[];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){
        Calls.Add(request.Method+" "+request.RequestUri!.AbsolutePath);
        if(request.Method==HttpMethod.Put&&!IgnoreWrites)Green=true;
        var json=request.Method==HttpMethod.Get
            ? JsonSerializer.Serialize(new{ errors=Array.Empty<object>(),data=new[]{new{id=Id,type="light",id_v1="/lights/1",on=new{on=true},dimming=new{brightness=50},color=new{xy=new{x=Green?.17:.45,y=Green?.70:.20}}}}})
            : "{\"errors\":[],\"data\":[]}";
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(json)});
    }
}
