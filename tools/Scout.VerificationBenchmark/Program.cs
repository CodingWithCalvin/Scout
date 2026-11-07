using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Scout.Core;

if(args.Length is <2 or >3||args.Length==3&&args[2]!="--finalize")throw new ArgumentException("Scout.VerificationBenchmark <frozen fixture.json> <isolated output> [--finalize]");
bool finalizeOnly=args.Length==3;
var inputText=File.ReadAllText(args[0]);var fixture=JsonNode.Parse(inputText)!.AsObject();var root=Path.GetFullPath(args[1]);Directory.CreateDirectory(root);
using var mutex=new Semaphore(1,1,"Local\\ScoutVerificationBenchmark-"+CollectionResolver.Hash(root)[..24]);
if(!mutex.WaitOne(0))throw new InvalidOperationException("Benchmark already running.");
var storeRoot=Path.Combine(root,"data");ScoutStorage.Register(storeRoot,new ScoutDatabase(Path.Combine(root,"benchmark.db")));
string Store(string name)=>Path.Combine(storeRoot,name);
void Save(string name,object value)=>Data.Save(Store(name),value);
if(!finalizeOnly&&ScoutStorage.Exists(Store("summary.json")))throw new InvalidOperationException("This benchmark is complete; no inference rerun.");
var settings=fixture["settings"]!.Deserialize<AppSettings>(Data.Options)!;
if(settings.Provider!="managed"||settings.GpuLayers!=0||settings.ThinkingEnabled!=false||settings.ManagedThinking)throw new InvalidDataException("CPU-only with explicit thinking-off is required.");
settings.AllowDownloads=false;settings.AutoDownloadMissing=false;settings.MaxRequestsPerRun=100;
var catalog=fixture["catalog"]!["episodes"]!.Deserialize<List<Episode>>(Data.Options)!;
var inputs=fixture.Array("inputs");var labels=fixture.Array("labels");
var manifest=JsonSerializer.SerializeToNode(new{inputHash=CollectionResolver.Hash(inputText),coreSha256=Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(typeof(DialogueMatcher).Assembly.Location))),
    matcher=DialogueMatcher.Version,revision=DialogueMatcher.RequestRevision,settings.CpuThreads,settings.GpuLayers,thinking=settings.ManagedThinking,context=settings.EffectiveContextSize,
    outputTokens=settings.EffectiveOutputTokens,timeoutSeconds=settings.EffectiveTimeoutSeconds,runtime=RuntimeDownloads.Profile(settings).Id,model=settings.LocalModel,fullSwaCache=settings.ManagedFullSwaCache,manualCorrections=false,mediaAccess=false,appDatabaseAccess=false},Data.Options)!;
