using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Scout.Core;
static class DatabaseTests {
 public static void Run(Action<string,Action> test,string temp) {
  void Assert(bool value){if(!value)throw new Exception("Database assertion failed");}
  test("SQLite preserves existing history encrypted keys and file results across reopen",()=>{
   var root=Path.Combine(temp,"db-existing");var media=Path.Combine(temp,"db-media");Directory.CreateDirectory(media);
   var dbPath=Path.Combine(temp,"existing.db");ScoutStorage.Register(root,new ScoutDatabase(dbPath));
   try {
    var store=new WorkspaceStore(Path.Combine(root,"Workspaces"));var scout=store.CreateRun(media);scout.Status="Running";scout.SelectedFiles=[Path.Combine(media,"one.mkv")];store.Save(scout);
    var settings=new SettingsStore(root);settings.Save(new AppSettings{LocalModel="saved"});settings.SetKey("test","test-secret-only");
    var secret=ScoutStorage.ReadBytes(Path.Combine(root,"credentials.dpapi"));var report=Path.Combine(store.DirectoryFor(scout),"report.json");
    var original=new JsonObject{["files"]=new JsonArray(new JsonObject{["path"]=scout.SelectedFiles[0],["error"]="request interrupted"}),["manual_corrections"]=false};Data.Save(report,original);
    ScoutStorage.Unregister(root);ScoutStorage.Register(root,new ScoutDatabase(dbPath));
    Assert(settings.ReadKey("test")=="test-secret-only"&&ScoutStorage.ReadBytes(Path.Combine(root,"credentials.dpapi")).SequenceEqual(secret));
    Assert(settings.Load().LocalModel=="saved"&&JsonNode.DeepEquals(Data.Read(report),original));
    var recovered=store.ReadAll(true).Single();Assert(recovered.Status=="Interrupted"&&recovered.Report==report&&recovered.SelectedFiles.Count==1);
    using var sql=new SqliteConnection($"Data Source={dbPath};Pooling=False");sql.Open();using var c=sql.CreateCommand();c.CommandText="SELECT count(*) FROM file_results";Assert(Convert.ToInt32(c.ExecuteScalar())==1);
   }finally{ScoutStorage.Unregister(root);}
  });
  test("SQLite creates new Scouts guides settings caches and previews without loose files",()=>{
   var root=Path.Combine(temp,"db-only");var media=Path.Combine(temp,"db-only-media");Directory.CreateDirectory(media);ScoutStorage.Register(root,new ScoutDatabase(Path.Combine(temp,"only.db")));
   try {
    var settings=new SettingsStore(root);settings.Save(new AppSettings());settings.SetKey("test","new-secret");
    var store=new WorkspaceStore(Path.Combine(root,"Workspaces"));var scout=store.CreateRun(media);var folder=store.DirectoryFor(scout);
    Data.Save(Path.Combine(folder,"catalog.json"),new{episodes=new[]{new Episode(1,1,1,"Pilot")}});
    Data.Save(Path.Combine(folder,"reasoning-cache","cache.json"),new{assessment="uncertain"});
    var report=new JsonObject{["files"]=new JsonArray()};Data.Save(Path.Combine(folder,"report.json"),report);
    var preview=PreviewPlanner.Write(PreviewPlanner.Create(report,media,"Show"),Path.Combine(folder,"move-preview"));
    Assert(store.ReadAll().Count==1&&Data.Catalog(Path.Combine(folder,"catalog.json")).Count==1&&ScoutStorage.Exists(preview));
    Assert(!Directory.Exists(root)&&settings.ReadKey("test")=="new-secret");
    store.DeleteRun(scout);Assert(store.ReadAll().Count==0);store.RestoreRun(scout);Assert(store.ReadAll().Count==1);
   }finally{ScoutStorage.Unregister(root);}
  });
  test("SQLite ignores legacy files while explicit external imports still work",()=>{
   var root=Path.Combine(temp,"ignored-legacy");Directory.CreateDirectory(root);
   File.WriteAllText(Path.Combine(root,"settings.json"),"not valid JSON");
   var legacy=Path.Combine(root,"Workspaces","legacy","workspace.json");Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);File.WriteAllText(legacy,"{}");
   var dbPath=Path.Combine(temp,"no-import.db");ScoutStorage.Register(root,new ScoutDatabase(dbPath));
   try {
    Assert(new WorkspaceStore(Path.Combine(root,"Workspaces")).ReadAll().Count==0);
    Assert(!ScoutStorage.Exists(Path.Combine(root,"settings.json"))&&new SettingsStore(root).Load().LocalModel=="");
    var external=Path.Combine(temp,"explicit-guide.json");File.WriteAllText(external,"{\"episodes\":[]}");
    var target=Path.Combine(root,"guide.json");ScoutStorage.Copy(external,target);Assert(Data.Read(target).Array("episodes").Count==0&&!File.Exists(target));
    new SettingsStore(root).Save(new AppSettings{LocalModel="database-only"});ScoutStorage.Register(root,new ScoutDatabase(dbPath));
    Assert(new SettingsStore(root).Load().LocalModel=="database-only"&&File.ReadAllText(Path.Combine(root,"settings.json"))=="not valid JSON");
   }finally{ScoutStorage.Unregister(root);}
  });
  test("SQLite checkpoint pause preserves the current failed file without processing the next",()=>{
   var root=Path.Combine(temp,"db-pause");var media=Path.Combine(temp,"db-pause-media");Directory.CreateDirectory(media);ScoutStorage.Register(root,new ScoutDatabase(Path.Combine(temp,"pause.db")));
   try {
    var catalog=Path.Combine(root,"catalog.json");Data.Save(catalog,new{episodes=new[]{new Episode(1,1,1,"Pilot")}});
    var pause=new ScoutPause();using var client=new HttpClient();var pipeline=new MediaPipeline(new DialogueMatcher(client));
    var settings=new MatchSettings(media,catalog,root,"local","http://127.0.0.1:1/v1/chat/completions",SelectedFiles:[Path.Combine(media,"first.mkv"),Path.Combine(media,"second.mkv")],Restart:true);
    try{pipeline.RunAsync(settings,new ProgressInline(s=>{if(s.StartsWith("[1/2]"))pause.Request();}),default,pause).GetAwaiter().GetResult();throw new Exception("Expected pause");}catch(ScoutPausedException){}
    Assert(Data.Read(Path.Combine(root,"report.json")).Array("files").Count==1&&!Directory.Exists(root));
    pipeline.RunAsync(settings,new ProgressInline(_=>{}),default).GetAwaiter().GetResult();Assert(Data.Read(Path.Combine(root,"report.json")).Array("files").Count==2);
   }finally{ScoutStorage.Unregister(root);}
  });
 }
 sealed class ProgressInline(Action<string> action):IProgress<string>{public void Report(string message)=>action(message);}
}
