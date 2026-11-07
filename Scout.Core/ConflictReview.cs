using System.Text.Json;
using System.Text.Json.Nodes;
namespace Scout.Core;
public static class ConflictReview {
 public static async Task RunAsync(JsonObject report,IReadOnlyList<Episode> catalog,DialogueMatcher matcher,string model,string endpoint,Action checkpoint,IProgress<string> progress,CancellationToken ct,ScoutPause? pause=null,string? episodeCode=null,bool checkSources=true,IReadOnlySet<string>? allowedPaths=null) {
  var groups=CollectionResolver.Resolve(report);
  var paths=groups.Where(g=>g.Status=="unresolved_conflict"&&(episodeCode==null||g.Claims.Any(e=>e.Code==episodeCode))).SelectMany(g=>g.Rows).Select(r=>Path.GetFullPath(r.Text("path"))).ToHashSet(StringComparer.OrdinalIgnoreCase);
  if(allowedPaths!=null){allowedPaths=IssueReview.Expand(report,allowedPaths);paths.IntersectWith(allowedPaths);}
  var rows=report.Array("files").OfType<JsonObject>().Where(r=>paths.Contains(Path.GetFullPath(r.Text("path")))||r.Bool("conflict_review_pending")&&(allowedPaths==null||allowedPaths.Contains(Path.GetFullPath(r.Text("path"))))).ToList();
  if(rows.Count==0)return;
  foreach(var row in rows)row["conflict_review_pending"]=true;checkpoint();
  foreach(var row in rows) {
   pause?.Checkpoint();ct.ThrowIfCancellationRequested();
   var signature=CollectionResolver.Hash(JsonSerializer.Serialize(new{version="full-guide-conflict-v1",revision=DialogueMatcher.RequestRevision,cues=MultipartEvidence.Cues(row),catalog,model,scope=matcher.CacheScope,endpoint}));
   progress.Report("Full-guide conflict review: "+Path.GetFileName(row.Text("path")));
   try {
    var proof=row.DeepClone().AsObject();proof.Remove("conflict_review_pending");proof.Remove("conflict_review_error");
    if(checkSources&&!RunRecovery.CanReuse(proof,catalog))throw new InvalidDataException("Source or saved evidence cannot be verified unchanged. Re-identify this recording before conflict review.");
   if(row["conflict_review"].Text("signature")==signature&&row.Text("conflict_review_error")=="")continue;
    var result=await matcher.ReevaluateConflictAsync(MultipartEvidence.Cues(row),catalog,model,endpoint,ct);
    if(checkSources&&!RunRecovery.CanReuse(proof,catalog))throw new InvalidDataException("Source changed during conflict review. No new assignment accepted.");
    row["conflict_review"]=new JsonObject{["signature"]=signature,["previous_reasoning"]=row["reasoning"]?.DeepClone(),["previous_candidates"]=row["candidates"]?.DeepClone()};
    row["reasoning"]=result;row["candidates"]=new JsonArray(result.Array("matches").Select(m=>(JsonNode)new JsonObject{
     ["episode"]=JsonSerializer.SerializeToNode(catalog.Single(e=>e.Id==m.Int("episode_id")),Data.Options),["method"]="reasoning",["reason"]=m!["reason"]?.DeepClone(),
     ["evidence"]=new JsonArray(m.Array("evidence").Select(b=>(JsonNode)new JsonObject{["start"]=b!["start"]?.DeepClone(),["text"]=b["source_text"]?.DeepClone()}).ToArray())}).ToArray());
    row.Remove("conflict_review_error");
   }catch(OperationCanceledException){throw;}catch(UsageLimitException){throw;}
   catch(Exception ex){row["conflict_review_error"]=ex.Message;progress.Report("Conflict remains held: "+ex.Message);}
   checkpoint();pause?.Checkpoint();
  }
  foreach(var row in rows)row.Remove("conflict_review_pending");checkpoint();
 }
}
