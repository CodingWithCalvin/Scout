using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
namespace Scout.Core;

public sealed record MoveUpdate(JsonObject Report,MovePlan Plan,string Message);
public static class MoveExecutor {
 public static bool HasPending(JsonObject report)=>report.Array("moves").Any(m=>m.Text("state") is "prepared" or "attention");
 public static void RequireSettled(JsonObject report) {
  if(HasPending(report))throw new InvalidOperationException("An interrupted move needs reconciliation. Generate the move preview before analyzing or moving more files.");
 }
 private static void Paths(string root,string source,string destination) {
  if(!PreviewPlanner.Within(source,root)||!PreviewPlanner.Within(destination,root)||PreviewPlanner.Same(source,destination))
   throw new InvalidOperationException("Move must have distinct source and destination paths inside this Scout's source folder.");
  if(PreviewPlanner.HasReparseAncestor(source)||PreviewPlanner.HasReparseAncestor(destination))
   throw new InvalidOperationException("Moves through symbolic links or junctions are held.");
 }
 private static async Task<string> Hash(string path,CancellationToken ct) {
  await using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,1024*1024,FileOptions.Asynchronous|FileOptions.SequentialScan);
  return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream,ct));
 }
 private static void Commit(JsonObject report,JsonObject entry,JsonObject row) {
  string source=entry.Text("source"),destination=entry.Text("destination");
  row["original_path"]??=source;
  row["recording_id"]??=CollectionResolver.Hash(CollectionResolver.FingerprintKey(row["primary_fingerprint"])!);
  if(row["path_history"] is not JsonArray)row["path_history"]=new JsonArray();
  row.Array("path_history").Add(new JsonObject{["from"]=source,["to"]=destination,["method"]="scout-move",["move_id"]=entry.Text("id"),["verified_at"]=DateTimeOffset.UtcNow.ToString("O")});
  if(entry["item"].Text("category")=="duplicate"&&entry["item"].Text("primary_source") is {Length:>0} primary)
   row["duplicate_of"]=RecordingRelocation.MapSelection(report,[primary]).Single();
  foreach(var other in report.Array("files"))if(other.Text("duplicate_of") is {Length:>0} old&&PreviewPlanner.Same(old,source))other!["duplicate_of"]=destination;
  row["path"]=destination;row["source_mtime_ns"]=PreviewPlanner.MtimeNs(destination);
  row["primary_fingerprint"]!["mtime_ns"]=PreviewPlanner.MtimeNs(destination);
  row.Remove("move_pending");row.Remove("relocation_error");
  entry["state"]="completed";entry["completed_at"]=DateTimeOffset.UtcNow.ToString("O");entry.Remove("error");
  foreach(var scope in new[]{"issue_review","incremental_review"})
   if(report[scope]?["paths"] is JsonArray paths)for(int i=0;i<paths.Count;i++)
    if(PreviewPlanner.Same(paths[i]!.GetValue<string>(),source))paths[i]=destination;
 }
 // Recovery updates the database only. It never starts or retries a filesystem move.
 public static async Task<JsonObject> RecoverAsync(string reportPath,string root,IProgress<string> progress,CancellationToken ct,string ffmpeg="ffmpeg") {
  var report=Data.Read(reportPath);
  foreach(var entry in report.Array("moves").OfType<JsonObject>().Where(m=>m.Text("state") is "prepared" or "attention")) {
   ct.ThrowIfCancellationRequested();
   string source=entry.Text("source"),destination=entry.Text("destination");
   var row=report.Array("files").OfType<JsonObject>().SingleOrDefault(r=>PreviewPlanner.Same(r.Text("path"),source));
   try {
    Paths(root,source,destination);
    if(row==null||CollectionResolver.FingerprintKey(row["primary_fingerprint"])!=entry.Text("fingerprint"))
     throw new InvalidOperationException("Saved recording identity no longer matches the pending move.");
    if(File.Exists(source)&&!File.Exists(destination)&&!Directory.Exists(destination)) {
     row.Remove("move_pending");entry["state"]="failed";entry["error"]="Interrupted before the move completed. Select this file to retry.";
    } else if(!File.Exists(source)&&File.Exists(destination)) {
     progress.Report("Reconciling moved recording: "+Path.GetFileName(destination));
     if(entry["verification"] is JsonObject proof) {
      if(proof.Text("file_id").Length>0) {
       if(!RenameVerification.Matches(destination,proof,requireIdentity:true))
        throw new IOException("Destination identity, metadata or sampled fingerprint cannot prove this interrupted rename.");
      } else {
       if(!RenameVerification.Matches(destination,proof,requireIdentity:false))
        throw new IOException("Destination changed since the interrupted rename.");
       progress.Report("Storage did not provide a file ID. Verifying full saved fingerprint to recover the interrupted rename: "+Path.GetFileName(destination));
       var fp=row["primary_fingerprint"]!;
       if(fp.Text("algorithm")=="full-file-sha256-v1") {
        if(await Hash(destination,ct)!=fp.Text("sha256"))throw new IOException("Recovered file fingerprint does not match.");
       } else {
        var hashes=await MediaPipeline.ExecuteAsync(ffmpeg,["-nostdin","-v","error","-i",destination,"-map","0:v:0","-map","0:a:0","-c","copy","-f","streamhash","-hash","sha256","-"],ct);
        string StreamHash(string type)=>Regex.Match(hashes,@"\d+,"+type+@",SHA256=([0-9a-fA-F]{64})").Groups[1].Value.ToLowerInvariant();
        if(StreamHash("v")+StreamHash("a")!=CollectionResolver.FingerprintKey(fp))throw new IOException("Recovered primary streams do not match.");
       }
      }
     } else {
      progress.Report("Verifying legacy interrupted move with a full-file checksum: "+Path.GetFileName(destination));
      if(await Hash(destination,ct)!=entry.Text("sha256"))throw new IOException("Destination content does not match the pending move.");
     }
     Commit(report,entry,row);
    } else throw new IOException("Cannot reconcile automatically: source and destination locations are ambiguous or unavailable.");
   }catch(OperationCanceledException){throw;}
   catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or InvalidOperationException) {
    entry["state"]="attention";entry["error"]=ex.Message;if(row!=null)row["move_pending"]=true;
    progress.Report("Move held: "+ex.Message);
   }
   Data.Save(reportPath,report);
  }
  return report;
 }
 public static async Task<JsonObject> ApplyAsync(string reportPath,MovePlan preview,IReadOnlyCollection<string> selected,IReadOnlyList<Episode> catalog,IProgress<MoveUpdate> progress,CancellationToken ct,Action<JsonObject>? checkpoint=null) {
  await Task.CompletedTask;
  var report=Data.Read(reportPath);RequireSettled(report);
  if(PreviewPlanner.Within(reportPath,preview.LibraryRoot))throw new InvalidOperationException("Scout storage must be outside the video folder.");
  var wanted=selected.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
  if(wanted.Count==0)throw new InvalidOperationException("Select at least one pending move.");
  var requested=preview.Items.Where(i=>i.Action=="rename/move"&&wanted.Contains(Path.GetFullPath(i.Source))).ToList();
  if(requested.Count!=wanted.Count)throw new InvalidOperationException("Only pending, unblocked moves can be selected.");
  var fresh=PreviewPlanner.Create(report,preview.LibraryRoot,preview.Series,catalog,naming:preview.Naming,identity:preview.SeriesIdentity,verifyOnly:wanted);
  foreach(var item in requested) {
   var now=fresh.Items.SingleOrDefault(i=>i.Action=="rename/move"&&PreviewPlanner.Same(i.Source,item.Source));
   if(now==null||now.Destination==null||item.Destination==null||!PreviewPlanner.Same(now.Destination,item.Destination))
    throw new InvalidOperationException("The preview is out of date. Regenerate it before moving files.");
  }
  void Save(){if(checkpoint!=null)checkpoint(report);else Data.Save(reportPath,report);}
  var displayed=preview;
  void Notify(string message,bool refresh=true) {
   if(refresh)displayed=PreviewPlanner.Create(report,preview.LibraryRoot,preview.Series,catalog,checkSources:false,naming:preview.Naming,identity:preview.SeriesIdentity);
   progress.Report(new(report.DeepClone().AsObject(),displayed,message));
  }
  if(report["moves"] is not JsonArray)report["moves"]=new JsonArray();
  var remaining=requested.ToList();
  while(remaining.Count>0) {
   ct.ThrowIfCancellationRequested();
   // Vacate selected dependencies first. Cycles and unselected occupants stay held.
   var item=remaining.FirstOrDefault(i=>!File.Exists(i.Destination)&&!Directory.Exists(i.Destination));
   if(item==null) {
    foreach(var blocked in remaining) {
     report.Array("moves").Add(new JsonObject{["id"]=Guid.NewGuid().ToString("N"),["source"]=blocked.Source,["destination"]=blocked.Destination,["state"]="failed",["error"]="Destination is occupied (or forms a rename cycle). No file was overwritten.",["item"]=JsonSerializer.SerializeToNode(blocked,Data.Options)});
    }
    Save();Notify("Remaining moves held: destinations are occupied. No files overwritten.");break;
   }
   remaining.Remove(item);
   string source=Path.GetFullPath(item.Source),destination=Path.GetFullPath(item.Destination!);
   var row=report.Array("files").OfType<JsonObject>().Single(r=>PreviewPlanner.Same(r.Text("path"),source));
   var entry=new JsonObject{["id"]=Guid.NewGuid().ToString("N"),["source"]=source,["destination"]=destination,["state"]="failed",["item"]=JsonSerializer.SerializeToNode(item,Data.Options),["fingerprint"]=CollectionResolver.FingerprintKey(row["primary_fingerprint"]),["started_at"]=DateTimeOffset.UtcNow.ToString("O")};
   report.Array("moves").Add(entry);
   FileStream? guard=null;
   try {
    try {
     Paths(preview.LibraryRoot,source,destination);
     Notify("Checking saved fingerprint (small samples): "+Path.GetFileName(source),refresh:false);
     guard=RenameVerification.OpenGuard(source);
     if(!RunRecovery.SourceUnchanged(row))throw new IOException("Source changed since identification. Re-identify it before moving.");
     entry["verification"]=RenameVerification.Capture(source,guard.SafeFileHandle,row["primary_fingerprint"]!);
    }catch(OperationCanceledException){throw;}
    catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or InvalidOperationException) {
     entry["error"]=ex.Message;Save();Notify("Not moved: "+Path.GetFileName(source)+" — "+ex.Message);continue;
    }
    ct.ThrowIfCancellationRequested();
    entry["state"]="prepared";row["move_pending"]=true;
    Save(); // Durable intent must exist before changing any media path.
    bool moved=false;
    try {
     Paths(preview.LibraryRoot,source,destination);
     if(!RunRecovery.SourceUnchanged(row))throw new IOException("Source changed before the move.");
     if(File.Exists(destination)||Directory.Exists(destination))throw new IOException("Destination already exists.");
     Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
     Paths(preview.LibraryRoot,source,destination);
     Notify("Renaming without copying video data: "+Path.GetFileName(source),refresh:false);
     RenameVerification.Move(source,destination);moved=true;
     // Finish and save this file even when a stop was requested after the rename.
     Notify("Checking renamed file identity and samples: "+Path.GetFileName(destination),refresh:false);
     if(!RenameVerification.Matches(destination,entry["verification"]!,requireIdentity:false))
      throw new IOException("Renamed file identity or fingerprint changed; saved analysis is held pending reconciliation.");
     Commit(report,entry,row);
    }catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or InvalidOperationException) {
     entry["state"]=moved?"attention":"failed";entry["error"]=ex.Message;
     if(!moved)row.Remove("move_pending");
    }
   } finally {guard?.Dispose();}
   Save(); // A failed save stops the batch; the prepared entry on disk enables recovery.
   Notify(entry.Text("state")=="completed"?"Moved: "+Path.GetFileName(destination):"Move held: "+entry.Text("error"));
   if(entry.Text("state")=="attention")break;
  }
  return report;
 }
}
