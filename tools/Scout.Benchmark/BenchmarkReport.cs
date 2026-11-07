using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Scout.Core;
static class BenchmarkReport {
 public static void Render(string root,Func<string,string> store,IReadOnlyList<Arm> arms,JsonArray inputs,IReadOnlyList<Episode> catalog) {
  string E(string s)=>WebUtility.HtmlEncode(s);
  var html=new StringBuilder("<!doctype html><meta charset=utf-8><title>Scout model comparison</title><style>body{font:15px Segoe UI;background:#111923;color:#eaf0f7;margin:32px}table{border-collapse:collapse;width:100%}td,th{border-bottom:1px solid #40516a;text-align:left;padding:12px;vertical-align:top}small{color:#aebed0}</style><h1>Scout model comparison</h1><p>13 saved dialogue cases; current matcher; no manual corrections. Identical dialogue reuses its result within each model.</p><p><small>Scores measure agreement with inherited evaluation labels, not general accuracy. Filenames and expected answers are never sent to the matcher.</small></p><table><tr><th>Model</th><th>Reference agreement</th><th>Errors</th><th>Uncertain</th><th>Conflicts</th><th>Inference time</th></tr>");
  foreach(var arm in arms) {
   var p=store(arm.Id+"/summary.json");
   if(ScoutStorage.Exists(p)){var s=Data.Read(p);html.Append($"<tr><td>{E(arm.Id)}</td><td>{s.Int("reference_agreement")}/13</td><td>{s.Int("errors")}</td><td>{s.Int("abstentions")}</td><td>{s.Int("conflict_groups")}</td><td>{TimeSpan.FromSeconds(s["seconds"]!.GetValue<double>()):g}</td></tr>");}
   else html.Append("<tr><td>"+E(arm.Id)+"</td><td colspan=5>Pending / in progress</td></tr>");
  }
  html.Append("</table><h2>Episode suggestions</h2><table><tr><th>Case</th>");foreach(var arm in arms)html.Append("<th>"+E(arm.Id)+"</th>");html.Append("</tr>");
  foreach(var input in inputs) {
   html.Append("<tr><td>"+E(input.Text("case_id"))+"</td>");
   foreach(var arm in arms) {
    var p=store(arm.Id+"/"+input.Text("case_id")+".json");var value="Pending";
    if(ScoutStorage.Exists(p)){var r=Data.Read(p);value=r.Text("error");if(value=="")value=string.Join(" + ",r["result"].Array("matches").Select(m=>catalog.Single(e=>e.Id==m.Int("episode_id"))).Select(e=>e.Code+" · "+e.Title));if(value=="")value=r["result"].Text("assessment");}
    html.Append("<td>"+E(value)+"</td>");
   }html.Append("</tr>");
  }html.Append("</table>");File.WriteAllText(Path.Combine(root,"results.html"),html.ToString());
 }
}
