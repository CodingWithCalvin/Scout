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
Test("New Scout persists source and identity before returning",()=>{
 var store=new WorkspaceStore(Path.Combine(temp,"new-scout"));var source=Path.Combine(temp,"New Show");Directory.CreateDirectory(source);
 store.SaveIdentity(source,new("tvdb","123","Catalog Show"));var run=store.CreateRun(source);
 Assert(run.Name=="Catalog Show"&&run.SeriesIdentity?.Id=="123"&&store.ReadAll().Single().Id==run.Id);
});
Test("New Scout rejects unavailable source without creating history",()=>{
 var store=new WorkspaceStore(Path.Combine(temp,"absent-source-history"));
 try{store.CreateRun(Path.Combine(temp,"unavailable-source"));throw new Exception("Expected missing folder failure");}catch(DirectoryNotFoundException){}
 Assert(store.ReadAll().Count==0);
});
Test("New Scout storage failure preserves existing run",()=>{
 var source=Path.Combine(temp,"source-storage-test");Directory.CreateDirectory(source);
 var goodStore=new WorkspaceStore(Path.Combine(temp,"good-history"));var original=goodStore.CreateRun(source);
 var blocked=Path.Combine(temp,"blocked-history");File.WriteAllText(blocked,"not a directory");
 try{new WorkspaceStore(blocked).CreateRun(source);throw new Exception("Expected storage failure");}catch(IOException){}
 Assert(goodStore.ReadAll().Single().Id==original.Id&&File.ReadAllText(blocked)=="not a directory");
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
Test("Correct episode names and season styles are preserved by default",()=>{
 var row=Row("Season 2/Show-S02E01-Title 1.mkv");var plan=PreviewPlanner.Create(Report(row),temp,"Show");var item=plan.Items.Single();
 Assert(item.Action=="unchanged"&&item.Change=="Unchanged"&&item.Source==item.Destination);
 Assert(plan.Naming.PreserveCorrectNames);
});
Test("Punctuation equivalence is conservative",()=>{
 Assert(NamingOptions.Inspect("Victorious-S04E06-Tori.Fixes.Beck.and.Jade.mkv","Victorious",new Episode(1,4,6,"Tori Fixes Beck & Jade")).Correct);
 Assert(NamingOptions.Inspect("Show - 2x01 - Tori & Jade’s Playdate.mkv","Show",new Episode(1,2,1,"Tori & Jade's Playdate")).Correct);
 Assert(!NamingOptions.Inspect("Show-S02E01-Opposite Date.mkv","Show",new Episode(1,2,1,"Tori & Jade's Playdate")).Correct);
 Assert(!NamingOptions.Inspect("Show-S02E01E02-Title 1.mkv","Show",new Episode(1,2,1,"Title 1")).Correct);
});
Test("Standardizing a correct name is formatting only",()=>{
 var row=Row("Season 02/Show-S02E01-Title 1.mkv");var item=PreviewPlanner.Create(Report(row),temp,"Show",naming:new(){PreserveCorrectNames=false}).Items.Single();
 Assert(item.Action=="rename/move"&&item.Change=="Formatting only");
});
Test("Wrong episode number is an episode correction",()=>{
 var item=PreviewPlanner.Create(Report(Row("Season 02/Show-S02E09-Title 1.mkv")),temp,"Show").Items.Single();
 Assert(item.Change=="Episode correction"&&item.Destination!.EndsWith("Show - S02E01 - Title 1.mkv"));
});
Test("Folder typography is formatting only when standardizing",()=>{
 var item=PreviewPlanner.Create(Report(Row("Season 2/Show - S02E01 - Title 1.mkv")),temp,"Show",naming:new(){PreserveCorrectNames=false}).Items.Single();
 Assert(item.Change=="Formatting only");
});
Test("Preserved filename destinations still detect collisions",()=>{
 var row=Row("Season 01/Show-S02E01-Title 1.mkv");var destination=Path.Combine(temp,"Season 02","Show-S02E01-Title 1.mkv");
 Directory.CreateDirectory(Path.GetDirectoryName(destination)!);File.WriteAllText(destination,"existing copy");
 var item=PreviewPlanner.Create(Report(row),temp,"Show").Items.Single();Assert(item.Action=="hold"&&File.ReadAllText(destination)=="existing copy");File.Delete(destination);
});
Test("Wrong title is an episode correction",()=>{
 var item=PreviewPlanner.Create(Report(Row("Season 02/Show-S02E01-Wrong title.mkv")),temp,"Show").Items.Single();Assert(item.Change=="Episode correction");
});
Test("Correct filename in wrong folder moves without formatting",()=>{
 var item=PreviewPlanner.Create(Report(Row("Season 01/Show-S02E01-Title 1.mkv")),temp,"Show").Items.Single();
 Assert(item.Change=="Folder move"&&Path.GetFileName(item.Destination)=="Show-S02E01-Title 1.mkv"&&Path.GetFileName(Path.GetDirectoryName(item.Destination))=="Season 02");
});
Test("Custom naming is idempotent and recorded in the preview",()=>{
 var options=new NamingOptions{FileTemplate="{title} - {season}x{episode:00}",SeasonTemplate="S{season:00}"};
 var row=Row("S02/Title 1 - 2x01.mkv");var plan=PreviewPlanner.Create(Report(row),temp,"Show",naming:options);
 Assert(plan.Items.Single().Action=="unchanged"&&plan.Naming.FileTemplate==options.FileTemplate);
 options.FileTemplate="{code} - {title}";Assert(plan.Naming.FileTemplate!=options.FileTemplate);
});
Test("Naming templates reject traversal and missing identity",()=>{
 Throws(()=>new NamingOptions{SeasonTemplate="../Season {season}"}.Validate());
 Throws(()=>new NamingOptions{FileTemplate="{unknown} - {title}"}.Validate());
 Throws(()=>new NamingOptions{FileTemplate="{title}"}.Validate());
 Throws(()=>new NamingOptions{SeasonTemplate="All"}.Validate());
 new NamingOptions{FileTemplate="{series}.{season}x{episode:00}.{title}"}.Validate();
});
Test("Global naming settings roundtrip",()=>{
 var store=new SettingsStore(Path.Combine(temp,"naming-settings"));store.Save(new(){Naming=new(){PreserveCorrectNames=false,SeasonTemplate="S{season}"}});
 Assert(!store.Load().Naming.PreserveCorrectNames&&store.Load().Naming.SeasonTemplate=="S{season}");
});
Test("Equivalent duplicate primary stays in place",()=>{
 var a=Row("Season 2/Show-S02E01-Title 1.mkv");var b=Row("duplicate-copy.mkv");var plan=PreviewPlanner.Create(Report(a,b),temp,"Show");
 Assert(plan.Items.Single(x=>x.Category=="episode").Action=="unchanged");
 Assert(plan.Items.Single(x=>x.Category=="duplicate").Change=="Duplicate handling");
});
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
Test("Evidence review separates supported rationale and original timed cues",()=>{
 var row=JsonNode.Parse("""
 {"path":"episode.mkv","matching_method":"reasoning","reasoning":{"assessment":"supported","summary":"A play rehearsal.","candidate_checks":[{"episode_id":2,"episode_title":"Other episode","event_comparison":"No rehearsal."}],"alternatives":[{"episode_id":2,"reason_against":"The events differ."}]},"dialogue":{"cues":[{"start":1,"text":"First line."},{"start":3,"text":"Second line."}]},"candidates":[{"episode":{"id":1,"season":2,"number":3,"title":"The Play","overview":"Students rehearse."},"reason":"The rehearsal agrees.","evidence":[{"start":1,"text":"First line. Second line."}]}]}
 """)!.AsObject();
 var group=new RecordingGroup{Id="test",Rows=[row],Status="suggested",Episode=new(1,2,3,"The Play")};
 var review=EvidenceReview.Create(group);
 Assert(review.Assessment.Contains("supports")&&review.Suggestion.Contains("S02E03"));
 Assert(review.Sections.Any(s=>s.Heading=="Why it was suggested"&&s.Text=="The rehearsal agrees."));
 Assert(review.Sections.Any(s=>s.Text.Contains("[00:00:01] First line.\n[00:00:03] Second line.")));
 Assert(review.Sections.Any(s=>s.Heading.Contains("Rejected alternative")&&s.Text=="The events differ."));
 group.Status="unresolved_conflict";group.Episode=null;review=EvidenceReview.Create(group);
 Assert(review.Assessment.Contains("do not assign")&&review.Suggestion=="No episode assignment");
});
Test("Evidence review distinguishes no-AI suggestions errors and features",()=>{
 var row=new JsonObject{["path"]="episode.mkv"};var group=new RecordingGroup{Id="test",Rows=[row],Status="suggested",Episode=new(1,1,1,"Pilot")};
 Assert(EvidenceReview.Create(group).Assessment.Contains("content unverified"));
 group.Status="source_or_model_error";group.Episode=null;row["error"]="No readable subtitles";
 Assert(EvidenceReview.Create(group).Sections.Any(s=>s.Heading=="Processing error"&&s.Text=="No readable subtitles"));
 group.Status="unassigned";row.Remove("error");row["reasoning"]=new JsonObject{["assessment"]="non_episode"};
 Assert(EvidenceReview.Create(group).Assessment.Contains("Non-episode"));
 row["reasoning"]!["assessment"]="uncertain";Assert(EvidenceReview.Create(group).Assessment.StartsWith("Uncertain"));
});
Test("Setup and independent discovery/identification selections survive restart",()=>{
 var store=new WorkspaceStore(Path.Combine(temp,"setup-history"));
 var first=new RunWorkspace{Library=temp,Setup=new ScoutSetup{Mode="discover",MetadataProvider="tmdb",Query="Victorious",CandidateKey="tmdb:1"},DiscoveryFiles=["sample.mkv"],SelectedFiles=["episode1.mkv","episode2.mkv"]};store.Save(first);
 var second=new RunWorkspace{Library=temp,Setup=new ScoutSetup{Mode="known",MetadataProvider="tvdb",Query="152721"}};store.Save(second);
 var loaded=store.ReadAll().Single(r=>r.Id==first.Id);var setup=loaded.RestoreSetup();
 Assert(setup.Mode=="discover"&&setup.MetadataProvider=="tmdb"&&setup.Query=="Victorious"&&setup.CandidateKey=="tmdb:1");
 loaded.SelectedFiles=["different.mkv"];Assert(loaded.DiscoveryFiles.SequenceEqual(new[]{"sample.mkv"}));
 Assert(store.ReadAll().Single(r=>r.Id==second.Id).RestoreSetup().Mode=="known");
 var inherited=setup with {};inherited.Mode="known";Assert(setup.Mode=="discover");
});
Test("Old runs recover discovery mode from recorded discovery events",()=>{
 var legacy=new RunWorkspace{Events=["12:00  Show discovery inspected 1 selected files using tvdb."]};
 Assert(legacy.RestoreSetup().Mode=="discover");
 legacy.Setup!.Mode="known";Assert(legacy.RestoreSetup().Mode=="known");
});
Test("Local model list includes downloaded and selected GGUF files, excludes partial downloads",()=>{
 var folder=Path.Combine(temp,"models-list");Directory.CreateDirectory(Path.Combine(folder,"nested"));
 var model=Path.Combine(folder,"nested","model.gguf");File.WriteAllText(model,"fixture");File.WriteAllText(Path.Combine(folder,"download.gguf.part"),"partial");
 var external=Path.Combine(temp,"external.gguf");File.WriteAllText(external,"fixture");
 var models=LocalModels.Find(folder,external);Assert(models.Count==2&&models.Any(m=>m.Path==model)&&models.Any(m=>m.Path==external));
 Assert(LocalModels.Find(folder,model).Count==1);Assert(LocalModels.Find(Path.Combine(temp,"missing-models"),"").Count==0);
});
Test("Managed runtime display distinguishes missing installed and incomplete",()=>{
 var settings=new AppSettings{ModelDirectory=Path.Combine(temp,"runtime-status")};
 Assert(RuntimeDownloads.Installation(settings).InstalledPath=="");
 var profile=RuntimeDownloads.Profile(settings);var root=Path.Combine(settings.ModelDirectory,"runtimes");var installed=Path.Combine(root,profile.Id,"llama-server.exe");
 Directory.CreateDirectory(Path.GetDirectoryName(installed)!);File.WriteAllText(installed,"fixture");
 Assert(RuntimeDownloads.Installation(settings).Status.StartsWith("Incomplete"));
 File.WriteAllText(Path.Combine(root,profile.FileName),"fixture");var status=RuntimeDownloads.Installation(settings);
 Assert(status.Status.StartsWith("Installed")&&status.InstalledPath==installed);
 settings.GpuLayers=1;Assert(RuntimeDownloads.Installation(settings).InstalledPath=="");
});
Test("Unified server choice keeps loopback local and remote protected",()=>{
 Assert(ConnectionPolicy.ServerProvider("http://127.0.0.1:18765/v1/chat/completions")=="local");
 Assert(ConnectionPolicy.ServerProvider("https://server.example/v1/chat/completions")=="compatible");
 Throws(()=>ConnectionPolicy.ServerProvider("http://server.example/v1/chat/completions"));
 var legacy=System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"use_custom_runtime\":true,\"runtime_executable\":\"old.exe\"}",Data.Options)!;
 Assert(!System.Text.Json.JsonSerializer.Serialize(legacy,Data.Options).Contains("runtime_executable"));
});
RuntimeTests.Run(Test,temp);
SettingsTests.Run(Test,temp);
DiscoveryTests.Run(Test,temp);
if(args.Contains("--metadata-smoke"))DiscoveryTests.PublicSmoke(Test);
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
