using System.Text.Json;
using System.Text.Json.Nodes;
namespace Scout.Core;
public static class PreviewCache {
 public static string Signature(JsonObject report,string root,string series,IReadOnlyList<Episode> catalog,NamingOptions naming,SeriesIdentity? identity)=>CollectionResolver.Hash(JsonSerializer.Serialize(new{version=3,report,root=Path.GetFullPath(root),series,catalog,naming,identity},Data.Options));
 private static string State(MovePlan plan) {
  var paths=plan.Items.SelectMany(i=>new[]{i.Source,i.Destination}).Where(p=>p!=null).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);
  var states=paths.Select(p=> {
   if(File.Exists(p)){var file=new FileInfo(p);return p+"|file|"+file.Length+"|"+file.LastWriteTimeUtc.Ticks+"|"+file.Attributes;}
   return p+(Directory.Exists(p)?"|directory|"+File.GetAttributes(p):"|missing");
  });
  return CollectionResolver.Hash(string.Join("\n",states));
 }
 public static void Save(string folder,string signature,MovePlan plan)=>Data.Save(Path.Combine(folder,"cache.json"),new{signature,state=State(plan),plan_hash=CollectionResolver.Hash(ScoutStorage.ReadText(Path.Combine(folder,"plan.json")))});
 public static MovePlan? Read(string folder,string signature) {
  try {
   var cache=Path.Combine(folder,"cache.json");var saved=Path.Combine(folder,"plan.json");
   if(!ScoutStorage.Exists(cache)||!ScoutStorage.Exists(saved)||!ScoutStorage.Exists(Path.Combine(folder,"preview.html")))return null;
   var stamp=Data.Read(cache);if(stamp.Text("signature")!=signature)return null;
   var text=ScoutStorage.ReadText(saved);if(stamp.Text("plan_hash")!=CollectionResolver.Hash(text))return null;
   var plan=JsonSerializer.Deserialize<MovePlan>(text,Data.Options);
   return plan!=null&&stamp.Text("state")==State(plan)?plan:null;
  }catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException){return null;}
 }
}