if(ScoutStorage.Exists(Store("manifest.json"))&&!JsonNode.DeepEquals(Data.Read(Store("manifest.json")),manifest))throw new InvalidOperationException("Configuration changed; use another output directory.");
Save("manifest.json",manifest);Save("fixture.json",fixture);
var wall=Stopwatch.StartNew();var gate=new object();string phase="preparing";string active="";
void Status(string state,string message){lock(gate){phase=message;Save("status.json",new{state,message,active,updated=DateTimeOffset.UtcNow,seconds=wall.Elapsed.TotalSeconds});}Console.WriteLine($"{DateTimeOffset.UtcNow:O} {message}");}
using var stopHeartbeat=new CancellationTokenSource();
async Task Heartbeat(){using var timer=new PeriodicTimer(TimeSpan.FromSeconds(30));try{while(await timer.WaitForNextTickAsync(stopHeartbeat.Token))lock(gate)Save("heartbeat.json",new{phase,active,updated=DateTimeOffset.UtcNow,seconds=wall.Elapsed.TotalSeconds});}catch(OperationCanceledException){}}
var heartbeat=Heartbeat();LocalRuntime? runtime=null;
// Do not compete with the user's running Scout or change it to obtain a timing result.
var existingEndpoint=new Uri(settings.LocalEndpoint).GetLeftPart(UriPartial.Authority);
async Task WaitForIdleScout() {
    using var probe=new HttpClient{Timeout=TimeSpan.FromSeconds(3)};DateTimeOffset? idleSince=null;bool announced=false;
    while(true) {
        bool idle=false,absent=false;
        try {
            using var response=await probe.GetAsync(existingEndpoint+"/slots");
            if(response.IsSuccessStatusCode) {
                var slots=JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray();
                idle=slots.Count>0&&slots.All(s=>!s.Bool("is_processing"));
            }
        }catch(HttpRequestException ex) when(ex.InnerException is SocketException socket&&socket.SocketErrorCode==SocketError.ConnectionRefused){absent=true;}
        catch(HttpRequestException){}catch(TaskCanceledException){}
        if(absent)return;
        if(idle){idleSince??=DateTimeOffset.UtcNow;if(DateTimeOffset.UtcNow-idleSince>=TimeSpan.FromSeconds(60))return;}
        else idleSince=null;
        if(!announced){Status("waiting","Waiting for the user's Scout model to be idle for 60 seconds; no user processing is stopped or changed.");announced=true;}
        await Task.Delay(TimeSpan.FromSeconds(15));
    }
}
try {
    if(finalizeOnly&&inputs.Any(i=>!ScoutStorage.Exists(Store($"cases/{i.Text("case_id")}.json"))))throw new InvalidOperationException("Finalization requires all case checkpoints; no inference was started.");
    if(!finalizeOnly) {
    await WaitForIdleScout();
    Status("preparing","Verifying installed Gemma model and CPU runtime.");
    var profile=ModelDownloads.Profiles.Single(p=>p.Id==settings.DownloadProfile);
    await using(var stream=File.OpenRead(settings.ModelFile))if(stream.Length!=profile.Bytes||Convert.ToHexStringLower(await SHA256.HashDataAsync(stream))!=profile.Sha256)throw new IOException("Installed model checksum mismatch.");
    using(var socket=new Socket(AddressFamily.InterNetwork,SocketType.Stream,ProtocolType.Tcp)){socket.Bind(new IPEndPoint(IPAddress.Loopback,0));settings.LocalEndpoint=$"http://127.0.0.1:{((IPEndPoint)socket.LocalEndPoint!).Port}/v1/chat/completions";}
    Save("runtime.json",new{endpoint=settings.LocalEndpoint,started=DateTimeOffset.UtcNow});
    runtime=await LocalRuntime.StartAsync(settings,new ProgressCallback(m=>Status("loading",m)),default);
    }
    using var recorder=new Recorder(Save,m=>Status("inference",m));using var http=new HttpClient(recorder){Timeout=TimeSpan.FromSeconds(settings.EffectiveTimeoutSeconds)};
    var matcher=new DialogueMatcher(http,settings.Inference());var exact=new Dictionary<string,JsonObject>();var results=new JsonArray();int consecutiveErrors=0;
    foreach(var input in inputs) {
        active=input.Text("case_id");var path=Store($"cases/{active}.json");var cues=input!["cues"]!.Deserialize<List<Cue>>(Data.Options)!;
        var hash=CollectionResolver.Hash(JsonSerializer.Serialize(cues,Data.Options));var label=labels.Single(l=>l.Text("case_id")==active)!;
        if(label.Text("dialogue_sha256")!=input.Text("dialogue_sha256"))throw new InvalidDataException("Label and evidence hash mismatch.");
        if(ScoutStorage.Exists(path)){var old=Data.Read(path);results.Add(old);if(old["result"] is JsonObject saved)exact[hash]=saved.DeepClone().AsObject();continue;}
        if(finalizeOnly)throw new InvalidOperationException("Missing checkpoint; finalization never performs inference.");
        await WaitForIdleScout();
        Status("inference",$"{active} · {results.Count+1}/{inputs.Count}: fresh identification and compact verification.");
        var watch=Stopwatch.StartNew();recorder.StartCase(active);var record=new JsonObject{["case_id"]=active,["dialogue_hash"]=hash};
        try {
            if(exact.TryGetValue(hash,out var same)){record["result"]=same.DeepClone();record["reused_identical_dialogue"]=true;}
            else {using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(70));var result=await matcher.IdentifyAsync(cues,catalog,settings.Model,settings.Endpoint,deadline.Token);record["result"]=result;exact[hash]=result.DeepClone().AsObject();}
            var actual=record["result"].Array("matches").Select(m=>m.Int("episode_id")).Order().ToArray();var expected=label.Array("expected_ids").Select(n=>n!.GetValue<int>()).Order().ToArray();
            record["agrees_with_reference"]=actual.SequenceEqual(expected)&&(expected.Length>0||record["result"].Text("assessment")==label.Text("expected_kind"));
            var saved=fixture["saved_results"].Array("cases").Single(r=>r.Text("case_id")==active)!["result"]!;
            record["agrees_with_saved_gemma"]=actual.SequenceEqual(saved.Array("matches").Select(m=>m.Int("episode_id")).Order())&&record["result"].Text("assessment")==saved.Text("assessment");
            consecutiveErrors=0;
        } catch(Exception ex){record["error"]=ex.Message;record["error_type"]=ex.GetType().Name;if(ex.Data["model_attempts"] is string attempts)record["model_attempts"]=JsonNode.Parse(attempts);consecutiveErrors++;}
        record["seconds"]=watch.Elapsed.TotalSeconds;record["requests"]=new JsonArray(recorder.Records.Select(r=>(JsonNode)r.DeepClone()).ToArray());
        Save($"cases/{active}.json",record);results.Add(record);Save("results.json",new{cases=results});
        Status("inference",$"{active} completed in {watch.Elapsed.TotalMinutes:0.0} minutes; error: {record.Text("error")}");
        if(record.Text("error")!=""&&fixture.Array("preflight_case_ids").Any(n=>n!.GetValue<string>()==active))throw new IOException("Preflight case failed validation or inference; the remaining suite was not started. Inspect saved raw output; no prediction was corrected.");
        if(consecutiveErrors>=2)throw new IOException("Two consecutive case errors; benchmark stopped. Inspect saved cases for the precise failures.");
    }
    // Report rows may be normalized into SQLite file_results rather than embedded
    // in the original documents row. These saved identity proofs are scoring-only.
    var proofRows=ScoutStorage.Exists(Store("recording-proofs.json"))?Data.Read(Store("recording-proofs.json")).Array("rows"):fixture["saved_report"].Array("files");
    if(proofRows.Count!=inputs.Count)throw new InvalidDataException("Saved recording identity proofs are missing; inference checkpoints are preserved.");
    var report=new JsonObject{["files"]=new JsonArray(),["manual_corrections"]=false};
    foreach(var record in results) {
        var id=record.Text("case_id");var label=labels.Single(l=>l.Text("case_id")==id)!;var original=proofRows.Single(r=>r.Text("path")==label.Text("path"))!;
        var row=new JsonObject{["path"]=label["path"]!.DeepClone(),["primary_fingerprint"]=original["primary_fingerprint"]?.DeepClone(),["matching_method"]="reasoning",["candidates"]=new JsonArray()};
        if(record!["result"] is JsonObject result){row["reasoning"]=result.DeepClone();row["dialogue"]=new JsonObject{["cues"]=inputs.Single(i=>i.Text("case_id")==id)!["cues"]!.DeepClone()};foreach(var m in result.Array("matches"))row.Array("candidates").Add(new JsonObject{["episode"]=JsonSerializer.SerializeToNode(catalog.Single(e=>e.Id==m.Int("episode_id")),Data.Options)});}
        else row["error"]=record.Text("error");report.Array("files").Add(row);
    }
    var groups=CollectionResolver.Resolve(report);Save("report.json",report);Save("groups.json",groups);
    var unique=results.Where(r=>!r.Bool("reused_identical_dialogue")).ToList();var calls=unique.SelectMany(r=>r.Array("requests")).ToList();
    var verify=calls.Where(r=>r.Text("stage")=="verify"&&r.Text("state")=="complete").ToList();
    Save("summary.json",new{cases=results.Count,uniqueDialogues=results.Select(r=>r.Text("dialogue_hash")).Distinct().Count(),freshEvaluations=unique.Count,reused=results.Count-unique.Count,errors=results.Count(r=>r.Text("error")!=""),referenceAgreement=results.Count(r=>r.Bool("agrees_with_reference")),
        savedGemmaAgreement=results.Count(r=>r.Bool("agrees_with_saved_gemma")),supported=results.Count(r=>r?["result"].Text("assessment")=="supported"),uncertain=results.Count(r=>r?["result"].Text("assessment")=="uncertain"),nonEpisode=results.Count(r=>r?["result"].Text("assessment")=="non_episode"),
        acceptedGroups=groups.Count(g=>g.Episode!=null),conflictGroups=groups.Count(g=>g.Status=="unresolved_conflict"),heldGroups=groups.Count(g=>g.Episode==null),
        seconds=results.Sum(r=>r!["seconds"]!.GetValue<double>()),verificationSeconds=verify.Sum(r=>r!["seconds"]!.GetValue<double>()),cachedVerificationTokens=verify.Sum(r=>r.Int("cached_tokens")),
        verificationInputTokens=verify.Sum(r=>r.Int("prompt_tokens")),verificationOutputTokens=verify.Sum(r=>r.Int("output_tokens")),verificationCalls=verify.Count,verificationCacheHits=verify.Count(r=>r.Int("cached_tokens")>0),
        validationRetries=calls.Count(r=>r.Bool("validation_retry")),manualCorrections=false,caveat=fixture.Text("limitations")});
    Status("complete","All saved difficult cases completed; Scout results and media were never accessed.");
}catch(Exception ex){Status("failed",ex.ToString());Environment.ExitCode=1;}
finally {stopHeartbeat.Cancel();await heartbeat;if(runtime!=null)await runtime.DisposeAsync();mutex.Release();}

