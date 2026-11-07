using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Scout.Core;
static class MultipartTests {
 public static void Run(Action<string,Action> test,string temp) {
  var one=new Episode(101,1,1,"Opening","The team discovers a locked vault and unlocks it.");
  var two=new Episode(102,1,2,"Aftermath","The team returns the stolen map to the museum.");
  void Assert(bool value){if(!value)throw new Exception("Multipart assertion failed: "+new System.Diagnostics.StackTrace(true));}
  void Reject(Action action){try{action();}catch(InvalidDataException){return;}throw new Exception("Expected invalid multipart evidence.");}
  List<Cue> Cues(string tag,int count=4)=>Enumerable.Range(0,count).Select(i=>new Cue(i*40,$"{tag} distinctive scene {i}: original dialogue for the catalog story.")).ToList();
  JsonObject Result(List<Cue> cues,string coverage,params Episode[] episodes) {
   var result=new JsonObject{["assessment"]="supported",["coverage"]=coverage,["summary"]="Distinctive catalog events.",["matches"]=new JsonArray(),["candidate_checks"]=new JsonArray(),["alternatives"]=new JsonArray()};
   for(int i=0;i<episodes.Length;i++) {
    result.Array("matches").Add(new JsonObject{["episode_title"]=episodes[i].Title,["reason"]="Distinctive events match."});
    result.Array("candidate_checks").Add(new JsonObject{["episode_title"]=episodes[i].Title,["supporting_blocks"]=new JsonArray(i*2,i*2+1),["contradicting_blocks"]=new JsonArray(),["event_comparison"]="Original events agree.",["guide_event"]=episodes[i].Overview});
   }
   return result;
  }
  JsonObject Row(string name,char hash,string tag,string coverage,params Episode[] episodes) {
   var cues=Cues(tag);var result=Result(cues,coverage,episodes);DialogueMatcher.Validate(result,DialogueMatcher.Blocks(cues),episodes,true);result["method"]=DialogueMatcher.Version;result["dialogue_hash"]=CollectionResolver.Hash(JsonSerializer.Serialize(cues,Data.Options));
   var path=Path.Combine(temp,name);Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path,name);
   return new JsonObject{["path"]=path,["matching_method"]="reasoning",["reasoning"]=result,["dialogue"]=new JsonObject{["cues"]=JsonSerializer.SerializeToNode(cues,Data.Options)},
    ["candidates"]=new JsonArray(result.Array("matches").Select(m=>(JsonNode)new JsonObject{["episode"]=JsonSerializer.SerializeToNode(episodes.Single(e=>e.Id==m.Int("episode_id")),Data.Options),["reason"]=m!["reason"]!.DeepClone(),["evidence"]=new JsonArray(m.Array("evidence").Select(b=>(JsonNode)new JsonObject{["start"]=b!["start"]!.DeepClone(),["text"]=b["source_text"]!.DeepClone()}).ToArray())}).ToArray()),
    ["primary_fingerprint"]=new JsonObject{["algorithm"]="primary-packets-sha256-v1",["video"]=new string(hash,64),["audio"]=new string('f',64),["size"]=new FileInfo(path).Length,["mtime_ns"]=PreviewPlanner.MtimeNs(path),["sampled_digest"]=PreviewPlanner.SampledDigest(path)}};
  }
  JsonObject Answer(int first=0)=>new(){["assessment"]="split",["first_recording"]=first,["reason"]="Complementary incomplete recordings.",["continuity"]="Vault discovery in A continues into unlocking in B.",["guide_event"]=one.Overview,["evidence_a"]=new JsonArray(0,1),["evidence_b"]=new JsonArray(2,3)};
  JsonObject Link(JsonObject a,JsonObject b,int first=0)=>new(){["method"]="split-dialogue-v1",["episode_id"]=one.Id,["fingerprint_a"]=CollectionResolver.FingerprintKey(a["primary_fingerprint"]),["fingerprint_b"]=CollectionResolver.FingerprintKey(b["primary_fingerprint"]),["dialogue_a"]=MultipartEvidence.DialogueHash(a),["dialogue_b"]=MultipartEvidence.DialogueHash(b),["initial"]=Answer(first),["verification"]=Answer(first)};
  JsonObject Report(params JsonObject[] rows)=>new(){["files"]=new JsonArray(rows.Select(r=>(JsonNode)r).ToArray())};
  test("New files checkpoint their scope and preserve older successes and failures",()=>{
   var a=Row("incremental/a.mkv",'a',"old","complete",one);
   var b=Row("incremental/b.mkv",'b',"failed","complete",one);b["error"]="old failure";b["candidates"]=new JsonArray();
   var c=Row("incremental/c.mkv",'c',"c","complete",two);
   var d=Row("incremental/d.mkv",'d',"d","complete",two);
   var root=Path.GetDirectoryName(a.Text("path"))!;var output=Path.Combine(temp,"incremental-output");
   var catalog=Path.Combine(output,"catalog.json");var reportPath=Path.Combine(output,"report.json");
   Data.Save(catalog,new{episodes=new[]{one,two}});Data.Save(reportPath,Report(a,b));
   var uncertain=Result(Cues("d"),"unknown");uncertain["assessment"]="uncertain";
   uncertain.Array("candidate_checks").Add(new JsonObject{["episode_title"]=one.Title,["supporting_blocks"]=new JsonArray(),["contradicting_blocks"]=new JsonArray(0,1),["event_comparison"]="Dialogue does not establish the defining events.",["guide_event"]=one.Overview});
   var handler=new Handler([Result(Cues("c"),"complete",two),Result(Cues("c"),"complete",two),uncertain,uncertain.DeepClone().AsObject()]);
   using var client=new HttpClient(handler);var toolPaths=new List<string>();
   Task<string> Execute(string exe,IEnumerable<string> args,CancellationToken ct) {
    var values=args.ToArray();var path=values.First(v=>v.EndsWith(".mkv"));toolPaths.Add(path);
    Assert(PreviewPlanner.Same(path,c.Text("path"))||PreviewPlanner.Same(path,d.Text("path")));
    if(values.Contains("-show_streams"))return Task.FromResult("""{"format":{"duration":"160"},"streams":[{"index":2,"codec_type":"subtitle","codec_name":"subrip","tags":{"language":"eng"}}]}""");
    if(values.Contains("streamhash"))return Task.FromResult("0,v,SHA256="+new string(Path.GetFileName(path)[0],64)+"\n1,a,SHA256="+new string('f',64));
    var tag=Path.GetFileNameWithoutExtension(path);
    return Task.FromResult(string.Join("\n\n",Cues(tag).Select((cue,i)=>$"{i+1}\n{TimeSpan.FromSeconds(cue.Start):hh\\:mm\\:ss},000 --> {TimeSpan.FromSeconds(cue.Start+2):hh\\:mm\\:ss},000\n{cue.Text}")));
   }
   var pipeline=new MediaPipeline(new DialogueMatcher(client),Execute);
   var settings=new MatchSettings(root,catalog,output,"model","http://127.0.0.1:1234/v1/chat/completions",SelectedFiles:[a.Text("path")],NewFilesOnly:true);
   var pause=new ScoutPause();
   try{pipeline.RunAsync(settings,new InlineProgress(m=>{if(m.StartsWith("[1/2]"))pause.Request();}),default,pause).GetAwaiter().GetResult();throw new Exception("Expected pause");}catch(ScoutPausedException){}
   var checkpoint=Data.Read(reportPath);Assert(handler.Requests.Count==2&&checkpoint["incremental_review"].Bool("active")&&checkpoint.Array("files").Count==3);
   Assert(JsonNode.DeepEquals(checkpoint.Array("files")[0],a)&&JsonNode.DeepEquals(checkpoint.Array("files")[1],b));
   pipeline.RunAsync(settings with {NewFilesOnly=false,Restart=true},new InlineProgress(_=>{}),default).GetAwaiter().GetResult();
   var final=Data.Read(reportPath);Assert(handler.Requests.Count==4&&toolPaths.Count==6&&!final["incremental_review"].Bool("active")&&final.Array("files").Count==4);
   Assert(JsonNode.DeepEquals(final.Array("files")[0],a)&&JsonNode.DeepEquals(final.Array("files")[1],b)&&final.Array("files").Skip(2).All(r=>r.Text("error")==""));
   pipeline.RunAsync(settings,new InlineProgress(_=>{}),default).GetAwaiter().GetResult();
   Assert(handler.Requests.Count==4&&toolPaths.Count==6&&JsonNode.DeepEquals(final,Data.Read(reportPath)));
  });
  test("Template completion understands tokens functions selectors and quoted literals",()=>{
   NamingCompletion? Complete(string text)=>NamingEditorLanguage.Complete(text,text.Length);
   Assert(Complete("{ser")!.Items.Single().Name=="series");
   Assert(Complete("{title.rep")!.Items.Select(i=>i.Name).ToHashSet().SetEquals(["replace","replaceWhitespace","replacePunctuation"]));
   Assert(Complete("{titles.")!.Items.Any(i=>i.Name=="join"));
   Assert(!Complete("{title.")!.Items.Any(i=>i.Name=="join"));
   Assert(Complete("{code.")==null&&Complete("{season:00.")==null);
   Assert(Complete("{title.remove(pu")!.Items.Single().Name=="punctuation");
   Assert(Complete("""{title.replace("a.b|c""")==null);
   Assert(Complete("Season 01.")==null&&Complete("{{title.")==null);
   Assert(Complete("""{title.replace("a.b|c", "x").rem""")!.Items.Any(i=>i.Name=="remove"));
   var mid=NamingEditorLanguage.Complete("{title.uppercase()}",10)!;
   Assert(mid.Start==7&&mid.End==16&&mid.Items.Single().Name=="uppercase");
   foreach(var item in NamingEditorLanguage.Functions) {
    NamingDsl.Validate("Season {season:00}\\{code} {titles."+item.Insert+"}");
    if(item.SelectionStart>=0)Assert(item.SelectionStart+item.SelectionLength<=item.Insert.Length);
   }
   foreach(var item in NamingEditorLanguage.Selectors)NamingDsl.Validate("Season {season:00}\\{code} {title.remove("+item.Insert+")}");
   var spans=NamingEditorLanguage.Highlight("""Season {season:00}\{title.replace("a.b|c", "x").remove(punctuation)}""");
   Assert(spans.Count(s=>s.Kind=="token")==2&&spans.Count(s=>s.Kind=="function")==2&&spans.Count(s=>s.Kind=="string")==2&&spans.Count(s=>s.Kind=="selector")==1);
   Assert(NamingEditorLanguage.Highlight("{{title}}").Count==0);
  });
  test("Dot chains preserve literal dots pipes Unicode and existing saved templates",()=>{
   var e=new Episode(1,1,1,"Caf\u00e9 & a.b|c!");
   var old=new NamingOptions{PathTemplate="""Season {season:00}\{code} {title|replace("\u0026", "and")|replace("a.b|c", "story")|remove(punctuation)|uppercase}"""};
   var display=old.EditablePathTemplate();
   Assert(display=="""Season {season:00}\{code} {title.replace("&", "and").replace("a.b|c", "story").remove(punctuation).uppercase()}""");
   var current=new NamingOptions{PathTemplate=display};
   Assert(current.FileName("Show",e)==old.FileName("Show",e));
   Assert(current.EditablePathTemplate()==display);
   var legacy=new NamingOptions{FileTemplate="{series|clean}.{code}.{title|clean|dots}"};
   var converted=legacy.EditablePathTemplate();
   Assert(converted.Contains(".remove(quotes)")&&!converted.Contains(@"\u")&&!converted.Contains('|'));
   Assert(new NamingOptions{PathTemplate=converted}.FileName("Show",e)==legacy.FileName("Show",e));
   Assert(NamingDsl.Quote("Caf\u00e9 & '")=="\"Caf\u00e9 & '\"");
  });
  test("Unified naming transforms only selected tokens with explicit removal selectors",()=>{
   var episode=new Episode(1,1,1,"Café, Go!");
   var naming=new NamingOptions{PathTemplate="""Season {season:00}\{series|replaceWhitespace(".")}.{code}.{titles|remove(diacritics)|remove(punctuation)|replaceWhitespace(".")|join(".and.")}{partSuffix|remove(punctuation)|replaceWhitespace(".")|prefix(".")}"""};
   naming.Validate();Assert(naming.Folder("My Show",episode)=="Season 01");
   Assert(naming.FileName("My Show",episode)=="My.Show.S01E01.Cafe.Go");
   Assert(naming.AssignmentName("My Show",[episode],1,2)=="My.Show.S01E01.Cafe.Go.Part.1.of.2");
   Assert(naming.AssignmentName("My Show",[episode,new Episode(2,1,2,"Next!")])=="My.Show.S01E01-E02.Cafe.Go.and.Next");
   var literal=new NamingOptions{PathTemplate="""Season {season:00}\{code} {title|remove("punctuation")}"""};
   Assert(literal.FileName("Show",episode with {Title="punctuation!"})=="S01E01 !");
   literal.PathTemplate="""Season {season:00}\{code} {title|remove(punctuation)}""";
   Assert(literal.FileName("Show",episode with {Title="punctuation!"})=="S01E01 punctuation");
   literal.PathTemplate="""Season {season:00}\{code} {title|replace("a|b,c", "ok")}""";
   Assert(literal.FileName("Show",episode with {Title="a|b,c"})=="S01E01 ok");
   foreach(var invalid in new[]{"""Season {season:00}\{code} {title|remove(unknown)}""","""Season {season:00}\{code|remove(punctuation)} {title}""","""..\Season {season:00}\{code} {title}""","""C:\Season {season:00}\{code} {title}""","""Season {season:00}\{code} {title|replace("x")}"""}) {
    try{NamingDsl.Validate(invalid);throw new Exception("Should reject "+invalid);}catch(ArgumentException){}
   }
   Assert(naming.EquivalentAssignmentName("My.Show-S01E01-E02-Cafe.Go.and.Next.mkv","My Show",[episode with {Title="Cafe Go"},new Episode(2,1,2,"Next")]));
  });
  test("Existing naming preferences convert to the unified template without global folder changes",()=>{
   var e=new Episode(1,1,1,"Tori & Jade\u2019s \"Playdate\"!");
   foreach(var separator in new[]{" ",".","_","-"}) {
    var old=new NamingOptions{FileWordSeparator=separator,FolderWordSeparator=" ",FileTemplate="{series|clean} - {code} - {title|clean}"};
    var converted=new NamingOptions{PathTemplate=old.EditablePathTemplate()};
    converted.Validate();
    Assert(converted.FileName("My Show",e)==old.FileName("My Show",e));
    Assert(converted.Folder("My Show",e)==old.Folder("My Show",e));
    Assert(converted.AssignmentName("My Show",[e],1,2)==old.AssignmentName("My Show",[e],1,2));
   }
  });
  test("Renamed recordings retain evidence and resume without model requests",()=>{
   var row=Row("relocate/old.mkv",'a',"saved","complete",one);var original=row.Text("path");var root=Path.GetDirectoryName(original)!;
   row["primary_fingerprint"]!["algorithm"]="full-file-sha256-v1";row["primary_fingerprint"]!["sha256"]=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(original)));
   var evidence=row["reasoning"]!.DeepClone();var moved=Path.Combine(root,"renamed.mkv");File.Move(original,moved);
   var output=Path.Combine(temp,"relocate-output");var catalog=Path.Combine(output,"catalog.json");Data.Save(catalog,new{episodes=new[]{one}});Data.Save(Path.Combine(output,"report.json"),Report(row));
   var handler=new Handler([]);using var client=new HttpClient(handler);var settings=new MatchSettings(root,catalog,output,"model","http://127.0.0.1:1234/v1/chat/completions",Ffmpeg:"must-not-run",Ffprobe:"must-not-run",SelectedFiles:[original],Restart:true);
   var pipeline=new MediaPipeline(new DialogueMatcher(client));pipeline.RunAsync(settings,new InlineProgress(_=>{}),default).GetAwaiter().GetResult();
   var saved=Data.Read(Path.Combine(output,"report.json")).Array("files").Single()!.AsObject();
   Assert(saved.Text("path")==moved&&saved.Array("path_history").Count==1&&JsonNode.DeepEquals(saved["reasoning"],evidence)&&RunRecovery.CanReuse(saved,[one])&&handler.Requests.Count==0);
   pipeline.RunAsync(settings,new InlineProgress(_=>{}),default).GetAwaiter().GetResult();Assert(handler.Requests.Count==0&&Data.Read(Path.Combine(output,"report.json")).Array("files").Single()!.Array("path_history").Count==1);
  });
  test("Relocation rejects ambiguous copies and requires full fingerprint confirmation",()=>{
   var row=Row("relocate-ambiguous/old.mkv",'a',"saved","complete",one);var original=row.Text("path");var root=Path.GetDirectoryName(original)!;
   var a=Path.Combine(root,"a.mkv");var b=Path.Combine(root,"b.mkv");File.Copy(original,a);File.Move(original,b);var report=Report(row);var key=CollectionResolver.FingerprintKey(row["primary_fingerprint"]);
   var changes=RecordingRelocation.ReconnectAsync(report,root,"unused",new InlineProgress(_=>{}),default,(_,_)=>Task.FromResult(key)).GetAwaiter().GetResult();
   Assert(changes.Count==0&&row.Text("path")==original&&row.Text("relocation_error").Contains("ambiguous")&&CollectionResolver.Resolve(report).Single().Episode==null);
   File.Delete(b);changes=RecordingRelocation.ReconnectAsync(report,root,"unused",new InlineProgress(_=>{}),default,(_,_)=>Task.FromResult<string?>("different-full-hash")).GetAwaiter().GetResult();Assert(changes.Count==0);
   changes=RecordingRelocation.ReconnectAsync(report,root,"unused",new InlineProgress(_=>{}),default,(_,_)=>Task.FromResult(key)).GetAwaiter().GetResult();
   Assert(changes.Count==1&&row.Text("path")==a&&row.Text("relocation_error")==""&&RunRecovery.CanReuse(row,[one]));
  });
  test("Issue selection includes competing recordings and disagreeing copies but excludes healthy duplicates",()=>{
   var a=Row("issues-select/a.mkv",'a',"a","complete",one);var b=Row("issues-select/b.mkv",'b',"b","complete",one);
   var c=Row("issues-select/c.mkv",'c',"c","complete",two);var copy=Row("issues-select/c-copy.mkv",'c',"c","complete",two);
   var report=Report(a,b,c,copy);var selected=IssueReview.Select(report);
   Assert(selected.SetEquals(new[]{Path.GetFullPath(a.Text("path")),Path.GetFullPath(b.Text("path"))}));
   b["primary_fingerprint"]=a["primary_fingerprint"]!.DeepClone();b["candidates"]![0]!["episode"]=JsonSerializer.SerializeToNode(two,Data.Options);
   Assert(IssueReview.Select(report,false).Count==4);
   File.AppendAllText(c.Text("path"),"changed");Assert(IssueReview.Select(Report(c.DeepClone().AsObject())).Contains(Path.GetFullPath(c.Text("path"))));
  });
  test("Issue review preserves unrelated results and resumes its saved scope without repeating successes",()=>{
   var a=Row("issues-run/a.mkv",'a',"first","complete",one);var b=Row("issues-run/b.mkv",'b',"second","complete",two);
   var c=Row("issues-run/c.mkv",'c',"third","complete",one);
   foreach(var row in new[]{a,b,c}) {row["full_dialogue"]=true;row["size"]=row["primary_fingerprint"]!["size"]!.DeepClone();row["sampled_digest"]=row["primary_fingerprint"]!["sampled_digest"]!.DeepClone();}
   a["error"]="failed request";c["error"]="failed request";a["candidates"]=new JsonArray();c["candidates"]=new JsonArray();
   var root=Path.GetDirectoryName(a.Text("path"))!;var output=Path.Combine(temp,"issues-run-output");var catalog=Path.Combine(output,"catalog.json");var reportPath=Path.Combine(output,"report.json");
   Data.Save(catalog,new{episodes=new[]{one,two}});Data.Save(reportPath,Report(a,b,c));
   var uncertain=Result(Cues("third"),"unknown");uncertain["assessment"]="uncertain";
   var handler=new Handler([Result(Cues("first"),"complete",one),Result(Cues("first"),"complete",one),uncertain,uncertain.DeepClone().AsObject()]);using var client=new HttpClient(handler);var pipeline=new MediaPipeline(new DialogueMatcher(client));
   var settings=new MatchSettings(root,catalog,output,"model","http://127.0.0.1:1234/v1/chat/completions",Ffmpeg:"must-not-run-ffmpeg",Ffprobe:"must-not-run-ffprobe",SelectedFiles:[a.Text("path"),b.Text("path"),c.Text("path")],ReviewIssues:true);
   var pause=new ScoutPause();
   try {pipeline.RunAsync(settings,new InlineProgress(m=>{if(m.StartsWith("[1/2]"))pause.Request();}),default,pause).GetAwaiter().GetResult();throw new Exception("Expected pause");}catch(ScoutPausedException){}
   var checkpoint=Data.Read(reportPath);Assert(handler.Requests.Count==2&&checkpoint["issue_review"].Bool("active")&&checkpoint["issue_review"].Array("paths").Count==2);
   Assert(JsonNode.DeepEquals(checkpoint.Array("files").Single(r=>r.Text("path")==b.Text("path")),b));
   pipeline.RunAsync(settings with {ReviewIssues=false,Restart=true},new InlineProgress(_=>{}),default).GetAwaiter().GetResult();
   var final=Data.Read(reportPath);Assert(handler.Requests.Count==4&&!final["issue_review"].Bool("active")&&final.Array("files").Count==3);
   Assert(JsonNode.DeepEquals(final.Array("files").Single(r=>r.Text("path")==b.Text("path")),b));
   Assert(final.Array("files").All(r=>r.Text("error")=="")&&IssueReview.Select(final).SetEquals(new[]{Path.GetFullPath(c.Text("path"))}));
   Assert(File.ReadAllText(a.Text("path"))=="issues-run/a.mkv");
  });
  test("Issue review with no issues preserves the report and performs no model calls",()=>{
   var a=Row("issues-none/a.mkv",'a',"first","complete",one);var root=Path.GetDirectoryName(a.Text("path"))!;
   var output=Path.Combine(temp,"issues-none-output");var catalog=Path.Combine(output,"catalog.json");var reportPath=Path.Combine(output,"report.json");
   var original=Report(a);Data.Save(reportPath,original);Data.Save(catalog,new{episodes=new[]{one,two}});
   var handler=new Handler([]);using var client=new HttpClient(handler);
   new MediaPipeline(new DialogueMatcher(client)).RunAsync(new(root,catalog,output,"model","http://127.0.0.1:1234/v1/chat/completions",SelectedFiles:[a.Text("path")],ReviewIssues:true),new InlineProgress(_=>{}),default).GetAwaiter().GetResult();
   Assert(handler.Requests.Count==0&&JsonNode.DeepEquals(Data.Read(reportPath),original));
  });
  test("Scoped conflict review includes newly competing recordings but leaves separate conflicts untouched",()=>{
   var a=Row("scope/a.mkv",'a',"a","complete",one);var b=Row("scope/b.mkv",'b',"b","complete",one);
   var c=Row("scope/c.mkv",'c',"c","complete",two);var d=Row("scope/d.mkv",'d',"d","complete",two);var report=Report(a,b,c,d);var beforeC=c.DeepClone();var beforeD=d.DeepClone();
   var handler=new Handler([Result(Cues("a"),"complete",one),Result(Cues("a"),"complete",one),Result(Cues("b"),"complete",one),Result(Cues("b"),"complete",one)]);using var client=new HttpClient(handler);
   ConflictReview.RunAsync(report,[one,two],new DialogueMatcher(client),"model","http://127.0.0.1:1234/v1/chat/completions",()=>{},new InlineProgress(_=>{}),default,allowedPaths:new HashSet<string>{Path.GetFullPath(a.Text("path"))}).GetAwaiter().GetResult();
   Assert(handler.Requests.Count==4&&JsonNode.DeepEquals(c,beforeC)&&JsonNode.DeepEquals(d,beforeD)&&CollectionResolver.Resolve(report).All(g=>g.Episode==null));
  });
  test("Issue review preserves unrelated verified split assignments",()=>{
   var a=Row("issues-split/a.mkv",'a',"beginning","partial",one);var b=Row("issues-split/b.mkv",'b',"ending","partial",one);
   var c=Row("issues-split/c.mkv",'c',"third","complete",two);c["error"]="failed";c["candidates"]=new JsonArray();c["full_dialogue"]=true;c["size"]=c["primary_fingerprint"]!["size"]!.DeepClone();c["sampled_digest"]=c["primary_fingerprint"]!["sampled_digest"]!.DeepClone();
   var original=Report(a,b,c);var link=Link(a,b);original["split_assignments"]=new JsonArray(link);
   var root=Path.GetDirectoryName(a.Text("path"))!;var output=Path.Combine(temp,"issues-split-output");var catalog=Path.Combine(output,"catalog.json");var reportPath=Path.Combine(output,"report.json");Data.Save(catalog,new{episodes=new[]{one,two}});Data.Save(reportPath,original);
   var handler=new Handler([Result(Cues("third"),"complete",two),Result(Cues("third"),"complete",two)]);using var client=new HttpClient(handler);
   new MediaPipeline(new DialogueMatcher(client)).RunAsync(new(root,catalog,output,"model","http://127.0.0.1:1234/v1/chat/completions",Ffmpeg:"must-not-run",Ffprobe:"must-not-run",SelectedFiles:[a.Text("path"),b.Text("path"),c.Text("path")],ReviewIssues:true),new InlineProgress(_=>{}),default).GetAwaiter().GetResult();
   var final=Data.Read(reportPath);Assert(handler.Requests.Count==2&&JsonNode.DeepEquals(final.Array("split_assignments").Single(),link)&&CollectionResolver.Resolve(final).Count(g=>g.Split)==2);
  });
  test("Reanalysis reruns inference reuses media evidence and resumes only pending files",()=>{
   var a=Row("reanalyze/a.mkv",'a',"first","complete",one);var b=Row("reanalyze/b.mkv",'b',"second","complete",two);
   foreach(var row in new[]{a,b}) {row["full_dialogue"]=true;row["size"]=row["primary_fingerprint"]!["size"]!.DeepClone();row["sampled_digest"]=row["primary_fingerprint"]!["sampled_digest"]!.DeepClone();}
   var root=Path.GetDirectoryName(a.Text("path"))!;var output=Path.Combine(temp,"reanalyze-output");var catalog=Path.Combine(output,"catalog.json");var reportPath=Path.Combine(output,"report.json");
   Data.Save(catalog,new{episodes=new[]{one,two}});Data.Save(reportPath,Report(a,b));
   var replies=new List<JsonObject>{Result(Cues("first"),"complete",one),Result(Cues("first"),"complete",one),Result(Cues("second"),"complete",two),Result(Cues("second"),"complete",two),Result(Cues("first"),"complete",one),Result(Cues("first"),"complete",one),Result(Cues("second"),"complete",two),Result(Cues("second"),"complete",two)};
   var handler=new Handler(replies);using var client=new HttpClient(handler);var pipeline=new MediaPipeline(new DialogueMatcher(client));
   var settings=new MatchSettings(root,catalog,output,"model","http://127.0.0.1:1234/v1/chat/completions",Ffmpeg:"must-not-run-ffmpeg",Ffprobe:"must-not-run-ffprobe",SelectedFiles:[a.Text("path"),b.Text("path")],Reanalyze:true);
   var pause=new ScoutPause();
   try {pipeline.RunAsync(settings,new InlineProgress(m=>{if(m.StartsWith("[1/2]"))pause.Request();}),default,pause).GetAwaiter().GetResult();throw new Exception("Expected pause");}catch(ScoutPausedException){}
   var checkpoint=Data.Read(reportPath);var generation=checkpoint.Text("analysis_generation");
   Assert(handler.Requests.Count==2&&checkpoint.Array("files")[1].Text("status")=="analysis_pending"&&CollectionResolver.Resolve(checkpoint).Count(g=>g.Episode!=null)==1);
   Assert(JsonNode.DeepEquals(checkpoint.Array("files")[0]!["primary_fingerprint"],a["primary_fingerprint"]));
   Assert(ScoutStorage.Exists(Path.Combine(output,"analysis-history",generation,"report.json")));
   pipeline.RunAsync(settings with {Reanalyze=false,Restart=true},new InlineProgress(_=>{}),default).GetAwaiter().GetResult();
   Assert(handler.Requests.Count==4&&Data.Read(reportPath).Text("analysis_generation")==generation&&CollectionResolver.Resolve(Data.Read(reportPath)).All(g=>g.Episode!=null));
   pipeline.RunAsync(settings,new InlineProgress(_=>{}),default).GetAwaiter().GetResult();
   Assert(handler.Requests.Count==8&&Data.Read(reportPath).Text("analysis_generation")!=generation);
   File.AppendAllText(a.Text("path"),"changed");Assert(!RunRecovery.SourceUnchanged(a));
  });
  test("Applying combined and split moves preserves their verified dialogue assignments",()=>{
   var combined=Row("move-combined/a.mkv",'a',"both","combined",one,two);
   var root=Path.GetDirectoryName(combined.Text("path"))!;var reportPath=Path.Combine(temp,"move-combined-output","report.json");
   Data.Save(reportPath,Report(combined));var plan=PreviewPlanner.Create(Data.Read(reportPath),root,"Show",[one,two]);
   MoveExecutor.ApplyAsync(reportPath,plan,[combined.Text("path")],[one,two],new MoveProgress(),default).GetAwaiter().GetResult();
   var saved=Data.Read(reportPath);Assert(CollectionResolver.Resolve(saved).Single().Combined&&saved.Array("files")[0].Text("path").Contains("S01E01-E02"));
   Assert(JsonNode.DeepEquals(saved.Array("files")[0]!["reasoning"],combined["reasoning"]));
   var a=Row("move-split/a.mkv",'b',"first","partial",one);var b=Row("move-split/b.mkv",'c',"last","partial",one);
   root=Path.GetDirectoryName(a.Text("path"))!;reportPath=Path.Combine(temp,"move-split-output","report.json");
   var split=Report(a,b);split["split_assignments"]=new JsonArray(Link(a,b));Data.Save(reportPath,split);
   plan=PreviewPlanner.Create(split,root,"Show",[one,two]);
   MoveExecutor.ApplyAsync(reportPath,plan,plan.Items.Select(i=>i.Source).ToArray(),[one,two],new MoveProgress(),default).GetAwaiter().GetResult();
   saved=Data.Read(reportPath);Assert(CollectionResolver.Resolve(saved).All(g=>g.Split)&&saved.Array("files").All(r=>r.Text("path").Contains("(Part ")));
   Assert(JsonNode.DeepEquals(saved["split_assignments"],split["split_assignments"]));
  });
  test("Combined episodes resolve one recording and name both episodes",()=>{
   var report=Report(Row("combined.mkv",'a',"combined","combined",one,two));var group=CollectionResolver.Resolve(report).Single();
   Assert(group.Combined&&group.Episodes.Count==2&&group.Code=="S01E01-E02");
   var plan=PreviewPlanner.Create(report,temp,"Show",[one,two]);var item=plan.Items.Single();
   Assert(item.Category=="combined"&&item.Destination!.Contains("S01E01-E02")&&item.Destination.Contains("Opening")&&item.Destination.Contains("Aftermath")&&item.Action=="rename/move"&&plan.NotIdentified.Count==0);
  });
  test("Combined validation rejects alternatives overlapping blocks and nonconsecutive numbers",()=>{
   var cues=Cues("source");var result=Result(cues,"combined",one,two);result.Array("candidate_checks")[1]!["supporting_blocks"]=new JsonArray(1,2);
   Reject(()=>DialogueMatcher.Validate(result,DialogueMatcher.Blocks(cues),[one,two],true));
   var nonconsecutive=two with {Number=3};result=Result(cues,"combined",one,nonconsecutive);Reject(()=>DialogueMatcher.Validate(result,DialogueMatcher.Blocks(cues),[one,nonconsecutive],true));
   result=Result(cues,"complete",one,two);Reject(()=>DialogueMatcher.Validate(result,DialogueMatcher.Blocks(cues),[one,two],true));
  });
  test("Combined duplicates remain copies and competing recordings conflict",()=>{
   var a=Row("combined-a.mkv",'a',"combo","combined",one,two);var copy=Row("combined-copy.mkv",'a',"combo","combined",one,two);
   var report=Report(a,copy);Assert(CollectionResolver.Resolve(report).Single().Copies==2);
   Assert(PreviewPlanner.Create(report,temp,"Show").Items.Count(i=>i.Category=="duplicate")==1);
   report.Array("files").Add(Row("competing.mkv",'b',"independent","complete",two));Assert(CollectionResolver.Resolve(report).All(g=>g.Status=="unresolved_conflict"));
  });
  test("Combined saved evidence cannot use stale dialogue",()=>{
   var row=Row("stale-combo.mkv",'a',"source","combined",one,two);row["dialogue"]!["cues"]=new JsonArray();
   Assert(CollectionResolver.Resolve(Report(row)).Single().Status=="invalid_model_output");
  });
  test("Verified split pair uses ordered part suffixes and one catalog assignment",()=>{
   var a=Row("half-a.mkv",'a',"beginning","partial",one);var b=Row("half-b.mkv",'b',"ending","partial",one);var report=Report(a,b);report["split_assignments"]=new JsonArray(Link(a,b,1));
   var groups=CollectionResolver.Resolve(report);Assert(groups.All(g=>g.Split)&&groups[0].Part==2&&groups[1].Part==1);
   var plan=PreviewPlanner.Create(report,temp,"Show",[one,two]);Assert(plan.Items.All(i=>i.Category=="split"&&i.Action=="rename/move"));
   Assert(plan.Items.Any(i=>i.Destination!.Contains("(Part 1 of 2)"))&&plan.Items.Any(i=>i.Destination!.Contains("(Part 2 of 2)"))&&plan.NotIdentified.Single().Id==two.Id);
   Assert(EvidenceReview.Create(groups[0]).Sections.Any(s=>s.Heading.StartsWith("Split continuity evidence")));
  });
  test("Unverified fragments remain unresolved without false episode assignments",()=>{
   var a=Row("missing-half.mkv",'a',"fragment","partial",one);var group=CollectionResolver.Resolve(Report(a)).Single();
   Assert(group.Status=="partial_unverified"&&group.Episode==null);Assert(PreviewPlanner.Create(Report(a.DeepClone().AsObject()),temp,"Show").Items.Single().Action=="hold");
  });
  test("Split verification rejects duplicate dialogue, order disagreement and stale source",()=>{
   var a=Row("split-x.mkv",'a',"beginning","partial",one);var b=Row("split-y.mkv",'b',"ending","partial",one);var link=Link(a,b);link["verification"]!["first_recording"]=1;
   var report=Report(a,b);report["split_assignments"]=new JsonArray(link);Assert(CollectionResolver.Resolve(report).All(g=>g.Status=="unresolved_conflict"));
   link["verification"]!["first_recording"]=0;b["dialogue"]!["cues"]![0]!["text"]="Changed dialogue source.";Assert(CollectionResolver.Resolve(report).All(g=>g.Episode==null));
   var copy=a.DeepClone().AsObject();Reject(()=>MultipartEvidence.ValidateSplit(Answer(),a,copy,one));
   var bad=Answer();bad["evidence_b"]=new JsonArray(99,100);Reject(()=>MultipartEvidence.ValidateSplit(bad,a,b,one));
  });
  test("Exact duplicates of split parts stay duplicates not additional parts",()=>{
   var a=Row("part-first.mkv",'a',"beginning","partial",one);var copy=Row("part-first-copy.mkv",'a',"beginning","partial",one);var b=Row("part-last.mkv",'b',"ending","partial",one);var report=Report(a,copy,b);report["split_assignments"]=new JsonArray(Link(a,b));
   var groups=CollectionResolver.Resolve(report);Assert(groups.Count==2&&groups.All(g=>g.Split)&&groups.Sum(g=>g.Copies-1)==1);
   var plan=PreviewPlanner.Create(report,temp,"Show");Assert(plan.Items.Count(i=>i.Category=="split")==2&&plan.Items.Count(i=>i.Category=="duplicate")==1);
   report.Array("files").Add(Row("third-contender.mkv",'c',"other","partial",one));Assert(CollectionResolver.Resolve(report).All(g=>g.Episode==null));
  });
  test("Existing multipart names are preserved and collisions held",()=>{
   var naming=new NamingOptions();var name=naming.AssignmentName("Show",[one,two]);var row=Row("Season 01/"+name+".mkv",'a',"combo","combined",one,two);
   Assert(PreviewPlanner.Create(Report(row),temp,"Show").Items.Single().Action=="unchanged");
   var a=Row("to-move-a.mkv",'a',"beginning","partial",one);var b=Row("to-move-b.mkv",'b',"ending","partial",one);var report=Report(a,b);report["split_assignments"]=new JsonArray(Link(a,b));
   var plan=PreviewPlanner.Create(report,temp,"Show");var destination=plan.Items[0].Destination!;Directory.CreateDirectory(Path.GetDirectoryName(destination)!);File.WriteAllText(destination,"occupied");
   Assert(PreviewPlanner.Create(report,temp,"Show").Items[0].Action=="hold");
   Assert(FilenameCatalogMatcher.Match("Show-S01E01 (Part 1 of 2).mkv",[one])==null);
  });
  test("Combined typography and boundary conjunction preserve correct filenames",()=>{
   var first=one with {Number=9,Title="Gramma's License"};var second=two with {Number=10,Title="Bear Trapped"};
   foreach(var separator in new[]{".and.",".&."," - "}) {
    var row=Row("Season 01/Big.City.Greens-S01E09-E10-Grammas.License"+separator+"Bear.Trapped.mkv",'a',"combo","combined",first,second);
    var report=Report(row);var plan=PreviewPlanner.Create(report,temp,"Big City Greens");
    Assert(plan.Items.Single().Action=="unchanged"&&plan.Items.Single().Destination==Path.GetFullPath(row.Text("path")));
    var formatted=PreviewPlanner.Create(report,temp,"Big City Greens",naming:new NamingOptions{PreserveCorrectNames=false});
    Assert(formatted.Items.Single().Change=="Formatting only");
   }
   var naming=new NamingOptions();
   Assert(!naming.EquivalentAssignmentName("Big.City.Greens-S01E09-E11-Grammas.License.and.Bear.Trapped.mkv","Big City Greens",[first,second]));
   Assert(!naming.EquivalentAssignmentName("Big.City.Greens-S01E09-E10-Bear.Trapped.and.Grammas.License.mkv","Big City Greens",[first,second]));
   Assert(!naming.EquivalentAssignmentName("Big.City.Greens-S01E09-E10-Grammas.License.and.Wrong.Title.mkv","Big City Greens",[first,second]));
   var titleWithAnd=first with {Title="This and That"};
   Assert(!naming.EquivalentAssignmentName("Big.City.Greens-S01E09-E10-This.That.and.Bear.Trapped.mkv","Big City Greens",[titleWithAnd,second]));
  });
  test("Rejected guide candidates do not invalidate selected combined evidence",()=>{
   var row=Row("rejected-candidate.mkv",'a',"combo","combined",one,two);
   row["reasoning"]!.Array("candidate_checks").Add(new JsonObject{["episode_title"]="Rejected title",["supporting_blocks"]=new JsonArray(0),["contradicting_blocks"]=new JsonArray(3),["guide_event"]="Other story"});
   row["reasoning"]!.Array("alternatives").Add(new JsonObject{["episode_id"]=999,["reason"]="Rejected"});
   Assert(CollectionResolver.Resolve(Report(row)).Single().Combined);
   row["reasoning"]!.Array("candidate_checks")[0]!["contradicting_blocks"]=new JsonArray(1);
   Assert(!MultipartEvidence.Combined(row));
  });
  test("Invalid model output retries once using original evidence and numbered guide",()=>{
   var cues=Cues("retry");var invalid=Result(cues,"combined",two,one);
   var handler=new Handler([invalid,Result(cues,"combined",one,two),Result(cues,"combined",one,two)]);using var client=new HttpClient(handler);
   var result=new DialogueMatcher(client).IdentifyAsync(cues,[one,two],"model","http://127.0.0.1:1234/v1/chat/completions").GetAwaiter().GetResult();
   Assert(handler.Requests.Count==3&&result["verification"]!["initial_result"]!.Array("validation_retries").Count==1);
   Assert(handler.Requests[0].Contains("season")&&handler.Requests[1].Contains("previous response failed validation"));
   var bad=Result(cues,"unknown");bad["assessment"]="supported";
   var failures=new Handler([bad,bad]);using var other=new HttpClient(failures);
   Reject(()=>new DialogueMatcher(other).DirectAsync(cues,[one,two],"model","http://127.0.0.1:1234/v1/chat/completions",false).GetAwaiter().GetResult());
   Assert(failures.Requests.Count==2);
  });
  test("Restart reuses saved results and fingerprints but rejects changed sources and failed rows",()=>{
   var row=Row("recovery.mkv",'a',"saved","complete",one);
   Assert(RunRecovery.CanReuse(row,[one]));
   row["error"]="Interrupted request";Assert(!RunRecovery.CanReuse(row,[one]));row.Remove("error");
   Assert(!RunRecovery.CanReuse(row,[one with {Title="Different guide"}]));
   File.AppendAllText(row.Text("path"),"changed");Assert(!RunRecovery.CanReuse(row,[one]));
  });
  test("Restart preserves completed rows while checkpointing failed and unprocessed files",()=>{
   var root=Path.Combine(temp,"restart-library");var output=Path.Combine(temp,"restart-output");
   var completed=Row("restart-library/done.mkv",'a',"saved","complete",one);
   var failed=Row("restart-library/failed.mkv",'b',"failed","complete",two);failed["error"]="Old failure";
   File.Delete(failed.Text("path"));var missing=Path.Combine(root,"unfinished.mkv");
   var catalog=Path.Combine(output,"catalog.json");Data.Save(catalog,new{episodes=new[]{one,two}});
   Data.Save(Path.Combine(output,"report.json"),Report(failed,completed));
   var handler=new Handler([]);using var client=new HttpClient(handler);var pipeline=new MediaPipeline(new DialogueMatcher(client));
   var result=pipeline.RunAsync(new(root,catalog,output,"model","http://127.0.0.1:1234/v1/chat/completions",SelectedFiles:[failed.Text("path"),missing,completed.Text("path")],Restart:true),new InlineProgress(_=>{}),default).GetAwaiter().GetResult();
   var saved=Data.Read(result).Array("files");Assert(saved.Count==3&&handler.Requests.Count==0);
   Assert(JsonNode.DeepEquals(saved.Single(r=>r.Text("path")==completed.Text("path")),completed));
   Assert(saved.Count(r=>r.Text("error")!="")==2);
   using var cancel=new CancellationTokenSource();cancel.Cancel();
   try {pipeline.RunAsync(new(root,catalog,output,"model","http://127.0.0.1:1234/v1/chat/completions",SelectedFiles:[completed.Text("path")],Restart:true),new InlineProgress(_=>{}),cancel.Token).GetAwaiter().GetResult();throw new Exception("Expected cancellation");}catch(OperationCanceledException){}
   Assert(Data.Read(result).Array("files").Count==3);
  });
  test("Timed-out file checkpoints evidence while the next file completes and resume retries only the failure",()=>{
   var root=Path.Combine(temp,"timeout-library");var output=Path.Combine(temp,"timeout-output");var catalog=Path.Combine(output,"catalog.json");
   var first=Row(Path.Combine("timeout-library","first.mkv"),'a',"first","complete",one);var second=Row(Path.Combine("timeout-library","second.mkv"),'b',"second","complete",two);
   foreach(var row in new[]{first,second}) {
    row["error"]="Awaiting analysis";row["full_dialogue"]=true;row["size"]=row["primary_fingerprint"]!["size"]!.DeepClone();
    row["source_mtime_ns"]=row["primary_fingerprint"]!["mtime_ns"]!.DeepClone();row["sampled_digest"]=row["primary_fingerprint"]!["sampled_digest"]!.DeepClone();
   }
   Data.Save(catalog,new{episodes=new[]{one,two}});Data.Save(Path.Combine(output,"report.json"),Report(first,second));
   var handler=new TimeoutOnceHandler([Result(Cues("second"),"complete",two),Result(Cues("second"),"complete",two),Result(Cues("first"),"complete",one),Result(Cues("first"),"complete",one)]);
   using var client=new HttpClient(handler){Timeout=TimeSpan.FromMilliseconds(250)};
   var pipeline=new MediaPipeline(new DialogueMatcher(client),(_,_,_)=>throw new Exception("Saved dialogue and fingerprints must be reused"));
   var settings=new MatchSettings(root,catalog,output,"model","http://127.0.0.1:1234/v1/chat/completions",SelectedFiles:[first.Text("path"),second.Text("path")],Restart:true);
   var saved=Data.Read(pipeline.RunAsync(settings,new InlineProgress(_=>{}),default).GetAwaiter().GetResult());
   var failed=saved.Array("files").Single(r=>r.Text("path")==first.Text("path"))!.AsObject();
   var done=saved.Array("files").Single(r=>r.Text("path")==second.Text("path"))!.AsObject();
   Assert(handler.Requests==3&&failed.Text("error_code")=="model_timeout"&&failed["timeout_seconds"]!.GetValue<double>()==0.25&&failed.Array("candidates").Count==0);
   Assert(JsonNode.DeepEquals(failed["dialogue"],first["dialogue"])&&JsonNode.DeepEquals(failed["primary_fingerprint"],first["primary_fingerprint"]));
   Assert(!RunRecovery.CanReuse(failed,[one,two])&&RunRecovery.CanReuse(done,[one,two])&&IssueReview.Select(saved).Contains(first.Text("path")));
   var final=Data.Read(pipeline.RunAsync(settings,new InlineProgress(_=>{}),default).GetAwaiter().GetResult());
   Assert(handler.Requests==5&&final.Array("files").All(r=>r.Text("error")=="")&&CollectionResolver.Resolve(final).All(g=>g.Episode!=null));
   Assert(JsonNode.DeepEquals(final.Array("files").Single(r=>r.Text("path")==second.Text("path")),done));
  });
  test("Pause is queued without cancelling work and stops after saving the current file",()=>{
   var root=Path.Combine(temp,"pause-library");Directory.CreateDirectory(root);
   var output=Path.Combine(temp,"pause-output");var catalog=Path.Combine(output,"catalog.json");Data.Save(catalog,new{episodes=new[]{one}});
   var first=Path.Combine(root,"missing-first.mkv");var second=Path.Combine(root,"missing-second.mkv");
   var pause=new ScoutPause();var handler=new Handler([]);using var client=new HttpClient(handler);
   var settings=new MatchSettings(root,catalog,output,"model","http://127.0.0.1:1234/v1/chat/completions",SelectedFiles:[first,second],Restart:true);
   var progress=new InlineProgress(message=>{if(message.StartsWith("[1/2]"))pause.Request();});
   try {new MediaPipeline(new DialogueMatcher(client)).RunAsync(settings,progress,default,pause).GetAwaiter().GetResult();throw new Exception("Expected pause");}catch(ScoutPausedException){}
   var saved=Data.Read(Path.Combine(output,"report.json")).Array("files");
   Assert(saved.Count==1&&saved[0].Text("path")==first&&saved[0].Text("error")!="");
   var before=Data.Read(Path.Combine(output,"report.json")).ToJsonString();
   try {new MediaPipeline(new DialogueMatcher(client)).RunAsync(settings,new InlineProgress(_=>{}),default,pause).GetAwaiter().GetResult();throw new Exception("Expected pause");}catch(ScoutPausedException){}
   Assert(Data.Read(Path.Combine(output,"report.json")).ToJsonString()==before&&handler.Requests.Count==0);
  });
  test("Conflict review checks every claimant against the full guide without choosing a winner by order",()=>{
   var a=Row("claim-a.mkv",'a',"a","complete",one);var b=Row("claim-b.mkv",'b',"b","complete",one);var report=Report(a,b);
   Assert(EvidenceReview.Create(CollectionResolver.Resolve(report)[0]).Sections.Any(s=>s.Heading=="Competing recordings"&&s.Text.Contains("claim-b.mkv")));
   var handler=new Handler([Result(Cues("a"),"complete",one),Result(Cues("a"),"complete",one),Result(Cues("b"),"complete",two),Result(Cues("b"),"complete",two)]);using var client=new HttpClient(handler);
   var matcher=new DialogueMatcher(client);int saves=0;
   ConflictReview.RunAsync(report,[one,two],matcher,"model","http://127.0.0.1:1234/v1/chat/completions",()=>saves++,new InlineProgress(_=>{}),default).GetAwaiter().GetResult();
   Assert(handler.Requests.Count==4&&saves==4&&CollectionResolver.Resolve(report).All(g=>g.Episode!=null));
   Assert(handler.Requests.All(r=>r.Contains("Opening")&&r.Contains("Aftermath")&&!r.Contains("claim-a.mkv")&&!r.Contains("claim-b.mkv")));
   Assert(b["candidates"]![0]!["episode"]!.Int("id")==two.Id&&b["conflict_review"]!=null);
  });
  test("Disagreeing full-guide checks keep conflicts held and pending review survives pause",()=>{
   var a=Row("disagree-a.mkv",'a',"a","complete",one);var b=Row("disagree-b.mkv",'b',"b","complete",one);var report=Report(a,b);
   var handler=new Handler([Result(Cues("a"),"complete",one),Result(Cues("a"),"complete",two),Result(Cues("b"),"complete",one),Result(Cues("b"),"complete",one)]);using var client=new HttpClient(handler);
   ConflictReview.RunAsync(report,[one,two],new DialogueMatcher(client),"model","http://127.0.0.1:1234/v1/chat/completions",()=>{},new InlineProgress(_=>{}),default).GetAwaiter().GetResult();
   Assert(a.Text("conflict_review_error").Contains("disagree")&&CollectionResolver.Resolve(report).All(g=>g.Episode==null));
   var pause=new ScoutPause();pause.Request();
   try {ConflictReview.RunAsync(report,[one,two],new DialogueMatcher(client),"model","http://127.0.0.1:1234/v1/chat/completions",()=>{},new InlineProgress(_=>{}),default,pause).GetAwaiter().GetResult();throw new Exception("Expected pause");}catch(ScoutPausedException){}
   Assert(report.Array("files").All(r=>r.Bool("conflict_review_pending"))&&RunRecovery.CanReuse(b,[one,two]));
  });
  test("Conflict review resumes after a checkpoint and does not repeatedly infer unchanged conflicts",()=>{
   var a=Row("resume-claim-a.mkv",'a',"a","complete",one);var b=Row("resume-claim-b.mkv",'b',"b","complete",one);var report=Report(a,b);
   var handler=new Handler([Result(Cues("a"),"complete",one),Result(Cues("a"),"complete",one),Result(Cues("b"),"complete",one),Result(Cues("b"),"complete",one)]);using var client=new HttpClient(handler);var matcher=new DialogueMatcher(client);var pause=new ScoutPause();
   try {ConflictReview.RunAsync(report,[one,two],matcher,"model","http://127.0.0.1:1234/v1/chat/completions",()=>{if(a["conflict_review"]!=null)pause.Request();},new InlineProgress(_=>{}),default,pause).GetAwaiter().GetResult();throw new Exception("Expected pause");}catch(ScoutPausedException){}
   Assert(handler.Requests.Count==2&&report.Array("files").All(r=>r.Bool("conflict_review_pending")));
   ConflictReview.RunAsync(report,[one,two],matcher,"model","http://127.0.0.1:1234/v1/chat/completions",()=>{},new InlineProgress(_=>{}),default).GetAwaiter().GetResult();
   Assert(handler.Requests.Count==4&&CollectionResolver.Resolve(report).All(g=>g.Status=="unresolved_conflict"));
   ConflictReview.RunAsync(report,[one,two],matcher,"model","http://127.0.0.1:1234/v1/chat/completions",()=>{},new InlineProgress(_=>{}),default).GetAwaiter().GetResult();
   Assert(handler.Requests.Count==4&&report.Array("files").All(r=>!r.Bool("conflict_review_pending")));
  });
  test("Context recovery preserves full dialogue guide and strict schema with bounded retry",()=>{
   var cues=Cues("context-test");var handler=new Handler([Result(cues,"complete",one),Result(cues,"complete",one)],1);using var client=new HttpClient(handler);
   var result=new DialogueMatcher(client).IdentifyAsync(cues,[one,two],"model","http://127.0.0.1:1234/v1/chat/completions").GetAwaiter().GetResult();
   Assert(handler.Requests.Count==3&&result["verification"]!["initial_result"].Text("request_adjustments").Contains("Context overflow"));
   var first=JsonNode.Parse(handler.Requests[0])!;var retry=JsonNode.Parse(handler.Requests[1])!;
   Assert(first["messages"]![0].Text("content")==retry["messages"]![0].Text("content"));
   Assert(first["messages"]![1].Text("content").Length>retry["messages"]![1].Text("content").Length);
   Assert(!first["messages"]![0].Text("content").Contains(one.Overview));
   Assert(VerificationTests.Evidence(first)==VerificationTests.Evidence(retry)&&JsonNode.DeepEquals(first["response_format"],retry["response_format"]));
   Assert(first["messages"]![1].Text("content").Contains(two.Overview)&&first["messages"]![1].Text("content").Contains(cues.Last().Text));
   var rejected=new Handler([],2);using var rejectedClient=new HttpClient(rejected);
   try{new DialogueMatcher(rejectedClient).IdentifyAsync(cues,[one,two],"model","http://127.0.0.1:1234/v1/chat/completions").GetAwaiter().GetResult();throw new Exception("Expected context rejection");}catch(ModelContextException){}
   Assert(rejected.Requests.Count==2);
  });
  test("Combined matcher verifies both episodes against original dialogue",()=>{
   var cues=Cues("two-stories");var initial=Result(cues,"combined",one,two);initial["summary"]="INITIAL SUMMARY MUST NOT LEAK";
   var handler=new Handler([initial,Result(cues,"combined",one,two)]);using var client=new HttpClient(handler);
   var result=new DialogueMatcher(client).IdentifyAsync(cues,[one,two],"model","http://127.0.0.1:1234/v1/chat/completions").GetAwaiter().GetResult();
   Assert(result.Array("matches").Count==2&&result.Text("coverage")=="combined"&&handler.Requests.Count==2&&!handler.Requests[1].Contains("INITIAL SUMMARY MUST NOT LEAK"));
  });
  test("Split pairs with errors or complete copies cannot bypass conflicts",()=>{
   var a=Row("error-a.mkv",'a',"beginning","partial",one);var b=Row("error-b.mkv",'b',"ending","partial",one);var report=Report(a,b);report["split_assignments"]=new JsonArray(Link(a,b));
   a["error"]="Source unavailable";Assert(CollectionResolver.Resolve(report).All(g=>g.Episode==null));
   a.Remove("error");a["reasoning"]!["coverage"]="complete";Assert(CollectionResolver.Resolve(report).All(g=>g.Episode==null));
  });
  test("Changed combined text and source files invalidate assignments and moves",()=>{
   var row=Row("changed-combined.mkv",'a',"combo","combined",one,two);
   row["dialogue"]!["cues"]![0]!["text"]="Different story dialogue";Assert(CollectionResolver.Resolve(Report(row)).Single().Episode==null);
   row=Row("changed-source-combo.mkv",'a',"combo","combined",one,two);File.AppendAllText(row.Text("path"),"modified");
   Assert(PreviewPlanner.Create(Report(row),temp,"Show").Items.Single().Action=="hold");
  });
  test("Split verifier independently checks both sources and rejects disagreement",()=>{
   var a=Row("http-a.mkv",'a',"beginning","partial",one);var b=Row("http-b.mkv",'b',"ending","partial",one);
   var first=Answer();first["reason"]="FIRST ANSWER MUST NOT LEAK";
   var handler=new Handler([first,Answer()]);using var client=new HttpClient(handler);var matcher=new DialogueMatcher(client);
   var link=matcher.VerifySplitAsync(a,b,one,"model","http://127.0.0.1:1234/v1/chat/completions",default).GetAwaiter().GetResult();
   Assert(link!=null&&handler.Requests.Count==2&&!handler.Requests[1].Contains("FIRST ANSWER MUST NOT LEAK"));
   var disagree=new Handler([Answer(),Answer(1)]);using var other=new HttpClient(disagree);
   Assert(new DialogueMatcher(other).VerifySplitAsync(a,b,one,"model","http://127.0.0.1:1234/v1/chat/completions",default).GetAwaiter().GetResult()==null);
  });
 }
 private sealed class TimeoutOnceHandler(List<JsonObject> replies):HttpMessageHandler {
  public int Requests;
  protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) {
   Requests++;
   if(Requests==1){await Task.Delay(Timeout.Infinite,ct);throw new Exception("Unreachable");}
   return new(HttpStatusCode.OK){Content=new StringContent(new JsonObject{["choices"]=new JsonArray(new JsonObject{["finish_reason"]="stop",["message"]=new JsonObject{["content"]=VerificationTests.WireResponse(JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!,replies[Requests-2]).ToJsonString()}})}.ToJsonString())};
  }
 }
 private sealed class MoveProgress:IProgress<MoveUpdate>{public void Report(MoveUpdate value){}}
 private sealed class InlineProgress(Action<string> action):IProgress<string> { public void Report(string value)=>action(value); }
 private sealed class Handler(List<JsonObject> replies,int contextFailures=0):HttpMessageHandler {
  public List<string> Requests=[];
  protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) {
   Requests.Add(await request.Content!.ReadAsStringAsync(ct));
   if(Requests.Count<=contextFailures)return new(HttpStatusCode.BadRequest){Content=new StringContent("""{"error":{"type":"exceed_context_size_error"}}""")};
   return new(HttpStatusCode.OK){Content=new StringContent(new JsonObject{["choices"]=new JsonArray(new JsonObject{["finish_reason"]="stop",["message"]=new JsonObject{["content"]=VerificationTests.WireResponse(JsonNode.Parse(Requests[^1])!,replies[Requests.Count-1-contextFailures]).ToJsonString()}})}.ToJsonString())};
  }
 }
}
