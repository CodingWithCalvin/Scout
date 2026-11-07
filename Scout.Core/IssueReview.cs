using System.Text.Json.Nodes;
namespace Scout.Core;
public static class IssueReview {
 public static HashSet<string> Select(JsonObject report,bool checkSources=true) {
  var groups=CollectionResolver.Resolve(report);
  var paths=groups.Where(g=>g.Status is not ("suggested" or "suggested_split")&&!g.Feature
    ||g.Rows.Any(r=>r.Text("error")!=""||r.Bool("source_changed")||r.Text("conflict_review_error")!=""||r.Bool("conflict_review_pending")
      ||checkSources&&!RunRecovery.SourceUnchanged(r)))
    .SelectMany(g=>g.Rows).Select(r=>Path.GetFullPath(r.Text("path"))).ToHashSet(StringComparer.OrdinalIgnoreCase);
  return Expand(report,paths);
 }
 public static HashSet<string> Expand(JsonObject report,IEnumerable<string> paths) {
  var selected=paths.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);var groups=CollectionResolver.Resolve(report);
  bool changed;
  do {
   var claims=groups.Where(g=>g.Rows.Any(r=>selected.Contains(Path.GetFullPath(r.Text("path"))))).SelectMany(g=>g.Claims).Select(e=>e.Id).ToHashSet();changed=false;
   foreach(var g in groups.Where(g=>g.Claims.Any(e=>claims.Contains(e.Id))||g.Rows.Any(r=>selected.Contains(Path.GetFullPath(r.Text("path"))))))
    foreach(var r in g.Rows)changed|=selected.Add(Path.GetFullPath(r.Text("path")));
  }while(changed);
  return selected;
 }
}
