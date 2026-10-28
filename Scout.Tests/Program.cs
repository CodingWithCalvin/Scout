using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Scout.Core;

var checks=0;var failures=new List<string>();
void Test(string name,Action action){try{action();checks++;Console.WriteLine("PASS "+name);}catch(Exception ex){failures.Add(name+": "+ex.Message);Console.WriteLine("FAIL "+name+": "+ex.Message);}}
void Assert(bool value,string message="Assertion failed"){if(!value)throw new Exception(message);}
void Throws(Action action){try{action();}catch(InvalidDataException){return;}catch(InvalidOperationException){return;}catch(ArgumentException){return;}throw new Exception("Expected rejection.");}
var temp=Path.Combine(Path.GetTempPath(),"scout-tests-"+Guid.NewGuid());Directory.CreateDirectory(temp);
JsonObject Row(string name,int? id=1,char hash='a') {
    var path=Path.Combine(temp,name);Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path,name);
    return new JsonObject {["path"]=path,["matching_method"]="reasoning",
        ["candidates"]=id is null?new JsonArray():new JsonArray(new JsonObject{["episode"]=JsonSerializer.SerializeToNode(new Episode(id.Value,2,id.Value,"Title "+id),Data.Options)}),
        ["primary_fingerprint"]=new JsonObject{["algorithm"]="primary-packets-sha256-v1",["video"]=new string(hash,64),["audio"]=new string('f',64),["size"]=new FileInfo(path).Length,["mtime_ns"]=PreviewPlanner.MtimeNs(path),["sampled_digest"]=PreviewPlanner.SampledDigest(path)}};
}
JsonObject Report(params JsonObject[] rows)=>new(){["files"]=new JsonArray(rows.Select(r=>(JsonNode)r).ToArray())};
try {
Test("Workspace isolation and history roundtrip",()=>{
 var store=new WorkspaceStore(Path.Combine(temp,"workspaces"));var first=new RunWorkspace{Name="First",Status="Finished",Report="saved-report.json",Preview="preview.html"};
 first.Events.Add("Reviewed");store.Save(first);var second=new RunWorkspace{Name="Second",Status="Running"};store.Save(second);
 Assert(store.DirectoryFor(first)!=store.DirectoryFor(second));
 var history=store.ReadAll();Assert(history.Count==2&&history.Single(x=>x.Id==first.Id).Events.Single()=="Reviewed");
 Assert(history.Single(x=>x.Id==second.Id).Status=="Running");
 Assert(store.ReadAll(true).Single(x=>x.Id==second.Id).Status=="Interrupted");
 Assert(store.ReadAll().Single(x=>x.Id==first.Id).Report=="saved-report.json");
 Throws(()=>store.Save(new RunWorkspace{Id="../escape"}));
});
Test("Run guides are private snapshots",()=>{
 var store=new WorkspaceStore(Path.Combine(temp,"guide-runs"));
 var source=Path.Combine(temp,"guide.json");
 Data.Save(source,new {episodes=new[]{new Episode(1,1,1,"Original")}});
 var first=new RunWorkspace();var a=store.SaveCatalog(first,source);
 var second=new RunWorkspace();var b=store.SaveCatalog(second,a);
 Data.Save(source,new {episodes=new[]{new Episode(1,1,1,"Replacement")}});
 store.SaveCatalog(second,source);
 Assert(Data.Catalog(a).Single().Title=="Original");
 Assert(Data.Catalog(b).Single().Title=="Replacement");
 Assert(a!=b&&store.ReadAll().Single(x=>x.Id==second.Id).Catalog==b);
});
Test("Delete and undo preserve run artifacts and other runs",()=>{
 var store=new WorkspaceStore(Path.Combine(temp,"delete-runs"));var first=new RunWorkspace();var second=new RunWorkspace();
 store.Save(first);store.Save(second);var artifact=Path.Combine(store.DirectoryFor(first),"report.json");File.WriteAllText(artifact,"saved");
 store.DeleteRun(first);Assert(store.ReadAll().Count==1&&store.ReadAll().Single().Id==second.Id);
 Assert(File.ReadAllText(artifact)=="saved");store.RestoreRun(first);Assert(store.ReadAll().Count==2);
 second.Status="Running";Throws(()=>store.DeleteRun(second));
});
Test("Exact duplicates grouped",()=>{
 var a=Row("a.mkv");var b=Row("b.mkv");var report=Report(a,b);var before=report.ToJsonString();var g=CollectionResolver.Resolve(report);
 Assert(g.Count==1&&g[0].Copies==2&&g[0].Episode?.Id==1);Assert(before==report.ToJsonString(),"Resolver mutated model results.");
});
Test("Different recordings conflict",()=>{var g=CollectionResolver.Resolve(Report(Row("c.mkv"),Row("d.mkv",hash:'b')));Assert(g.All(x=>x.Status=="unresolved_conflict"&&x.Episode==null));});
Test("Disagreeing duplicates held",()=>{var g=CollectionResolver.Resolve(Report(Row("e.mkv"),Row("f.mkv",2)));Assert(g.Single().Episode==null);});
Test("Multiple suggestions invalid",()=>{var r=Row("g.mkv");r.Array("candidates").Add(new JsonObject{["episode"]=JsonSerializer.SerializeToNode(new Episode(2,2,2,"Two"),Data.Options)});Assert(CollectionResolver.Resolve(Report(r))[0].Status=="invalid_model_output");});
Test("Duplicate paths rejected",()=>{var r=Row("h.mkv");Throws(()=>CollectionResolver.Resolve(Report(r,r.DeepClone().AsObject())));});
Test("No fingerprint cannot prove duplicates",()=>{var a=Row("i.mkv");var b=Row("j.mkv");a.Remove("primary_fingerprint");b.Remove("primary_fingerprint");Assert(CollectionResolver.Resolve(Report(a,b)).Count==2);});
Test("Filename sanitization",()=>Assert(PreviewPlanner.SafeName("A: B?")=="A B"&&PreviewPlanner.SafeName("CON.txt")=="_CON.txt"));
Test("Preview retains all duplicate copies",()=>{
 var a=Row("p1.mkv");var b=Row("p2.mkv");
 b.Array("candidates")[0]!["filename_evidence"]=new JsonObject{["number_match"]=true,["title_match"]=true};
 var plan=PreviewPlanner.Create(Report(a,b),temp,"Show");
 Assert(plan.Items.Count==2&&plan.Summary["duplicate"]==1&&plan.Items.Single(i=>i.Category=="episode").Source==b.Text("path"));Assert(!Directory.Exists(Path.Combine(temp,"_Duplicates")));
});
Test("Feature has no invented episode number",()=>{var r=Row("feature.mkv",null);r["reasoning"]=new JsonObject{["assessment"]="non_episode"};var i=PreviewPlanner.Create(Report(r),temp,"Show").Items.Single();Assert(i.Category=="feature"&&i.Destination!.Contains("Extras")&&!i.Destination.Contains("S00"));});
Test("Changed source held",()=>{var r=Row("changed.mkv");File.AppendAllText(r.Text("path"),"extra");Assert(PreviewPlanner.Create(Report(r),temp,"Show").Items.Single().Action=="hold");});
Test("Missing source held",()=>{var r=Row("missing.mkv");File.Delete(r.Text("path"));Assert(PreviewPlanner.Create(Report(r),temp,"Show").Items.Single().Action=="hold");});
Test("Occupied destination held",()=>{
 var r=Row("occupied.mkv");var target=Path.Combine(temp,"Season 02","Show - S02E01 - Title 1.mkv");Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.WriteAllText(target,"existing");
 Assert(PreviewPlanner.Create(Report(r),temp,"Show").Items.Single().Action=="hold");Assert(File.ReadAllText(target)=="existing");File.Delete(target);
});
Test("No preview files inside library",()=>{var p=PreviewPlanner.Create(Report(Row("inside.mkv")),temp,"Show");Throws(()=>PreviewPlanner.Write(p,Path.Combine(temp,"preview")));Assert(!Directory.Exists(Path.Combine(temp,"preview")));});
Test("Subtitle parsing",()=>{var cues=MediaPipeline.ParseSrt("1\n00:01:02,500 --> 00:01:04,000\n<i>Hello &amp; goodbye</i>\n");Assert(cues.Count==1&&cues[0].Start==62.5&&cues[0].Text=="Hello & goodbye");});
Test("Full dialogue block coverage",()=>{
 var cues=Enumerable.Range(0,40).Select(i=>new Cue(i*5,"cue"+i)).ToList();var b=DialogueMatcher.Blocks(cues);
 Assert(b.SelectMany(x=>Enumerable.Range(x.CueIndex,x.EndCueIndex-x.CueIndex+1)).SequenceEqual(Enumerable.Range(0,40)));
});
Test("Remote endpoint refused",()=>Throws(()=>DialogueMatcher.LocalEndpoint("https://example.com/api")));
var episodes=new List<Episode>{new(1,2,1,"The Play","Two students rehearse a play."),new(2,2,2,"The Trip","Friends take a trip.")};
var blocks=new List<Block>{new(0,0,1,0,"We rehearse a play."),new(1,2,3,10,"We perform the play.")};
JsonObject Response(bool verify=false)=>new() {
 ["assessment"]="supported",["summary"]="Students rehearse and perform.",
 ["candidate_checks"]=new JsonArray(new JsonObject {["episode_title"]="The Play",["supporting_blocks"]=new JsonArray(0,1),["contradicting_blocks"]=new JsonArray(),["event_comparison"]="The play is rehearsed.",["guide_event"]=verify?episodes[0].Overview:null}),
 ["matches"]=new JsonArray(new JsonObject{["episode_title"]="The Play",["reason"]="Rehearsal and performance."}),["alternatives"]=new JsonArray()
};
Test("Evidence resolves to original source",()=>{var r=Response();DialogueMatcher.Validate(r,blocks,episodes,false);Assert(r.Array("matches")[0]!.Array("evidence")[1].Text("source_text")==blocks[1].SourceText);});
Test("Wrong guide card rejected",()=>{var r=Response(true);r.Array("candidate_checks")[0]!["guide_event"]="Wrong";Throws(()=>DialogueMatcher.Validate(r,blocks,episodes,true));});
Test("Unresolved contradiction rejected",()=>{var r=Response();r.Array("candidate_checks")[0]!["contradicting_blocks"]=new JsonArray(1);Throws(()=>DialogueMatcher.Validate(r,blocks,episodes,false));});
Test("Single evidence block rejected",()=>{var r=Response();r.Array("candidate_checks")[0]!["supporting_blocks"]=new JsonArray(0);Throws(()=>DialogueMatcher.Validate(r,blocks,episodes,false));});
Test("Two-pass HTTP contract",()=>{
 var handler=new FakeHandler();using var http=new HttpClient(handler);
 var matcher=new DialogueMatcher(http);var cues=Enumerable.Range(0,20).Select(i=>new Cue(i*5,"We rehearse the play "+i)).ToList();
 handler.Results=[Response(),Response(true)];
 var result=matcher.IdentifyAsync(cues,episodes,"model","http://127.0.0.1:1234/v1/chat/completions").GetAwaiter().GetResult();
 Assert(handler.Requests.Count==2);var second=handler.Requests[1]["messages"]![1].Text("content");
 Assert(!second.Contains("Students rehearse and perform."),"Verifier received generated summary.");Assert(result.Text("method")==DialogueMatcher.Version);
});
SettingsTests.Run(Test,temp);
if(args.Length>=3) {
 var reportFile=Path.GetFullPath(args[0]);var root=args[1];var output=args[2];
 Test("57-file imported report parity and media unchanged",()=>{
    var report=Data.Read(reportFile);var before=report.ToJsonString();var groups=CollectionResolver.Resolve(report);
    Assert(groups.Count==55&&groups.Count(g=>g.Episode!=null)==54&&groups.Sum(g=>g.Copies-1)==2&&groups.Count(g=>g.Feature)==1);
    var paths=report.Array("files").Select(n=>n.Text("path")).ToList();
    var snapshot=paths.ToDictionary(p=>p,p=>(new FileInfo(p).Length,PreviewPlanner.MtimeNs(p)));
    var catalog=Data.Catalog(Path.Combine(Path.GetDirectoryName(reportFile)!,"catalog.json"));
    var plan=PreviewPlanner.Create(report,root,"Victorious",catalog);
    Assert(plan.Items.Count==57&&plan.Actions.GetValueOrDefault("hold")==0,"Unexpected hold: "+string.Join("; ",plan.Items.SelectMany(i=>i.BlockedReasons)));
    Assert(plan.NotIdentified.Any(e=>e.Season==2&&e.Number==1));PreviewPlanner.Write(plan,output);
    Assert(before==report.ToJsonString());Assert(snapshot.All(p=>p.Value==(new FileInfo(p.Key).Length,PreviewPlanner.MtimeNs(p.Key))));
    Assert(!Directory.Exists(Path.Combine(root,"_Duplicates"))&&!Directory.Exists(Path.Combine(root,"Extras")),"Unexpected destination folder.");
 });
}
}finally{Directory.Delete(temp,true);}
Console.WriteLine($"Completed: {checks} passed, {failures.Count} failed.");foreach(var f in failures)Console.WriteLine(f);
return failures.Count==0?0:1;

sealed class FakeHandler : HttpMessageHandler {
 public List<JsonObject> Results=[];public List<JsonObject> Requests=[];
 protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) {
  Requests.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject());
  var result=Results[Requests.Count-1];
  return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(new JsonObject{["choices"]=new JsonArray(new JsonObject{["finish_reason"]="stop",["message"]=new JsonObject{["content"]=result.ToJsonString()}})}.ToJsonString())};
 }
}
