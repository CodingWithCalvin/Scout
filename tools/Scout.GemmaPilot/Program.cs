using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net;
using System.Text;
using Scout.Core;
if(args.Length==2&&args[0]=="--audit-saved") {
 var items=JsonNode.Parse(File.ReadAllText(args[1]))!.AsArray();var audit=new JsonArray();
 foreach(var item in items) {
  var record=new JsonObject {["id"]=item.Text("id")};
  try {
   if(item.Bool("quality_only"))ModelOutputQuality.ValidateExplanations(item!["response"]!.DeepClone().AsObject());
   else DialogueMatcher.Validate(item!["response"]!.DeepClone().AsObject(),DialogueMatcher.Blocks(item["cues"]!.Deserialize<List<Cue>>(Data.Options)!),item["catalog"]!.Deserialize<List<Episode>>(Data.Options)!,item.Bool("verify"));
   record["accepted"]=true;
  }
  catch(InvalidDataException ex){record["accepted"]=false;record["error"]=ex.Message;}
  audit.Add(record);
 }
 Console.WriteLine(audit.ToJsonString());return;
}
if(args.Length is not (3 or 4)||args.Length==4&&args[3]!="--cpu")throw new ArgumentException("Pilot input, isolated output directory, model directory, optional --cpu required");
var input=JsonNode.Parse(File.ReadAllText(args[0]))!;var root=Path.GetFullPath(args[1]);Directory.CreateDirectory(root);
ScoutStorage.Register(Path.Combine(root,"data"),new ScoutDatabase(Path.Combine(root,"pilot.db")));
string Store(string name)=>Path.Combine(root,"data",name);
void Save(string name,object value)=>Data.Save(Store(name),value);
void Status(string state,string message){Save("status.json",new{state,message,updated=DateTimeOffset.UtcNow});Console.WriteLine(message);}
var jobs=input.Array("jobs");var catalog=input["catalog"]!.Deserialize<List<Episode>>(Data.Options)!;
var settings=new AppSettings{ContextSize=32768,MaxOutputTokens=8192,GpuLayers=args.Length==4?0:8,CpuThreads=8};
var manifest=JsonSerializer.SerializeToNode(new{version=2,inputHash=CollectionResolver.Hash(input.ToJsonString()),thinking=false,context=32768,outputTokens=8192,gpuLayers=settings.GpuLayers,threads=8,caseDeadlineMinutes=40,matcher=DialogueMatcher.Version,revision=DialogueMatcher.RequestRevision,coreSha256=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(typeof(DialogueMatcher).Assembly.Location)))},Data.Options)!;
if(ScoutStorage.Exists(Store("manifest.json"))&&!JsonNode.DeepEquals(Data.Read(Store("manifest.json")),manifest))throw new InvalidOperationException("Pilot configuration changed; choose a fresh output directory.");
Save("manifest.json",manifest);Save("input.json",input);
var models=Path.GetFullPath(args[2]);var model=Path.Combine(models,"gemma-4-31B-it-Q4_K_M.gguf");
var results=new JsonArray();
try {
 Status("verifying","Verifying Gemma model for thinking-off pilot.");
 await using(var f=File.OpenRead(model))if(Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(f))!="38bd64c852c4b460434cc7162fa9bdcf242faf86502581a754cb72956bb17f84")throw new IOException("Gemma checksum mismatch");
 foreach(var job in jobs) {
  var id=job.Text("id");var path=Store(id+".json");if(ScoutStorage.Exists(path)){results.Add(Data.Read(path));continue;}
  var arm=new Arm("gemma31-off-pilot",0,false);
  await using var runtime=await Runtime.Start(Path.Combine(models,"runtimes/b11908-vulkan/llama-server.exe"),model,arm,settings,m=>Status("loading",m));
  using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(40));
  using var trace=new TracedTransport(id,Save);using var http=new HttpClient(trace){Timeout=Timeout.InfiniteTimeSpan};
  var matcher=new DialogueMatcher(http,new InferenceOptions{Provider="local",EnableThinking=false,MaxOutputTokens=8192,MaxRequests=8});
  Status("inference",$"Thinking off: {id}; 40-minute deadline includes identification and verification.");
  var watch=Stopwatch.StartNew();var record=new JsonObject{["id"]=id,["thinking"]=false};
  try {
   var result=await matcher.IdentifyAsync(job!["cues"]!.Deserialize<List<Cue>>(Data.Options)!,catalog,arm.Id,runtime.Endpoint,deadline.Token);record["result"]=result;
   if(job["reference_ids"] is JsonArray reference) {
    var actual=result.Array("matches").Select(m=>m.Int("episode_id")).Order();var expected=reference.Select(m=>m!.GetValue<int>()).Order();
    record["reference_agreement"]=actual.SequenceEqual(expected)&&(reference.Count>0||result.Text("assessment")=="non_episode");
   }
  }catch(OperationCanceledException) when(deadline.IsCancellationRequested){record["error"]="40-minute per-recording deadline exceeded; no assignment accepted.";record["timed_out"]=true;}
  catch(Exception ex){record["error"]=ex.Message;if(ex.Data["model_attempts"] is string attempts)record["model_attempts"]=JsonNode.Parse(attempts);}
  record["seconds"]=watch.Elapsed.TotalSeconds;record["usage"]=JsonSerializer.SerializeToNode(matcher.Usage,Data.Options);Save(id+".json",record);Save(id+"-runtime-tail.json",runtime.Tail);results.Add(record);
 }
 var summary=new{cases=results.Count,completed=results.Count(r=>r!["result"]!=null),errors=results.Count(r=>r.Text("error")!=""),timeouts=results.Count(r=>r.Bool("timed_out")),referenceAgreements=results.Count(r=>r.Bool("reference_agreement")),seconds=results.Sum(r=>r!["seconds"]!.GetValue<double>()),manualCorrections=false,caveat="Two selected troublesome recordings have no consistent reference labels. Completed model suggestions passed structural, evidence and generated-prose checks, not independent truth validation. Thinking-on baseline includes a failed and an interrupted run, not a completed accuracy comparison."};
 Save("summary.json",summary);Save("results.json",results);
 string E(string s)=>WebUtility.HtmlEncode(s);
 var html=new StringBuilder("<meta charset=utf-8><title>Scout Gemma thinking-off pilot</title><style>body{font:16px Segoe UI;margin:36px}td,th{padding:12px;text-align:left;border-bottom:1px solid #ccc}</style><h1>Gemma validation pilot</h1><p>Automated identification and independent verification using original saved dialogue. No manual corrections. A completed suggestion is not independently confirmed truth.</p><pre>"+E(JsonSerializer.Serialize(summary,Data.Options))+"</pre><table><tr><th>Recording</th><th>Outcome</th><th>Minutes</th><th>Saved reference</th></tr>");
 foreach(var r in results){var value=r.Text("error");if(value=="")value=string.Join(" + ",r!["result"].Array("matches").Select(m=>catalog.Single(e=>e.Id==m.Int("episode_id"))).Select(e=>e.Code+" · "+e.Title));if(value=="")value=r!["result"].Text("assessment");html.Append($"<tr><td>{E(r.Text("id"))}</td><td>{E(value)}</td><td>{r!["seconds"]!.GetValue<double>()/60:F1}</td><td>{(r["reference_agreement"]==null?"Unscored":r.Bool("reference_agreement")?"Agrees":"Disagrees")}</td></tr>");}
 html.Append("</table>");
 foreach(var r in results.Where(r=>r?["result"]!=null)) {
  html.Append($"<h2>{E(r.Text("id"))}</h2><p>{E(r!["result"].Text("summary"))}</p>");
  foreach(var check in r["result"].Array("candidate_checks"))html.Append($"<p><strong>{E(check.Text("episode_title"))}</strong>: {E(check.Text("event_comparison"))}</p>");
 }
 File.WriteAllText(Path.Combine(root,"results.html"),html.ToString());Status("complete","Thinking-off pilot complete. No Scout data or media changed.");
}catch(Exception ex){Status("failed",ex.Message);throw;}

