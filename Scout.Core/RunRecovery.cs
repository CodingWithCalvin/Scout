using System.Text.Json.Nodes;
namespace Scout.Core;
public static class RunRecovery {
 public static bool SourceUnchanged(JsonObject row) {
  try {
   if(row.Bool("source_changed")||CollectionResolver.FingerprintKey(row["primary_fingerprint"])==null)return false;
   var path=row.Text("path");var fp=row["primary_fingerprint"]!;
   return File.Exists(path)&&new FileInfo(path).Length==fp["size"]!.GetValue<long>()&&PreviewPlanner.MtimeNs(path)==fp["mtime_ns"]!.GetValue<long>()&&PreviewPlanner.SampledDigest(path)==fp.Text("sampled_digest");
  }catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NullReferenceException){return false;}
 }
 public static bool CanReuse(JsonObject row,IReadOnlyList<Episode> catalog) {
  try {
   if(row.Text("error")!=""||row.Bool("source_changed")||row["reasoning"] is not JsonObject||CollectionResolver.FingerprintKey(row["primary_fingerprint"])==null)return false;
   if(row["reasoning"].Text("method")!=DialogueMatcher.Version)return false;
   ModelOutputQuality.ValidateSavedResult(row["reasoning"]!.AsObject());
   if(!SourceUnchanged(row))return false;
   foreach(var candidate in row.Array("candidates")) {
    var episode=Data.Episode(candidate!["episode"]!);
    if(!catalog.Any(e=>e.Id==episode.Id&&e.Season==episode.Season&&e.Number==episode.Number&&e.Title==episode.Title&&e.Overview==episode.Overview))return false;
   }
   var checkedRow=row.DeepClone().AsObject();checkedRow.Remove("conflict_review_pending");checkedRow.Remove("conflict_review_error");
   var group=CollectionResolver.Resolve(new JsonObject{["files"]=new JsonArray(checkedRow)}).Single();
   return group.Status is not ("invalid_model_output" or "source_or_model_error");
  }catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NullReferenceException){return false;}
 }
}
