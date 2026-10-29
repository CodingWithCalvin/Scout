using System.Text.Json;
namespace Scout.Core;
public sealed record ScoutSetup {
 public string Mode {get;set;}="known";
 public string MetadataProvider {get;set;}="tvdb";
 public string Query {get;set;}="";
 public string? CandidateKey {get;set;}
}
public sealed class RunWorkspace {
 public ScoutSetup? Setup {get;set;}
 public ScoutSetup RestoreSetup()=>Setup??=new ScoutSetup {
  Mode=Events.Any(e=>e.Contains("Show discovery inspected",StringComparison.Ordinal))?"discover":"known",
  MetadataProvider=SeriesIdentity?.Provider??"tvdb",Query=SeriesIdentity?.Name??Name
 };

 public SeriesIdentity? SeriesIdentity {get;set;}
 public List<string> DiscoveryFiles {get;set;}=[];
 public List<string> SelectedFiles {get;set;}=[];
 public bool Deleted {get;set;}
 public string Id {get;set;}=Guid.NewGuid().ToString("N");
 public string Name {get;set;}="New workspace";
 public string Status {get;set;}="Draft";
 public DateTimeOffset Updated {get;set;}=DateTimeOffset.Now;
 public string Library {get;set;}="";
 public string Catalog {get;set;}="";
 public string Dialogue {get;set;}="";
 public string Report {get;set;}="";
 public string Preview {get;set;}="";
 public string Model {get;set;}="";
 public string Provider {get;set;}="";
 public List<string> Events {get;set;}=[];
 public string Label=>$"{Updated:MMM d, HH:mm}  ·  {Status}  ·  {Id[..6]}";
}
public sealed class WorkspaceStore(string root) {
 public RunWorkspace CreateRun(string library) {
  library=Path.GetFullPath(library);
  if(!Directory.Exists(library))throw new DirectoryNotFoundException("The selected source folder is unavailable. Reconnect the drive or choose another folder.");
  var identity=LoadIdentity(library);
  var run=new RunWorkspace{Library=library,Name=identity?.Name??Path.GetFileName(Path.TrimEndingDirectorySeparator(library)).Split(" {")[0],SeriesIdentity=identity};
  Save(run);return run;
 }
 public SeriesIdentity? LoadIdentity(string library) {
  var path=NamingPath(library)+".series.json";
  if(!File.Exists(path))return null;
  var identity=JsonSerializer.Deserialize<SeriesIdentity>(File.ReadAllText(path));identity?.Validate();return identity;
 }
 public void SaveIdentity(string library,SeriesIdentity identity) {
  identity.Validate();var path=NamingPath(library)+".series.json";Directory.CreateDirectory(Path.GetDirectoryName(path)!);
  File.WriteAllText(path+".tmp",JsonSerializer.Serialize(identity));File.Move(path+".tmp",path,true);
 }
 public string NamingPath(string library) => Path.Combine(root,"scout-settings",CollectionResolver.Hash(Path.GetFullPath(library).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant())+".json");
 public string DirectoryFor(RunWorkspace run) {
  if(run.Id.Length!=32||!run.Id.All(Uri.IsHexDigit))throw new InvalidDataException("Invalid workspace ID.");
  return Path.Combine(root,run.Id);
 }
 public void DeleteRun(RunWorkspace run) {
  if(run.Status=="Running")throw new InvalidOperationException("Stop this run before deleting it.");
  run.Deleted=true;Save(run);
 }
 public void RestoreRun(RunWorkspace run){run.Deleted=false;Save(run);}
 public string SaveCatalog(RunWorkspace run,string source) {
  Data.Catalog(source);
  var directory=DirectoryFor(run);Directory.CreateDirectory(directory);
  var target=Path.Combine(directory,"catalog.json");
  if(!string.Equals(Path.GetFullPath(source),Path.GetFullPath(target),StringComparison.OrdinalIgnoreCase)) {
   File.Copy(source,target+".tmp",true);File.Move(target+".tmp",target,true);
  }
  run.Catalog=target;Save(run);return target;
 }
 public void Save(RunWorkspace run) {
  var directory=DirectoryFor(run);Directory.CreateDirectory(directory);run.Updated=DateTimeOffset.Now;
  var path=Path.Combine(directory,"workspace.json");
  File.WriteAllText(path+".tmp",JsonSerializer.Serialize(run));File.Move(path+".tmp",path,true);
 }
 public List<RunWorkspace> ReadAll(bool recoverInterrupted=false) {
  if(!Directory.Exists(root))return [];
  var result=new List<RunWorkspace>();
  foreach(var path in Directory.EnumerateFiles(root,"workspace.json",SearchOption.AllDirectories)) {
   try {var run=JsonSerializer.Deserialize<RunWorkspace>(File.ReadAllText(path));
    if(run==null||Path.GetFullPath(Path.GetDirectoryName(path)!)!=Path.GetFullPath(DirectoryFor(run)))continue;
    if(recoverInterrupted&&run.Status=="Running"){run.Status="Interrupted";run.Events.Add("Processing was interrupted. Start a new workspace to retry.");Save(run);}
    if(!run.Deleted)result.Add(run);
   } catch(JsonException){} catch(IOException){} catch(InvalidDataException){}
  }
  return result.OrderByDescending(x=>x.Updated).ToList();
 }
}