sealed class TracedTransport(string caseId,Action<string,object> save):HttpMessageHandler {
 readonly HttpClient inner=new(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=Timeout.InfiniteTimeSpan};int sequence;
 protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) {
  var seq=++sequence;var key=$"requests/{caseId}-{seq:00}";var body=JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
  // Streaming is diagnostic only; messages, schema and sampling options remain unchanged.
  body["stream"]=true;body["stream_options"]=new JsonObject{["include_usage"]=true};save(key+"-request.json",body);
  using var wire=new HttpRequestMessage(HttpMethod.Post,request.RequestUri){Content=new StringContent(body.ToJsonString(),Encoding.UTF8,"application/json")};
  var clock=Stopwatch.StartNew();var content=new StringBuilder();var reasoning=new StringBuilder();JsonNode? usage=null;string? finish=null;var last=TimeSpan.Zero;var chunks=0;double? firstTokenSeconds=null;
  void Progress(string state)=>save(key+"-progress.json",new{state,seconds=clock.Elapsed.TotalSeconds,firstTokenSeconds,chunks,answerCharacters=content.Length,reasoningCharacters=reasoning.Length,finishReason=finish,usage,updated=DateTimeOffset.UtcNow});
  try {
   using var response=await inner.SendAsync(wire,HttpCompletionOption.ResponseHeadersRead,ct);
   if(!response.IsSuccessStatusCode){var error=await response.Content.ReadAsStringAsync(ct);save(key+"-http-error.json",new{status=(int)response.StatusCode,body=error});return new HttpResponseMessage(response.StatusCode){Content=new StringContent(error)};}
   using var reader=new StreamReader(await response.Content.ReadAsStreamAsync(ct));
   while(await reader.ReadLineAsync(ct) is string line) {
    if(!line.StartsWith("data: ")||line=="data: [DONE]")continue;
    var evt=JsonNode.Parse(line[6..])!;if(evt["usage"]!=null)usage=evt["usage"]!.DeepClone();
    foreach(var choice in evt.Array("choices")) {
     var delta=choice!["delta"];var text=delta.Text("content");var thought=delta.Text("reasoning_content");
     if(text.Length+thought.Length>0){firstTokenSeconds??=clock.Elapsed.TotalSeconds;chunks++;}content.Append(text);reasoning.Append(thought);
     if(choice["finish_reason"]!=null)finish=choice.Text("finish_reason");
    }
    if(clock.Elapsed-last>TimeSpan.FromSeconds(15)){Progress("streaming");last=clock.Elapsed;}
   }
   var result=new JsonObject{["choices"]=new JsonArray(new JsonObject{["finish_reason"]=finish,["message"]=new JsonObject{["content"]=content.ToString(),["reasoning_content"]=reasoning.ToString()}}),["usage"]=usage};
   save(key+"-response.json",result);Progress("complete");return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(result.ToJsonString())};
  }catch(Exception ex){save(key+"-interrupted.json",new{error=ex.Message,partialAnswer=content.ToString(),partialReasoning=reasoning.ToString()});Progress("interrupted");throw;}
 }
 protected override void Dispose(bool disposing){if(disposing)inner.Dispose();base.Dispose(disposing);}
}
