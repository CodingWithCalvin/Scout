using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
namespace Scout.Core;
public static class RecordingRelocation {
 public static List<string> MapSelection(JsonObject report,IEnumerable<string> selected) {
  var paths=report.Array("files").SelectMany(r=>r.Array("path_history").Select(h=>(Old:Path.GetFullPath(h.Text("from")),Current:r.Text("path"))))
   .GroupBy(x=>x.Old,StringComparer.OrdinalIgnoreCase).Where(g=>g.Select(x=>x.Current).Distinct(StringComparer.OrdinalIgnoreCase).Count()==1).ToDictionary(g=>g.Key,g=>g.First().Current,StringComparer.OrdinalIgnoreCase);
  var current=report.Array("files").Select(r=>Path.GetFullPath(r.Text("path"))).ToHashSet(StringComparer.OrdinalIgnoreCase);
  return selected.Select(p=>current.Contains(Path.GetFullPath(p))?Path.GetFullPath(p):paths.GetValueOrDefault(Path.GetFullPath(p),p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
 }
 public static async Task<Dictionary<string,string>> ReconnectAsync(JsonObject report,string root,string ffmpeg,IProgress<string> progress,CancellationToken ct,Func<string,CancellationToken,Task<string?>>? fingerprint=null) {
  MoveExecutor.RequireSettled(report);
  var rows=report.Array("files").OfType<JsonObject>().ToList();
  foreach(var row in rows.Where(r=>File.Exists(r.Text("path"))&&r.Text("relocation_error")!=""))row.Remove("relocation_error");
  var missing=rows.Where(r=>!File.Exists(r.Text("path"))).ToList();
  var mapping=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
  if(missing.Count==0)return mapping;
  if(!Directory.Exists(root))throw new DirectoryNotFoundException("Source folder unavailable. Use Locate moved files to select its new location.");
  var occupied=rows.Where(r=>File.Exists(r.Text("path"))).Select(r=>Path.GetFullPath(r.Text("path"))).ToHashSet(StringComparer.OrdinalIgnoreCase);
  var files=VideoSelection.Enumerate(root).Where(p=>!occupied.Contains(Path.GetFullPath(p))).ToList();
  var verifiedTimes=new Dictionary<string,long>(StringComparer.OrdinalIgnoreCase);
  var verified=new Dictionary<string,string?>(StringComparer.OrdinalIgnoreCase);
  var choices=new Dictionary<JsonObject,List<string>>();
  foreach(var row in missing) {
   ct.ThrowIfCancellationRequested();var matches=new List<string>();choices[row]=matches;
   var fp=row["primary_fingerprint"];var key=CollectionResolver.FingerprintKey(fp);
   if(key==null)continue;
   foreach(var file in files) {
    ct.ThrowIfCancellationRequested();
    try {
     var size=new FileInfo(file).Length;var mtime=PreviewPlanner.MtimeNs(file);
     if(size!=fp!["size"]!.GetValue<long>())continue;
     var sample=PreviewPlanner.SampledDigest(file);if(sample!=fp.Text("sampled_digest"))continue;
     if(!verified.TryGetValue(file,out var actual)) {
      progress.Report("Verifying moved recording: "+Path.GetFileName(file));
      if(fingerprint!=null)actual=await fingerprint(file,ct);
      else if(fp.Text("algorithm")=="full-file-sha256-v1") {await using var stream=File.OpenRead(file);actual="file:"+Convert.ToHexStringLower(await SHA256.HashDataAsync(stream,ct));}
      else {
       var hashes=await MediaPipeline.ExecuteAsync(ffmpeg,["-nostdin","-v","error","-i",file,"-map","0:v:0","-map","0:a:0","-c","copy","-f","streamhash","-hash","sha256","-"],ct);
       string Hash(string type)=>Regex.Match(hashes,@"\d+,"+type+@",SHA256=([0-9a-fA-F]{64})").Groups[1].Value.ToLowerInvariant();
       actual=Hash("v")+Hash("a");if(actual.Length!=128)actual=null;
      }
      if(new FileInfo(file).Length!=size||PreviewPlanner.MtimeNs(file)!=mtime||PreviewPlanner.SampledDigest(file)!=sample)actual=null;
      verified[file]=actual;verifiedTimes[file]=mtime;
     }
     if(actual==key)matches.Add(file);
    }catch(OperationCanceledException){throw;}catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception){progress.Report("Could not verify "+Path.GetFileName(file)+"; recording remains held.");}
   }
  }
  foreach(var row in missing) {
   var matches=choices[row];
   if(matches.Count!=1||choices.Count(c=>c.Value.Contains(matches.FirstOrDefault()??"",StringComparer.OrdinalIgnoreCase))!=1) {
    row["relocation_error"]=matches.Count==0?"Original path is missing. No uniquely verified replacement was found in the source folder.":"Multiple copies or saved recordings share this fingerprint. Automatic relinking is ambiguous; no path changed.";continue;
   }
   var target=matches[0];var original=Path.GetFullPath(row.Text("path"));
   // Final lightweight check before changing the stored locator.
   if(PreviewPlanner.MtimeNs(target)!=verifiedTimes[target]||new FileInfo(target).Length!=row["primary_fingerprint"]!["size"]!.GetValue<long>()||PreviewPlanner.SampledDigest(target)!=row["primary_fingerprint"].Text("sampled_digest")) {row["relocation_error"]="Replacement changed during verification.";continue;}
   row["original_path"]??=original;row["recording_id"]??=CollectionResolver.Hash(CollectionResolver.FingerprintKey(row["primary_fingerprint"])!);
   if(row["path_history"] is not JsonArray)row["path_history"]=new JsonArray();
   row.Array("path_history").Add(new JsonObject{["from"]=original,["to"]=target,["verified_at"]=DateTimeOffset.UtcNow.ToString("O"),["method"]="verified-fingerprint"});
   row["path"]=Path.GetFullPath(target);row["primary_fingerprint"]!["mtime_ns"]=PreviewPlanner.MtimeNs(target);row["source_mtime_ns"]=PreviewPlanner.MtimeNs(target);row.Remove("relocation_error");mapping[original]=Path.GetFullPath(target);
   progress.Report("Reconnected saved analysis: "+Path.GetFileName(original)+" → "+Path.GetFileName(target));
  }
  foreach(var scope in new[]{"issue_review","incremental_review"})
  if(report[scope]?["paths"] is JsonArray pending)for(int i=0;i<pending.Count;i++)if(mapping.TryGetValue(Path.GetFullPath(pending[i]!.GetValue<string>()),out var target))pending[i]=target;
  return mapping;
 }
}
