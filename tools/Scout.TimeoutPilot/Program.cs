using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Scout.Core;

if(args.Length!=2)throw new ArgumentException("Usage: Scout.TimeoutPilot <input.json> <isolated output directory>");
var input=JsonNode.Parse(File.ReadAllText(args[0]))!.AsObject();var output=Path.GetFullPath(args[1]);
Directory.CreateDirectory(output);
var logical=Path.Combine(output,"data");ScoutStorage.Register(logical,new ScoutDatabase(Path.Combine(output,"test.db")));
string Store(string name)=>Path.Combine(logical,name);
void Save(string name,object value)=>Data.Save(Store(name),value);
if(ScoutStorage.Exists(Store("summary.json")))throw new InvalidOperationException("This test already has a final result. No inference was rerun.");
var settings=input["settings"]!.Deserialize<AppSettings>(Data.Options)!;
if(settings.Provider!="managed"||settings.GpuLayers!=0||settings.ManagedThinking||settings.ThinkingEnabled!=false)throw new InvalidDataException("This test requires Scout-managed CPU-only processing with thinking explicitly disabled.");
settings.AllowDownloads=false;settings.AutoDownloadMissing=false;
settings.Ffmpeg=Path.GetFullPath(@"C:\tools\ffmpeg.exe");settings.Ffprobe=Path.GetFullPath(@"C:\tools\ffprobe.exe");
settings.Validate();
if(settings.EffectiveTimeoutSeconds!=1800)throw new InvalidOperationException("The loaded Scout.Core does not have the expected 30-minute CPU timeout.");
var source=input.Text("video");var library=input.Text("library");
if(!File.Exists(source)||!PreviewPlanner.Within(source,library))throw new FileNotFoundException("Expected source episode is unavailable.");
object Snapshot()=>new{size=new FileInfo(source).Length,mtime=PreviewPlanner.MtimeNs(source),sample=PreviewPlanner.SampledDigest(source)};
var before=JsonSerializer.SerializeToNode(Snapshot())!;
var watch=Stopwatch.StartNew();var gate=new object();string phase="preparing";
void Status(string state,string message) {
    lock(gate){phase=message;Save("status.json",new{state,message,seconds=watch.Elapsed.TotalSeconds,updated=DateTimeOffset.UtcNow});}
    Console.WriteLine($"{DateTimeOffset.UtcNow:O} {message}");
}
var identity=input["catalog"]!["series_identity"]!.Deserialize<SeriesIdentity>(Data.Options)!;
Data.Save(Store("catalog.json"),input["catalog"]!);Save("input.json",input);
int port;using(var socket=new Socket(AddressFamily.InterNetwork,SocketType.Stream,ProtocolType.Tcp)) {
    socket.Bind(new IPEndPoint(IPAddress.Loopback,0));port=((IPEndPoint)socket.LocalEndPoint!).Port;
}
settings.LocalEndpoint=$"http://127.0.0.1:{port}/v1/chat/completions";
var manifest=new{started=DateTimeOffset.UtcNow,source,inputHash=CollectionResolver.Hash(input.ToJsonString()),coreSha256=Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(typeof(DialogueMatcher).Assembly.Location))),matcher=DialogueMatcher.Version,revision=DialogueMatcher.RequestRevision,settings.CpuThreads,settings.GpuLayers,thinking=settings.ManagedThinking,context=settings.EffectiveContextSize,outputTokens=settings.EffectiveOutputTokens,timeoutSeconds=settings.EffectiveTimeoutSeconds,previousTimeoutSeconds=settings.RequestTimeoutSeconds,runtime=RuntimeDownloads.Profile(settings).Id,endpoint=settings.LocalEndpoint,caseDeadlineMinutes=65,manualCorrections=false,mediaBefore=before};
Save("manifest.json",manifest);
using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(65));
using var heartbeatStop=new CancellationTokenSource();
async Task Heartbeat() {
    using var timer=new PeriodicTimer(TimeSpan.FromSeconds(30));
    try{while(await timer.WaitForNextTickAsync(heartbeatStop.Token))lock(gate)Save("heartbeat.json",new{phase,seconds=watch.Elapsed.TotalSeconds,updated=DateTimeOffset.UtcNow});}
    catch(OperationCanceledException) when(heartbeatStop.IsCancellationRequested){}
}
var heartbeat=Heartbeat();LocalRuntime? runtime=null;DialogueMatcher? matcher=null;
var requests=new List<JsonObject>();
using var trace=new RecordingHandler(Save,requests,message=>Status("processing",message));
using var http=new HttpClient(trace){Timeout=TimeSpan.FromSeconds(settings.EffectiveTimeoutSeconds)};
JsonObject summary=new(){["source"]=source,["cpu_only"]=true,["thinking"]=false,["request_timeout_seconds"]=settings.EffectiveTimeoutSeconds,["manual_corrections"]=false};
try {
    Status("verifying","Verifying the installed Gemma weights and CPU runtime.");
    var profile=ModelDownloads.Profiles.Single(p=>p.Id==settings.DownloadProfile);
    await using(var file=File.OpenRead(settings.ModelFile)) {
        if(file.Length!=profile.Bytes||Convert.ToHexStringLower(await SHA256.HashDataAsync(file,deadline.Token))!=profile.Sha256)throw new IOException("Installed model differs from the pinned profile.");
    }
    runtime=await LocalRuntime.StartAsync(settings,new ProgressCallback(m=>Status("loading",m)),deadline.Token);
    matcher=new DialogueMatcher(http,settings.Inference());
    Status("processing","Running the first selected recording through Scout's production media and matching pipeline.");
    var match=new MatchSettings(library,Store("catalog.json"),logical,settings.Model,settings.Endpoint,settings.Ffmpeg,settings.Ffprobe,SelectedFiles:[source],SeriesIdentity:identity);
    var reportPath=await new MediaPipeline(matcher).RunAsync(match,new ProgressCallback(m=>Status("processing",m)),deadline.Token);
    var report=Data.Read(reportPath);var row=report.Array("files").Single()!;
    var groups=CollectionResolver.Resolve(report);var group=groups.Single();
    summary["pipeline_completed"]=true;summary["error"]=row.Text("error");summary["group_status"]=group.Status;
    summary["episodes"]=JsonSerializer.SerializeToNode(group.Episodes,Data.Options);
    summary["assessment"]=row["reasoning"].Text("assessment");summary["coverage"]=row["reasoning"].Text("coverage");
    summary["verification_completed"]=row["reasoning"]?["verification"]!=null;
    summary["validation_retries"]=row["reasoning"].Array("validation_retries").Count+(row["reasoning"]?["verification"]?["initial_result"]).Array("validation_retries").Count;
    summary["timeout_error"]=row.Text("error_code")=="model_timeout";
}
catch(OperationCanceledException) when(deadline.IsCancellationRequested){summary["error"]="The isolated 65-minute test deadline was reached. No full-series work was started.";summary["test_deadline_exceeded"]=true;}
catch(Exception ex){summary["error"]=ex.Message;summary["failure_type"]=ex.GetType().Name;}
finally {
    heartbeatStop.Cancel();await heartbeat;
    if(runtime!=null)await runtime.DisposeAsync();
    summary["seconds"]=watch.Elapsed.TotalSeconds;summary["usage"]=matcher==null?null:JsonSerializer.SerializeToNode(matcher.Usage,Data.Options);
    var after=JsonSerializer.SerializeToNode(Snapshot())!;summary["media_unchanged"]=JsonNode.DeepEquals(before,after);Save("media-after.json",after);
    lock(requests)summary["requests"]=new JsonArray(requests.Select(r=>(JsonNode)r.DeepClone()).ToArray());
    summary["passed_previous_timeout"]=summary.Array("requests").Any(r=>r.Text("state")=="complete"&&r!["seconds"]!.GetValue<double>()>settings.RequestTimeoutSeconds);
    summary["success"]=summary.Bool("pipeline_completed")&&summary.Text("error")==""&&summary.Bool("verification_completed")&&summary.Bool("media_unchanged");
    Save("summary.json",summary);
    string E(string s)=>WebUtility.HtmlEncode(s);
    var html=new StringBuilder("<!doctype html><meta charset=utf-8><title>Scout CPU timeout test</title><style>body{font:16px Segoe UI;max-width:1100px;margin:36px auto;background:#111923;color:#eef4ff}pre{white-space:pre-wrap;background:#1c293b;padding:20px}h1{color:#8bc3ff}</style><h1>CPU-only timeout test</h1><p>One recording, production matcher and independent dialogue verification. Thinking disabled. No manual corrections or media edits. A supported model suggestion is not independently confirmed ground truth.</p><pre>");
    html.Append(E(summary.ToJsonString(Data.Options))).Append("</pre>");
    if(ScoutStorage.Exists(Store("report.json"))) {
        var row=Data.Read(Store("report.json")).Array("files").FirstOrDefault();
        if(row?["reasoning"]!=null)html.Append("<h2>Automated match review</h2><pre>").Append(E(row["reasoning"]!.ToJsonString(Data.Options))).Append("</pre>");
    }
    File.WriteAllText(Path.Combine(output,"results.html"),html.ToString());
    Status(summary.Bool("success")?"complete":"failed",summary.Bool("success")?"CPU-only first-episode test completed.":"Test did not produce a completed verified match: "+summary.Text("error"));
    Console.WriteLine(summary.ToJsonString(Data.Options));
}

