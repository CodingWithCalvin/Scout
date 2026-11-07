using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Scout.Core;

if(args.Length!=2)throw new ArgumentException("Scout.VerificationBaseline <completed new benchmark> <isolated baseline output>");
var source=Path.GetFullPath(args[0]);var root=Path.GetFullPath(args[1]);Directory.CreateDirectory(root);
var assemblyHash=Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(typeof(DialogueMatcher).Assembly.Location)));
if(assemblyHash!="9877de1e683f5c508fcc7b8ad3c60410cc4450b094f0af80606e975461e2c4e8")throw new InvalidOperationException("This control requires the frozen pre-change published core assembly.");
using var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(source,"benchmark.db"),Mode=SqliteOpenMode.ReadOnly}.ToString());db.Open();
JsonObject Read(string suffix){using var cmd=db.CreateCommand();cmd.CommandText="select content from documents where path=$path";cmd.Parameters.AddWithValue("$path",Path.GetFullPath(Path.Combine(source,"data",suffix)));if(cmd.ExecuteScalar() is not byte[] content)throw new InvalidDataException($"Missing benchmark document: {suffix}");return JsonNode.Parse(content)!.AsObject();}
if(Read("status.json").Text("state")!="complete")throw new InvalidOperationException("Run this control only after the new benchmark completes; no competing inference.");
var fixture=Read("fixture.json");var settings=fixture["settings"]!.Deserialize<AppSettings>(Data.Options)!;
if(settings.GpuLayers!=0||settings.ManagedThinking||settings.ThinkingEnabled!=false)throw new InvalidOperationException("CPU only, thinking disabled required.");
settings.AllowDownloads=false;settings.AutoDownloadMissing=false;var catalog=fixture["catalog"]!["episodes"]!.Deserialize<List<Episode>>(Data.Options)!;
string Store(string name)=>Path.Combine(root,"data",name);ScoutStorage.Register(Path.Combine(root,"data"),new ScoutDatabase(Path.Combine(root,"baseline.db")));
void Save(string name,object value)=>Data.Save(Store(name),value);
var manifest=JsonSerializer.SerializeToNode(new{coreSha256=assemblyHash,newManifest=Read("manifest.json"),cases=new[]{"case-01","case-07"},purpose="Paired CPU-only old-verifier control against the exact new verification cues and candidate IDs; no inference retuning.",manualCorrections=false},Data.Options)!;
if(ScoutStorage.Exists(Store("manifest.json"))&&!JsonNode.DeepEquals(Data.Read(Store("manifest.json")),manifest))throw new InvalidOperationException("Baseline configuration changed.");
if(ScoutStorage.Exists(Store("summary.json")))throw new InvalidOperationException("Baseline complete; no rerun.");Save("manifest.json",manifest);
var output=new JsonArray();
async Task WaitForIdleScout() {
    using var probe=new HttpClient{Timeout=TimeSpan.FromSeconds(3)};DateTimeOffset? idleSince=null;
    while(true) {
        bool idle=false;
        try {using var response=await probe.GetAsync("http://127.0.0.1:18765/slots");if(response.IsSuccessStatusCode)idle=JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray().All(s=>!s.Bool("is_processing"));}
        catch(HttpRequestException ex) when(ex.InnerException is SocketException socket&&socket.SocketErrorCode==SocketError.ConnectionRefused){return;}
        catch(HttpRequestException){}catch(TaskCanceledException){}
        if(idle){idleSince??=DateTimeOffset.UtcNow;if(DateTimeOffset.UtcNow-idleSince>=TimeSpan.FromSeconds(60))return;}else idleSince=null;
        Save("status.json",new{state="waiting",message="Waiting for the user's Scout model to be idle; no user process is changed.",updated=DateTimeOffset.UtcNow});
        await Task.Delay(TimeSpan.FromSeconds(15));
    }
}
foreach(var id in new[]{"case-01","case-07"}) {
    if(ScoutStorage.Exists(Store(id+".json"))){output.Add(Data.Read(Store(id+".json")));continue;}
    await WaitForIdleScout();
    var newer=Read($"cases/{id}.json");var record=new JsonObject{["case_id"]=id};
    if(newer["result"]?["verification"]?["candidate_ids"] is not JsonArray candidateIds)throw new InvalidDataException("New result lacks candidate shortlist.");
    var ids=candidateIds.Select(n=>n!.GetValue<int>()).ToHashSet();var shortlist=catalog.Where(e=>ids.Contains(e.Id)).ToList();
    var cues=fixture.Array("inputs").Single(i=>i.Text("case_id")==id)!["cues"]!.Deserialize<List<Cue>>(Data.Options)!;
    using(var socket=new Socket(AddressFamily.InterNetwork,SocketType.Stream,ProtocolType.Tcp)){socket.Bind(new IPEndPoint(IPAddress.Loopback,0));settings.LocalEndpoint=$"http://127.0.0.1:{((IPEndPoint)socket.LocalEndPoint!).Port}/v1/chat/completions";}
    Save("status.json",new{state="running",active=id,updated=DateTimeOffset.UtcNow,endpoint=settings.LocalEndpoint});
    await using var runtime=await LocalRuntime.StartAsync(settings,new ProgressCallback(Console.WriteLine),default);
    using var recorder=new Recorder((n,v)=>Save(id+"/"+n,v));using var http=new HttpClient(recorder){Timeout=TimeSpan.FromSeconds(settings.EffectiveTimeoutSeconds)};
    var matcher=new DialogueMatcher(http,settings.Inference());var watch=Stopwatch.StartNew();
    try{record["result"]=await matcher.DirectAsync(cues,shortlist,settings.Model,settings.Endpoint,true);}
    catch(Exception ex){record["error"]=ex.Message;}
    record["seconds"]=watch.Elapsed.TotalSeconds;record["requests"]=recorder.Records;record["new_requests"]=newer["requests"]!.DeepClone();record["new_result"]=newer["result"]!.DeepClone();
    Save(id+".json",record);output.Add(record);Console.WriteLine($"{id} original verifier: {watch.Elapsed.TotalMinutes:0.0} min");
}
Save("summary.json",new{cases=output,manualCorrections=false,comparison="Same cues, candidates, CPU model, thread count and thinking-off. Old verifier starts cold as in the prior workflow; new verifier uses its real preceding identification prefix. Initial identification time is excluded from both stage measurements."});
Save("status.json",new{state="complete",updated=DateTimeOffset.UtcNow});

sealed class ProgressCallback(Action<string> action):IProgress<string>{public void Report(string value)=>action(value);}
sealed class Recorder(Action<string,object> save):DelegatingHandler(new HttpClientHandler{AllowAutoRedirect=false}) {
    public JsonArray Records=[];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) {
        int n=Records.Count+1;var body=JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;save($"{n}-request.json",body);var watch=Stopwatch.StartNew();
        var response=await base.SendAsync(request,ct);var raw=JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;save($"{n}-response.json",raw);
        Records.Add(new JsonObject{["seconds"]=watch.Elapsed.TotalSeconds,["timings"]=raw["timings"]?.DeepClone(),["usage"]=raw["usage"]?.DeepClone()});return response;
    }
}
