using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Scout.Core;
if(args.Length!=3)throw new ArgumentException("Scout.Benchmark <existing input base> <benchmark output> <model folder>");
var source=Path.GetFullPath(args[0]);var root=Path.GetFullPath(args[1]);Directory.CreateDirectory(root);
using var mutex=new Semaphore(1,1,"Local\\ScoutBenchmark-"+CollectionResolver.Hash(root)[..24]);
if(!mutex.WaitOne(0))throw new InvalidOperationException("Benchmark already running.");
ScoutStorage.Register(Path.Combine(root,"data"),new ScoutDatabase(Path.Combine(root,"benchmark.db")));
string Store(string name)=>Path.Combine(root,"data",name);
void Status(string stage,string message){Data.Save(Store("status.json"),new{stage,message,updated=DateTimeOffset.UtcNow});Console.WriteLine(message);}
var inputText=File.ReadAllText(Path.Combine(source,"work","stronger-13","inputs.json"));
var catalogPath=Path.Combine(source,"work","stronger-13","catalog.json");
var labelText=File.ReadAllText(Path.Combine(source,"outputs","victorious-stronger-13","reference-labels.json"));
var inputs=JsonNode.Parse(inputText)!.AsArray();var labels=JsonNode.Parse(labelText)!.AsArray();var catalog=Data.Catalog(catalogPath);
var sourceReport=Data.Read(Path.Combine(source,"outputs","victorious-verified-13","report.json"));
var modelDirectory=Path.GetFullPath(args[2]);
var settings=new AppSettings{ModelDirectory=modelDirectory,AllowDownloads=true,AllowExperimental=true,GpuLayers=8,CpuThreads=8,ContextSize=32768,MaxOutputTokens=8192};
var profiles=new[]{
 ModelDownloads.Profiles.Single(p=>p.Id=="qwen36-35b"),
 new ModelProfile("qwen36-27b","Qwen3.6 27B Q4_K_M","Qwen3.6-27B-Q4_K_M.gguf",16817244384,"5ed60d0af4650a854b1755bd392f9aef4872643dc25a254bc68043fa638392a0","https://huggingface.co/unsloth/Qwen3.6-27B-GGUF/resolve/82d411acf4a06cfb8d9b073a5211bf410bfc29bf/Qwen3.6-27B-Q4_K_M.gguf",false,"Benchmark candidate"),
 new ModelProfile("gemma4-31b","Gemma 4 31B Q4_K_M","gemma-4-31B-it-Q4_K_M.gguf",18323733440,"38bd64c852c4b460434cc7162fa9bdcf242faf86502581a754cb72956bb17f84","https://huggingface.co/unsloth/gemma-4-31B-it-GGUF/resolve/c1ac76e99d5513b141e8adde7288b85c3f9c32ec/gemma-4-31B-it-Q4_K_M.gguf",false,"Benchmark candidate")
};
var arms=new[]{new Arm("qwen35-off",0,false),new Arm("qwen35-thinking",0,true),new Arm("qwen27-thinking",1,true),new Arm("gemma31-thinking",2,true)};
var manifest=JsonSerializer.SerializeToNode(new{pipeline=DialogueMatcher.Version,revision=DialogueMatcher.RequestRevision,inputs=CollectionResolver.Hash(inputText),catalog=CollectionResolver.Hash(File.ReadAllText(catalogPath)),labels=CollectionResolver.Hash(labelText),profiles,arms,settings.ContextSize,settings.MaxOutputTokens,settings.CpuThreads,settings.GpuLayers,thinkingBudget=2048,cases=inputs.Count,manual_corrections=false},Data.Options)!;
if(ScoutStorage.Exists(Store("manifest.json"))&&!JsonNode.DeepEquals(Data.Read(Store("manifest.json")),manifest))throw new InvalidOperationException("Benchmark configuration changed; use a new output folder.");
Data.Save(Store("manifest.json"),manifest);Data.Save(Store("inputs.json"),inputs);Data.Save(Store("catalog.json"),new{episodes=catalog});Data.Save(Store("evaluation-labels.json"),labels);
using var downloadHttp=new HttpClient{Timeout=TimeSpan.FromHours(12)};
try {
 Status("preparing","Verifying existing model and downloading pinned comparison models.");
 var modelPaths=new List<string>();
 foreach(var profile in profiles) {
  var installed=Path.Combine(Environment.GetEnvironmentVariable("USERPROFILE")!,"AppData","Local","Scout","models",profile.FileName);
  if(File.Exists(installed)) {
   var existingSettings=new AppSettings{ModelDirectory=Path.GetDirectoryName(installed)!,AllowDownloads=true,AllowExperimental=true};
   modelPaths.Add(await ModelDownloads.DownloadAsync(profile,existingSettings,downloadHttp,new ProgressCallback<string>(m=>Status("download",m)),default));
  } else modelPaths.Add(await ModelDownloads.DownloadAsync(profile,settings,downloadHttp,new ProgressCallback<string>(m=>Status("download",m)),default));
 }
 var executable=await RuntimeDownloads.EnsureAsync(settings,downloadHttp,new ProgressCallback<string>(m=>Status("runtime",m)),default);
 Data.Save(Store("runtime.json"),new{executable,version=RuntimeDownloads.Version,backend="vulkan"});
 foreach(var arm in arms) {
  if(ScoutStorage.Exists(Store(arm.Id+"/summary.json")))continue;
  Status("loading","Loading "+profiles[arm.Profile].Name+(arm.Thinking?" with thinking":" without thinking"));
  await using var runtime=await Runtime.Start(executable,modelPaths[arm.Profile],arm,settings,m=>Status("loading",m));
  using var http=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromHours(2)};
  var matcher=new DialogueMatcher(http,new InferenceOptions{Provider="local",EnableThinking=arm.Thinking,MaxOutputTokens=settings.MaxOutputTokens,MaxRequests=120});
  var output=new JsonArray();var exact=new Dictionary<string,JsonObject>();
  foreach(var input in inputs) {
   var id=input.Text("case_id");var cues=input!["cues"]!.Deserialize<List<Cue>>(Data.Options)!;var label=labels.Single(l=>l.Text("case_id")==id)!;
   if(label.Text("dialogue_sha256")!=input.Text("dialogue_sha256"))throw new InvalidDataException("Label/dialogue identity mismatch.");
   var key=CollectionResolver.Hash(JsonSerializer.Serialize(cues,Data.Options));var casePath=Store(arm.Id+"/"+id+".json");
   if(ScoutStorage.Exists(casePath)){var old=Data.Read(casePath);if(old["result"] is JsonObject saved)exact[key]=saved.DeepClone().AsObject();output.Add(old);continue;}
   Status("inference",$"{arm.Id} · {id} · {output.Count+1}/{inputs.Count}: full dialogue plus independent verification.");
   var watch=Stopwatch.StartNew();var before=matcher.Usage;var record=new JsonObject{["case_id"]=id,["dialogue_hash"]=key};
   try {
    if(exact.TryGetValue(key,out var same)){record["result"]=same.DeepClone();record["reused_identical_dialogue"]=true;}
    else {var result=await matcher.IdentifyAsync(cues,catalog,arm.Id,runtime.Endpoint,default);record["result"]=result;exact[key]=result.DeepClone().AsObject();}
    var actual=record["result"].Array("matches").Select(m=>m.Int("episode_id")).Order().ToArray();var expected=label.Array("expected_ids").Select(n=>n!.GetValue<int>()).Order().ToArray();
    record["agrees_with_saved_reference"]=actual.SequenceEqual(expected)&&(expected.Length>0||record["result"].Text("assessment")==label.Text("expected_kind"));
   }catch(Exception ex){record["error"]=ex.Message;if(ex.Data["model_attempts"] is string attempts)record["model_attempts"]=JsonNode.Parse(attempts);}
   var after=matcher.Usage;record["seconds"]=watch.Elapsed.TotalSeconds;record["requests"]=after.Requests-before.Requests;record["input_tokens"]=after.InputTokens-before.InputTokens;record["output_tokens"]=after.OutputTokens-before.OutputTokens;
   Data.Save(casePath,record);output.Add(record);Data.Save(Store(arm.Id+"/results.json"),new{cases=output});BenchmarkReport.Render(root,Store,arms,inputs,catalog);
  }
  var report=new JsonObject{["files"]=new JsonArray(),["manual_corrections"]=false};
  foreach(var record in output) {
   var label=labels.Single(l=>l.Text("case_id")==record.Text("case_id"))!;var old=sourceReport.Array("files").Single(r=>r.Text("path")==label.Text("path"))!;
   var row=new JsonObject{["path"]=label["path"]!.DeepClone(),["primary_fingerprint"]=old["primary_fingerprint"]?.DeepClone(),["matching_method"]="reasoning",["candidates"]=new JsonArray()};
   if(record?["result"] is JsonObject result) {
    row["reasoning"]=result.DeepClone();row["dialogue"]=new JsonObject{["cues"]=inputs.Single(i=>i.Text("case_id")==record.Text("case_id"))!["cues"]!.DeepClone()};
    foreach(var match in result.Array("matches"))row.Array("candidates").Add(new JsonObject{["episode"]=JsonSerializer.SerializeToNode(catalog.Single(e=>e.Id==match.Int("episode_id")),Data.Options)});
   }else row["error"]=record.Text("error");
   report.Array("files").Add(row);
  }
  var groups=CollectionResolver.Resolve(report);
  Data.Save(Store(arm.Id+"/summary.json"),new{cases=output.Count,referenceAgreement=output.Count(r=>r.Bool("agrees_with_saved_reference")),errors=output.Count(r=>r.Text("error")!=""),abstentions=output.Count(r=>r!["result"].Text("assessment")=="uncertain"),conflictGroups=groups.Count(g=>g.Status=="unresolved_conflict"),seconds=output.Sum(r=>r!["seconds"]!.GetValue<double>()),usage=matcher.Usage,arm.Thinking,caveat="Inherited evaluation labels are not an independent human-verified accuracy audit."});
  Data.Save(Store(arm.Id+"/report.json"),report);Data.Save(Store(arm.Id+"/runtime-tail.json"),new{lines=runtime.Tail});BenchmarkReport.Render(root,Store,arms,inputs,catalog);
 }
 Status("complete","All comparisons complete. No Scout results or media files modified.");BenchmarkReport.Render(root,Store,arms,inputs,catalog);
}catch(Exception ex){Status("failed",ex.ToString());throw;}
finally{mutex.Release();}
