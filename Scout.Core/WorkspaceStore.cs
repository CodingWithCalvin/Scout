using System.Text.Json;
namespace Scout.Core;
public sealed class RunWorkspace {
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
