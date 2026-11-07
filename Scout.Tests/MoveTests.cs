using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Scout.Core;
static class MoveTests {
 public static void Run(Action<string,Action> test,string temp) {
  void Assert(bool value){if(!value)throw new Exception("Move assertion failed: "+new System.Diagnostics.StackTrace(true));}
  void Reject(Action action){try{action();throw new Exception("Expected move rejection");}catch(InvalidOperationException){}}
  var one=new Episode(901,1,1,"Opening");var two=new Episode(902,1,2,"Ending");Episode[] catalog=[one,two];
  (string Root,string Report) Setup(string name)=> (Path.Combine(temp,name),Path.Combine(temp,name+"-data","report.json"));
  JsonObject Row(string root,string name,Episode episode,string content) {
   var path=Path.Combine(root,name);Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path,content);
   return new JsonObject{["path"]=path,["matching_method"]="reasoning",["saved_evidence"]="retain this",
    ["candidates"]=new JsonArray(new JsonObject{["episode"]=JsonSerializer.SerializeToNode(episode,Data.Options)}),
    ["primary_fingerprint"]=new JsonObject{["algorithm"]="full-file-sha256-v1",["sha256"]=Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))),["size"]=new FileInfo(path).Length,["mtime_ns"]=PreviewPlanner.MtimeNs(path),["sampled_digest"]=PreviewPlanner.SampledDigest(path)}};
  }
  void Save(string path,params JsonObject[] rows)=>Data.Save(path,new JsonObject{["files"]=new JsonArray(rows.Select(r=>(JsonNode)r).ToArray())});
  MovePlan Plan(string path,string root)=>PreviewPlanner.Create(Data.Read(path),root,"Show",catalog,naming:new NamingOptions{PreserveCorrectNames=false});
  var silent=new InlineProgress<MoveUpdate>(_=>{});var log=new InlineProgress<string>(_=>{});
  test("Selected moves retain analysis and completed history while unselected files remain pending",()=>{
   var (root,path)=Setup("moves-subset");var a=Row(root,"a.mkv",one,"first");var b=Row(root,"b.mkv",two,"second");Save(path,a,b);
   var plan=Plan(path,root);var selected=plan.Items.Single(i=>i.Episode!.Id==one.Id);
   MoveExecutor.ApplyAsync(path,plan,[selected.Source],catalog,silent,default).GetAwaiter().GetResult();
   Assert(!File.Exists(selected.Source)&&File.ReadAllText(selected.Destination!)=="first"&&File.Exists(b.Text("path")));
   var saved=Data.Read(path);var moved=saved.Array("files").Single(r=>r.Text("path")==selected.Destination)!;
   Assert(moved.Text("saved_evidence")=="retain this"&&moved.Array("path_history").Count==1&&RunRecovery.SourceUnchanged(moved.AsObject()));
   var updated=Plan(path,root);Assert(updated.Items.Count(i=>i.Action=="completed")==1&&updated.Items.Count(i=>i.Action=="rename/move")==1);
   Reject(()=>MoveExecutor.ApplyAsync(path,updated,[selected.Source],catalog,silent,default).GetAwaiter().GetResult());
   var next=updated.Items.Single(i=>i.Action=="rename/move");MoveExecutor.ApplyAsync(path,updated,[next.Source],catalog,silent,default).GetAwaiter().GetResult();
   Assert(Plan(path,root).Items.All(i=>i.Action=="completed"));
  });
  test("Move completion and retained evidence survive SQLite reopen without loose report files",()=>{
   var (root,path)=Setup("moves-sqlite");var folder=Path.GetDirectoryName(path)!;
   var database=new ScoutDatabase(Path.Combine(temp,"moves-sqlite.db"));ScoutStorage.Register(folder,database);
   try {
    var row=Row(root,"a.mkv",one,"sqlite-content");Save(path,row);var plan=Plan(path,root);
    MoveExecutor.ApplyAsync(path,plan,[row.Text("path")],catalog,silent,default).GetAwaiter().GetResult();
    ScoutStorage.Unregister(folder);ScoutStorage.Register(folder,new ScoutDatabase(database.Path));
    Assert(!File.Exists(path)&&Data.Read(path).Array("files")[0].Text("saved_evidence")=="retain this");
    Assert(Plan(path,root).Items.Single().Action=="completed"&&Data.Read(path).Array("moves").Single()!.Text("state")=="completed");
   }finally{ScoutStorage.Unregister(folder);}
  });
  test("Fast renames retain file identity and block concurrent writers without whole-file checksums",()=>{
   var (root,path)=Setup("moves-fast");var row=Row(root,"a.mkv",one,"unchanged-content");Save(path,row);
   string? before;using(var handle=RenameVerification.OpenGuard(row.Text("path")))before=RenameVerification.Identity(handle.SafeFileHandle);
   Assert(before!=null);
   var plan=Plan(path,root);bool writerBlocked=false;
   void Checkpoint(JsonObject report) {
    Data.Save(path,report);
    if(MoveExecutor.HasPending(report)) {
     try{using var writer=new FileStream(row.Text("path"),FileMode.Open,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete);}
     catch(IOException){writerBlocked=true;}
    }
   }
   MoveExecutor.ApplyAsync(path,plan,[row.Text("path")],catalog,silent,default,Checkpoint).GetAwaiter().GetResult();
   var move=Data.Read(path).Array("moves").Single()!;
   Assert(writerBlocked&&move["sha256"]==null&&move["verification"].Text("method")=="same-volume-rename-v1");
   using var destination=RenameVerification.OpenGuard(plan.Items[0].Destination!);
   Assert(RenameVerification.Identity(destination.SafeFileHandle)==before);
  });
  test("Moving selected files never rereads unselected video contents",()=>{
   var (root,path)=Setup("moves-unselected");var a=Row(root,"a.mkv",one,"a");var b=Row(root,"b.mkv",two,"b");Save(path,a,b);var plan=Plan(path,root);
   using(var locked=new FileStream(b.Text("path"),FileMode.Open,FileAccess.Read,FileShare.None))
    MoveExecutor.ApplyAsync(path,plan,[a.Text("path")],catalog,silent,default).GetAwaiter().GetResult();
   Assert(Data.Read(path).Array("moves").Single()!.Text("state")=="completed"&&File.ReadAllText(b.Text("path"))=="b");
  });
  test("Recovery supports old full-checksum journals and storage without file IDs",()=>{
   foreach(bool legacy in new[]{true,false}) {
    var (root,path)=Setup("moves-compat-"+legacy);var row=Row(root,"a.mkv",one,"recovery-content");Save(path,row);var plan=Plan(path,root);
    void Checkpoint(JsonObject report) {
     if(report.Array("moves").Any(m=>m.Text("state")=="completed"))throw new IOException("Simulated interruption");
     Data.Save(path,report);
    }
    try{MoveExecutor.ApplyAsync(path,plan,[row.Text("path")],catalog,silent,default,Checkpoint).GetAwaiter().GetResult();}catch(IOException){}
    var pending=Data.Read(path);var entry=pending.Array("moves").Single()!.AsObject();
    if(legacy){entry.Remove("verification");entry["sha256"]=row["primary_fingerprint"]!["sha256"]!.DeepClone();}
    else entry["verification"]!["file_id"]=null;
    Data.Save(path,pending);
    var recovered=MoveExecutor.RecoverAsync(path,root,log,default).GetAwaiter().GetResult();
    Assert(recovered.Array("moves").Single()!.Text("state")=="completed"&&!MoveExecutor.HasPending(recovered));
   }
  });
  test("Moves reject changed sources and occupied destinations without overwriting",()=>{
   var (root,path)=Setup("moves-stale");var row=Row(root,"a.mkv",one,"original");Save(path,row);var plan=Plan(path,root);
   File.AppendAllText(row.Text("path"),"changed");
   Reject(()=>MoveExecutor.ApplyAsync(path,plan,[row.Text("path")],catalog,silent,default).GetAwaiter().GetResult());
   Assert(File.Exists(row.Text("path"))&&!File.Exists(plan.Items[0].Destination));
   row=Row(root,"a.mkv",one,"original");Save(path,row);plan=Plan(path,root);
   Directory.CreateDirectory(Path.GetDirectoryName(plan.Items[0].Destination!)!);File.WriteAllText(plan.Items[0].Destination!,"occupant");
   Reject(()=>MoveExecutor.ApplyAsync(path,plan,[row.Text("path")],catalog,silent,default).GetAwaiter().GetResult());
   Assert(File.ReadAllText(plan.Items[0].Destination!)=="occupant"&&File.ReadAllText(row.Text("path"))=="original");
  });
  test("Move intent recovers a crash after rename before the database completion checkpoint",()=>{
   var (root,path)=Setup("moves-recover");var row=Row(root,"a.mkv",one,"content");Save(path,row);var plan=Plan(path,root);
   void Checkpoint(JsonObject report) {if(report.Array("moves").Any(m=>m.Text("state")=="completed"))throw new IOException("Simulated database interruption");Data.Save(path,report);}
   try{MoveExecutor.ApplyAsync(path,plan,[row.Text("path")],catalog,silent,default,Checkpoint).GetAwaiter().GetResult();throw new Exception("Expected interruption");}catch(IOException){}
   Assert(!File.Exists(row.Text("path"))&&File.Exists(plan.Items[0].Destination)&&MoveExecutor.HasPending(Data.Read(path)));
   Assert(CollectionResolver.Resolve(Data.Read(path)).All(g=>g.Episode==null));
   var recovered=MoveExecutor.RecoverAsync(path,root,log,default).GetAwaiter().GetResult();
   Assert(!MoveExecutor.HasPending(recovered)&&recovered.Array("files")[0].Text("path")==plan.Items[0].Destination);
   Assert(recovered.Array("files")[0].Text("saved_evidence")=="retain this"&&Plan(path,root).Items.Single().Action=="completed");
   var again=MoveExecutor.RecoverAsync(path,root,log,default).GetAwaiter().GetResult();Assert(again.Array("files")[0].Array("path_history").Count==1);
  });
  test("Interrupted move intent does not retry media moves and mismatched recovery stays held",()=>{
   var (root,path)=Setup("moves-intent");var row=Row(root,"a.mkv",one,"content");Save(path,row);var plan=Plan(path,root);
   void BeforeMove(JsonObject report){Data.Save(path,report);throw new IOException("Simulated stop after durable intent");}
   try{MoveExecutor.ApplyAsync(path,plan,[row.Text("path")],catalog,silent,default,BeforeMove).GetAwaiter().GetResult();}catch(IOException){}
   var recovered=MoveExecutor.RecoverAsync(path,root,log,default).GetAwaiter().GetResult();
   Assert(File.Exists(row.Text("path"))&&!File.Exists(plan.Items[0].Destination)&&!MoveExecutor.HasPending(recovered));
   plan=Plan(path,root);
   try{MoveExecutor.ApplyAsync(path,plan,[row.Text("path")],catalog,silent,default,BeforeMove).GetAwaiter().GetResult();}catch(IOException){}
   Directory.CreateDirectory(Path.GetDirectoryName(plan.Items[0].Destination!)!);File.Move(row.Text("path"),plan.Items[0].Destination!);File.AppendAllText(plan.Items[0].Destination!,"different");
   recovered=MoveExecutor.RecoverAsync(path,root,log,default).GetAwaiter().GetResult();
   Assert(MoveExecutor.HasPending(recovered)&&recovered.Array("files")[0].Text("path")==row.Text("path")&&CollectionResolver.Resolve(recovered).All(g=>g.Episode==null));
  });
  test("Stopping a move batch checkpoints completed files and leaves the rest untouched",()=>{
   var (root,path)=Setup("moves-stop");var a=Row(root,"a.mkv",one,"a");var b=Row(root,"b.mkv",two,"b");Save(path,a,b);var plan=Plan(path,root);
   using var stop=new CancellationTokenSource();
   var progress=new InlineProgress<MoveUpdate>(u=>{if(u.Message.StartsWith("Moved:"))stop.Cancel();});
   try{MoveExecutor.ApplyAsync(path,plan,plan.Items.Select(i=>i.Source).ToArray(),catalog,progress,stop.Token).GetAwaiter().GetResult();throw new Exception("Expected cancellation");}catch(OperationCanceledException){}
   Assert(Data.Read(path).Array("moves").Count(m=>m.Text("state")=="completed")==1&&Plan(path,root).Items.Count(i=>i.Action=="rename/move")==1);
  });
  test("Moved duplicates keep a stable destination and never become the primary recording",()=>{
   var (root,path)=Setup("moves-duplicates");var a=Row(root,"a.mkv",one,"same");var b=Row(root,"b.mkv",one,"same");Save(path,a,b);var plan=Plan(path,root);var copy=plan.Items.Single(i=>i.Category=="duplicate");
   MoveExecutor.ApplyAsync(path,plan,[copy.Source],catalog,silent,default).GetAwaiter().GetResult();
   var after=Plan(path,root);
   Assert(File.Exists(a.Text("path"))&&after.Items.Single(i=>i.Action=="rename/move").Source==a.Text("path"));
   Assert(after.Items.Count(i=>i.Category=="duplicate")==1&&after.Items.Single(i=>i.Category=="duplicate").Action=="completed");
  });
  test("Selected dependencies move in order but unselected occupants and cycles remain untouched",()=>{
   var (root,path)=Setup("moves-dependency");var a=Row(root,Path.Combine("Season 01","Show - S01E02 - Ending.mkv"),one,"a");var b=Row(root,"b.mkv",two,"b");Save(path,a,b);var plan=Plan(path,root);
   MoveExecutor.ApplyAsync(path,plan,[b.Text("path")],catalog,silent,default).GetAwaiter().GetResult();
   Assert(File.Exists(b.Text("path"))&&File.ReadAllText(a.Text("path"))=="a");
   plan=Plan(path,root);MoveExecutor.ApplyAsync(path,plan,plan.Items.Where(i=>i.Action=="rename/move").Select(i=>i.Source).Reverse().ToArray(),catalog,silent,default).GetAwaiter().GetResult();
   Assert(Plan(path,root).Items.Count(i=>i.Action=="completed")==2);
   var (cycleRoot,cyclePath)=Setup("moves-cycle");
   var c=Row(cycleRoot,Path.Combine("Season 01","Show - S01E02 - Ending.mkv"),one,"c");var d=Row(cycleRoot,Path.Combine("Season 01","Show - S01E01 - Opening.mkv"),two,"d");Save(cyclePath,c,d);var cycle=Plan(cyclePath,cycleRoot);
   MoveExecutor.ApplyAsync(cyclePath,cycle,cycle.Items.Select(i=>i.Source).ToArray(),catalog,silent,default).GetAwaiter().GetResult();
   Assert(File.ReadAllText(c.Text("path"))=="c"&&File.ReadAllText(d.Text("path"))=="d"&&Data.Read(cyclePath).Array("moves").All(m=>m.Text("state")=="failed"));
  });
  test("A destination appearing immediately before rename is never replaced",()=>{
   var (root,path)=Setup("moves-race");var row=Row(root,"a.mkv",one,"source");Save(path,row);var plan=Plan(path,root);bool inserted=false;
   void Checkpoint(JsonObject report) {
    Data.Save(path,report);
    if(!inserted&&MoveExecutor.HasPending(report)){inserted=true;Directory.CreateDirectory(Path.GetDirectoryName(plan.Items[0].Destination!)!);File.WriteAllText(plan.Items[0].Destination!,"occupant");}
   }
   MoveExecutor.ApplyAsync(path,plan,[row.Text("path")],catalog,silent,default,Checkpoint).GetAwaiter().GetResult();
   Assert(File.ReadAllText(row.Text("path"))=="source"&&File.ReadAllText(plan.Items[0].Destination!)=="occupant"&&!MoveExecutor.HasPending(Data.Read(path)));
  });
 }
 private sealed class InlineProgress<T>(Action<T> callback):IProgress<T> {public void Report(T value)=>callback(value);}
}
