using System.Text.Json;
using System.Text.Json.Nodes;
using Scout.Core;
if(args.Length!=3)throw new ArgumentException("fallback input, output directory, model directory required");
var input=JsonNode.Parse(File.ReadAllText(args[0]))!;
var root=Path.GetFullPath(args[1]);Directory.CreateDirectory(root);
ScoutStorage.Register(Path.Combine(root,"data"),new ScoutDatabase(Path.Combine(root,"fallback.db")));
string Store(string name)=>Path.Combine(root,"data",name);
var jobs=input.Array("jobs");var catalog=input["catalog"]!.Deserialize<List<Episode>>(Data.Options)!;
var settings=new AppSettings{ContextSize=32768,MaxOutputTokens=8192,GpuLayers=8,CpuThreads=8};
var arm=new Arm("gemma31-thinking",0,true);
var models=Path.GetFullPath(args[2]);
var model=Path.Combine(models,"gemma-4-31B-it-Q4_K_M.gguf");
void Status(string state,string message){Data.Save(Store("status.json"),new{state,message,updated=DateTimeOffset.UtcNow});Console.WriteLine(message);}
if(jobs.All(j=>ScoutStorage.Exists(Store(j.Text("id")+".json")))){Status("complete","All fallback checkpoints already present.");return;}
try {
 Status("verifying","Verifying Gemma model.");
 await using(var f=File.OpenRead(model))if(Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(f))!="38bd64c852c4b460434cc7162fa9bdcf242faf86502581a754cb72956bb17f84")throw new IOException("Gemma checksum mismatch");
 await using var runtime=await Runtime.Start(Path.Combine(models,"runtimes","b11908-vulkan","llama-server.exe"),model,arm,settings,m=>Status("loading",m));
 using var http=new HttpClient{Timeout=TimeSpan.FromHours(2)};
 var matcher=new DialogueMatcher(http,new InferenceOptions{Provider="local",EnableThinking=true,MaxOutputTokens=8192,MaxRequests=2000});
 foreach(var job in jobs) {
  var id=job.Text("id");if(ScoutStorage.Exists(Store(id+".json")))continue;
  Status("inference","Gemma fallback: "+id);var timer=System.Diagnostics.Stopwatch.StartNew();var record=new JsonObject();
  try{record["result"]=await matcher.IdentifyAsync(job!["cues"]!.Deserialize<List<Cue>>(Data.Options)!,catalog,arm.Id,runtime.Endpoint,default);}
  catch(Exception ex){record["error"]=ex.Message;}
  record["seconds"]=timer.Elapsed.TotalSeconds;Data.Save(Store(id+".json"),record);
 }
 Status("complete","Gemma fallback complete.");
}catch(Exception ex){Status("failed",ex.Message);throw;}
