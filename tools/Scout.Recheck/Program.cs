using System.Text.Json;
using System.Text.Json.Nodes;
using Scout.Core;
if(args.Length==3&&args[0]=="--apply-conflicts") {
 var originalPath=Path.Combine(args[1],"report.json");var reviewed=Data.Read(Path.Combine(args[2],"report.json"));var savedBeforeReview=Data.Read(originalPath);
 var catalog=Data.Catalog(Path.Combine(args[1],"catalog.json"));
 if(reviewed.Array("files").Count!=savedBeforeReview.Array("files").Count)throw new InvalidDataException("Saved Scout changed during review.");
 foreach(var row in reviewed.Array("files").OfType<JsonObject>()) {
  var old=savedBeforeReview.Array("files").Single(r=>r.Text("path")==row.Text("path"));
  if(row["conflict_review"] is {} audit) {
   if(!JsonNode.DeepEquals(old?["reasoning"],audit["previous_reasoning"])||!JsonNode.DeepEquals(old?["candidates"],audit["previous_candidates"]))throw new InvalidDataException("Saved suggestions changed during review.");
   if(!RunRecovery.CanReuse(row,catalog))throw new InvalidDataException("Reviewed source changed or evidence failed validation.");
  }else if(!JsonNode.DeepEquals(row,old))throw new InvalidDataException("Unreviewed row changed.");
 }
 // Use the normal storage abstraction; no episode identities are supplied here.
 ScoutStorage.WriteText(originalPath,reviewed.ToJsonString(Data.Options));
 Console.WriteLine("Automated review saved; unchanged rows and source fingerprints verified.");return;
}
if(args.Length==5&&args[0]=="--conflicts") {
 var source=Path.GetFullPath(args[1]);var settingsDirectory=Path.GetFullPath(args[2]);var output=Path.GetFullPath(args[3]);
 var databasePath=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Scout","scout.db");
 if(File.Exists(databasePath))ScoutStorage.Register(settingsDirectory,new ScoutDatabase(databasePath));
 var snapshot=Data.Read(Path.Combine(source,"report.json"));var saved=JsonSerializer.Deserialize<RunWorkspace>(ScoutStorage.ReadText(Path.Combine(source,"workspace.json")))!;
 var prefs=new SettingsStore(settingsDirectory).Load();if(prefs.Provider is not ("local" or "managed"))throw new InvalidOperationException("Local model required for this diagnostic.");
 var catalog=Data.Catalog(saved.Catalog);var target=Path.Combine(output,"report.json");Data.Save(target,snapshot);
 var log=new Progress<string>(Console.WriteLine);await using var runtime=prefs.Provider=="managed"?await LocalRuntime.StartAsync(prefs,log,default):null;
 using var http=new HttpClient{Timeout=TimeSpan.FromSeconds(prefs.RequestTimeoutSeconds)};
 var matcher=new DialogueMatcher(http,new InferenceOptions{Provider=prefs.Provider,MaxOutputTokens=prefs.MaxOutputTokens,MaxRequests=prefs.MaxRequestsPerRun});
 await ConflictReview.RunAsync(snapshot,catalog,matcher,prefs.Model,prefs.Endpoint,()=>Data.Save(target,snapshot),log,default,episodeCode:args[4]);
 Data.Save(Path.Combine(output,"usage.json"),matcher.Usage);
 foreach(var g in CollectionResolver.Resolve(snapshot).Where(g=>g.Rows.Any(r=>r["conflict_review"]!=null||r.Text("conflict_review_error")!="")))Console.WriteLine(string.Join("; ",g.Rows.Select(r=>Path.GetFileName(r.Text("path"))))+" | "+g.Status+" | "+g.Code+" "+g.Title);
 return;
}
if(args.Length is <2 or >3)throw new ArgumentException("Usage: Scout.Recheck <run directory> <settings directory> [output directory]");
var dir=Path.GetFullPath(args[0]);var original=Path.Combine(dir,"report.json");
var database=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Scout","scout.db");
if(File.Exists(database))ScoutStorage.Register(args[1],new ScoutDatabase(database));
var report=Data.Read(original);var workspace=JsonSerializer.Deserialize<RunWorkspace>(ScoutStorage.ReadText(Path.Combine(dir,"workspace.json")))!;
var destination=args.Length==3?Path.GetFullPath(args[2]):dir;
var settings=new SettingsStore(args[1]).Load();
if(settings.Provider is not ("local" or "managed"))throw new InvalidOperationException("This diagnostic command supports local inference only.");
var paths=CollectionResolver.Resolve(report).Where(g=>g.Episode==null).SelectMany(g=>g.Rows).Where(r=>r.Text("error")!="").Select(r=>r.Text("path")).ToList();
Console.WriteLine($"Revalidated saved evidence; {paths.Count} model errors need fresh inference.");
var progress=new Progress<string>(Console.WriteLine);
if(paths.Count>0){
 await using var runtime=settings.Provider=="managed"?await LocalRuntime.StartAsync(settings,progress,CancellationToken.None):null;
 using var http=new HttpClient{Timeout=TimeSpan.FromSeconds(settings.RequestTimeoutSeconds)};
 var matcher=new DialogueMatcher(http,new InferenceOptions{Provider=settings.Provider,MaxOutputTokens=settings.MaxOutputTokens,MaxRequests=settings.MaxRequestsPerRun});
 var output=Path.Combine(destination,"automated-recheck");
 var path=await new MediaPipeline(matcher).RunAsync(new(workspace.Library,workspace.Catalog,output,settings.Model,settings.Endpoint,settings.Ffmpeg,settings.Ffprobe,original,paths,workspace.SeriesIdentity),progress,CancellationToken.None);
 var fresh=Data.Read(path).Array("files").ToDictionary(r=>r.Text("path"),StringComparer.OrdinalIgnoreCase);
 report["files"]=new JsonArray(report.Array("files").Select(r=>fresh.GetValueOrDefault(r.Text("path"),r)!.DeepClone()).ToArray());
 Data.Save(Path.Combine(output,"usage.json"),matcher.Usage);
}
report["manual_corrections"]=false;
report["recheck_revision"]=DialogueMatcher.RequestRevision;
Data.Save(Path.Combine(destination,"report.json"),report);
var plan=PreviewPlanner.Create(report,workspace.Library,workspace.SeriesIdentity!.Name,Data.Catalog(workspace.Catalog),naming:settings.Naming,identity:workspace.SeriesIdentity);
PreviewPlanner.Write(plan,Path.Combine(destination,"move-preview"));
foreach(var item in plan.Items)Console.WriteLine($"{Path.GetFileName(item.Source)} | {item.Action} | {item.Change} | {item.Reason}");