sealed class ProgressCallback(Action<string> action):IProgress<string>{public void Report(string value)=>action(value);}
sealed class RecordingHandler(Action<string,object> save,List<JsonObject> records,Action<string> progress):DelegatingHandler(new HttpClientHandler{AllowAutoRedirect=false}) {
    int sequence;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) {
        var index=++sequence;var key=$"requests/{index:00}";
        var body=JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;save(key+"-request.json",body);
        var record=new JsonObject{["index"]=index,["state"]="running",["started"]=DateTimeOffset.UtcNow,["thinking"]=body["chat_template_kwargs"].Bool("enable_thinking"),["max_tokens"]=body.Int("max_tokens")};
        lock(records)records.Add(record);save(key+"-status.json",record);
        var watch=Stopwatch.StartNew();progress($"Model request {index} started; 30-minute production HTTP deadline.");
        HttpResponseMessage? response=null;
        try {
            // Preserve the application's non-streaming request and let its HttpClient enforce the deadline.
            response=await base.SendAsync(request,ct);
            var raw=await response.Content.ReadAsStringAsync(ct);save(key+"-response.json",JsonNode.Parse(raw)!);
            lock(records){record["state"]="complete";record["seconds"]=watch.Elapsed.TotalSeconds;record["http_status"]=(int)response.StatusCode;}
            save(key+"-status.json",record);progress($"Model request {index} returned in {watch.Elapsed.TotalSeconds:0.0} seconds.");
            return response;
        }catch(Exception ex) {
            response?.Dispose();lock(records){record["state"]="failed";record["seconds"]=watch.Elapsed.TotalSeconds;record["error_type"]=ex.GetType().Name;record["error"]=ex.Message;}
            save(key+"-status.json",record);throw;
        }
    }
}