sealed class ProgressCallback(Action<string> action):IProgress<string>{public void Report(string value)=>action(value);}
sealed class Recorder(Action<string,object> save,Action<string> progress):DelegatingHandler(new HttpClientHandler{AllowAutoRedirect=false}) {
    string active="";public List<JsonObject> Records=[];
    public void StartCase(string id){active=id;Records.Clear();}
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) {
        var body=JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;string stage=body["response_format"]?["json_schema"]?["schema"]?["properties"]?["checks"]!=null?"verify":"initial";
        var index=Records.Count+1;var key=$"requests/{active}/{index:00}";save(key+"-request.json",body);
        var task=JsonNode.Parse(body["messages"]![1].Text("content"))!.Text("task");
        var record=new JsonObject{["index"]=index,["stage"]=stage,["state"]="running",["started"]=DateTimeOffset.UtcNow,["thinking"]=body["chat_template_kwargs"].Bool("enable_thinking"),["validation_retry"]=task.Contains("The previous response failed validation:",StringComparison.Ordinal)};Records.Add(record);save(key+"-status.json",record);
        progress($"{active} request {index}: {stage}");var watch=Stopwatch.StartNew();HttpResponseMessage? response=null;
        try {
            response=await base.SendAsync(request,ct);var text=await response.Content.ReadAsStringAsync(ct);var raw=JsonNode.Parse(text)!;save(key+"-response.json",raw);
            record["state"]="complete";record["seconds"]=watch.Elapsed.TotalSeconds;record["http_status"]=(int)response.StatusCode;
            record["prompt_tokens"]=raw["usage"].Int("prompt_tokens");record["cached_tokens"]=raw["usage"]?["prompt_tokens_details"].Int("cached_tokens")??0;record["output_tokens"]=raw["usage"].Int("completion_tokens");
            record["timings"]=raw["timings"]?.DeepClone();record["reasoning_chars"]=raw["choices"]?[0]?["message"].Text("reasoning_content").Length??0;
            save(key+"-status.json",record);progress($"{active} {stage} returned in {watch.Elapsed.TotalMinutes:0.0} minutes; cached {record.Int("cached_tokens")} tokens.");return response;
        }catch(Exception ex){response?.Dispose();record["state"]="failed";record["seconds"]=watch.Elapsed.TotalSeconds;record["error"]=ex.Message;save(key+"-status.json",record);throw;}
    }
}
