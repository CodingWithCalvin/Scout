using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
namespace Scout.Core;
public static class MultipartEvidence {
 public static List<Cue> Cues(JsonObject row)=>row["dialogue"].Array("cues").Select(n=>new Cue(n?["start"]?.GetValue<double>()??0,n.Text("text"))).ToList();
 public static string DialogueHash(JsonObject row)=>CollectionResolver.Hash(JsonSerializer.Serialize(Cues(row),Data.Options));
 public static bool Combined(JsonObject row) {
  try {
   if(row["reasoning"].Text("dialogue_hash")!=DialogueHash(row)||row["reasoning"].Text("method")!=DialogueMatcher.Version||row["reasoning"].Text("coverage")!="combined")return false;
   var candidates=row.Array("candidates");if(candidates.Count!=2)return false;
   var episodes=candidates.Select(c=>Data.Episode(c!["episode"]!)).ToList();
   var reasoning=row["reasoning"]!.DeepClone().AsObject();
   foreach(var m in reasoning.Array("matches"))m!["episode_title"]=episodes.Single(e=>e.Id==m.Int("episode_id")).Title;
   SelectedChecks(reasoning,episodes);
   DialogueMatcher.Validate(reasoning,DialogueMatcher.Blocks(Cues(row)),episodes,true);
   return reasoning.Array("matches").Select(m=>m.Int("episode_id")).SequenceEqual(episodes.Select(e=>e.Id));
  }catch(Exception ex) when(ex is InvalidDataException or InvalidOperationException or ArgumentException or NullReferenceException){return false;}
 }
 public static bool Partial(JsonObject row) {
  try {
   if(row["reasoning"].Text("dialogue_hash")!=DialogueHash(row)||row["reasoning"].Text("method")!=DialogueMatcher.Version||row["reasoning"].Text("coverage")!="partial"||row.Array("candidates").Count!=1)return false;
   var episode=Data.Episode(row.Array("candidates")[0]!["episode"]!);var reasoning=row["reasoning"]!.DeepClone().AsObject();
   if(reasoning.Array("matches").Count!=1||reasoning.Array("matches")[0].Int("episode_id")!=episode.Id)return false;
   reasoning.Array("matches")[0]!["episode_title"]=episode.Title;
   SelectedChecks(reasoning,[episode]);
   DialogueMatcher.Validate(reasoning,DialogueMatcher.Blocks(Cues(row)),[episode],true);return true;
  }catch(Exception ex) when(ex is InvalidDataException or InvalidOperationException or ArgumentException or NullReferenceException){return false;}
 }
 // Revalidate selected evidence against the selected guide cards. Rejected candidates
 // are not assignments and their cards are intentionally absent from this local check.
 private static void SelectedChecks(JsonObject reasoning,IReadOnlyList<Episode> episodes) {
  var titles=episodes.Select(e=>e.Title).ToHashSet(StringComparer.Ordinal);
  reasoning["candidate_checks"]=new JsonArray(reasoning.Array("candidate_checks").Where(c=>titles.Contains(c.Text("episode_title"))).Select(c=>c!.DeepClone()).ToArray());
  reasoning["alternatives"]=new JsonArray();
 }
 public static bool Overlapping(JsonObject a,JsonObject b) {
  HashSet<string> Lines(JsonObject r)=>Cues(r).Select(c=>Regex.Replace(c.Text.ToLowerInvariant(),@"[^\p{L}\p{Nd}]","")).Where(t=>t.Length>=20).ToHashSet();
  var x=Lines(a);var y=Lines(b);return x.Count==0||y.Count==0||x.Intersect(y).Count()>=Math.Min(x.Count,y.Count)*0.5;
 }
 public static void ValidateSplit(JsonObject answer,JsonObject a,JsonObject b,Episode episode) {
  if(answer.Text("assessment")!="split"||answer.Int("first_recording",-1) is not (0 or 1)||answer.Text("guide_event")!=episode.Overview||string.IsNullOrWhiteSpace(answer.Text("reason"))||string.IsNullOrWhiteSpace(answer.Text("continuity")))throw new InvalidDataException("Split evidence is incomplete.");
  if(Overlapping(a,b))throw new InvalidDataException("Recordings overlap too much to verify complementary parts.");
  foreach(var (row,key) in new[]{(a,"evidence_a"),(b,"evidence_b")}) {
   var count=DialogueMatcher.Blocks(Cues(row)).Count;var ids=answer.Array(key).Select(n=>n!.GetValue<int>()).ToList();
   if(ids.Count<2||ids.Count>4||ids.Distinct().Count()!=ids.Count||ids.Any(i=>i<0||i>=count))throw new InvalidDataException("Split requires two distinct original dialogue blocks from each recording.");
  }
 }
 public static bool SplitLink(JsonObject link,RecordingGroup a,RecordingGroup b,Episode episode) {
  try {
   if(link.Text("method")!="split-dialogue-v1"||link.Int("episode_id")!=episode.Id||a.Rows.Any(r=>!Partial(r))||b.Rows.Any(r=>!Partial(r)))return false;
   var ra=a.Rows[0];var rb=b.Rows[0];
   if(link.Text("fingerprint_a")!=CollectionResolver.FingerprintKey(ra["primary_fingerprint"])||link.Text("fingerprint_b")!=CollectionResolver.FingerprintKey(rb["primary_fingerprint"])||link.Text("dialogue_a")!=DialogueHash(ra)||link.Text("dialogue_b")!=DialogueHash(rb))return false;
   ValidateSplit(link["initial"]!.AsObject(),ra,rb,episode);ValidateSplit(link["verification"]!.AsObject(),ra,rb,episode);
   return link["initial"].Int("first_recording",-1)==link["verification"].Int("first_recording",-1);
  }catch(Exception ex) when(ex is InvalidDataException or InvalidOperationException or ArgumentException or NullReferenceException){return false;}
 }
}
