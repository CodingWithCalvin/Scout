using System.Text.Json.Nodes;
namespace Scout.Core;

public sealed record EvidenceSection(string Heading,string Text);
public sealed record EvidenceReview(string Assessment,string Suggestion,IReadOnlyList<EvidenceSection> Sections)
{
 public static EvidenceReview Create(RecordingGroup group) {
  var assessment=group.Status switch {
   "unresolved_conflict"=>"Unresolved conflict — do not assign",
   "invalid_model_output"=>"Invalid model output — review required",
   "source_or_model_error"=>"Processing error — review required",
   _ when group.Feature=>"Non-episode content suggested",
   _ when group.Episode!=null&&group.Rows.All(r=>r.Text("matching_method")=="reasoning"||r["reasoning"]!=null)=>"Dialogue supports this match — suggestion",
   _ when group.Episode!=null=>"Filename/catalog suggestion — content unverified",
   _=>"Uncertain — no episode assigned"
  };
  var suggestion=group.Episode is {} episode?$"{episode.Code} · {episode.Title}":"No episode assignment";
  var sections=new List<EvidenceSection>();
  if(group.Status=="unresolved_conflict")sections.Add(new("Why this is held","Different recordings compete for the same episode, or duplicate copies disagree. Individual model suggestions below are not an accepted assignment."));
  if(group.Duplicate)sections.Add(new("Duplicate copies",$"{group.Copies} identical recordings share this review and one episode assignment."));
  foreach(var row in group.Rows) {
   sections.Add(new("Recording",row.Text("path")));
   if(!string.IsNullOrWhiteSpace(row.Text("error")))sections.Add(new("Processing error",row.Text("error")));
   if(row.Bool("source_changed"))sections.Add(new("Source changed","This file changed during processing. Its previous suggestion cannot be used."));
   var reasoning=row["reasoning"];
   var modelAssessment=reasoning.Text("assessment");
   if(modelAssessment!="")sections.Add(new("Model assessment",modelAssessment switch {"supported"=>"Supported by dialogue (not a guarantee of correctness)","uncertain"=>"Uncertain — insufficient evidence","non_episode"=>"Likely non-episode content",_=>modelAssessment}));
   if(reasoning.Text("summary")!="")sections.Add(new("Dialogue summary",reasoning.Text("summary")));
   var cues=row["dialogue"].Array("cues").Select(n=>new Cue(n?["start"]?.GetValue<double>()??0,n.Text("text"))).ToList();
   var blocks=DialogueMatcher.Blocks(cues);
   foreach(var candidate in row.Array("candidates")) {
    var e=candidate?["episode"] is {} node?Data.Episode(node):null;
    sections.Add(new("Suggested episode",e==null?"No episode details saved":$"{e.Code} · {e.Title}"));
    if(candidate.Text("reason")!="")sections.Add(new("Why it was suggested",candidate.Text("reason")));
    if(e?.Overview is {Length:>0} overview)sections.Add(new("Episode guide",overview));
    var quotes=candidate.Array("evidence");
    foreach(var quote in quotes) {
     var start=quote?["start"]?.GetValue<double>()??0;var text=quote.Text("text",quote.Text("source_text"));
     // Only expand a block when the original cue text exactly matches the saved evidence.
     var block=blocks.FirstOrDefault(b=>Math.Abs(b.Start-start)<0.01&&b.SourceText==text);
     var body=block==null?text:string.Join("\n",cues.Skip(block.CueIndex).Take(block.EndCueIndex-block.CueIndex+1).Select(c=>$"[{Time(c.Start)}] {c.Text}"));
     sections.Add(new($"Supporting dialogue · {Time(start)}",body));
    }
    if(quotes.Count==0)sections.Add(new("Dialogue evidence","No dialogue excerpts saved for this suggestion. Filename/catalog matching does not verify video content."));
   }
   foreach(var check in reasoning.Array("candidate_checks")) {
    sections.Add(new("Candidate check · "+check.Text("episode_title",$"Episode ID {check.Int("episode_id")}"),check.Text("event_comparison","No comparison explanation saved.")));
    foreach(var id in check.Array("contradicting_blocks")) {
     var block=blocks.FirstOrDefault(b=>b.Index==id?.GetValue<int>());
     sections.Add(new("Contradicting dialogue",block==null?$"Source block {id} — original dialogue unavailable.":$"[{Time(block.Start)}] {block.SourceText}"));
    }
   }
   foreach(var alternative in reasoning.Array("alternatives")) {
    var id=alternative.Int("episode_id");var title=reasoning.Array("candidate_checks").FirstOrDefault(c=>c.Int("episode_id")==id).Text("episode_title",$"Episode ID {id}");
    sections.Add(new("Rejected alternative · "+title,alternative.Text("reason_against",alternative.Text("reason","No rejection explanation saved."))));
   }
   if(row.Array("candidates").Count==0&&reasoning==null&&row.Text("error")=="")sections.Add(new("Evidence unavailable","This recording has no saved match rationale or dialogue evidence."));
  }
  return new(assessment,suggestion,sections);
 }
 private static string Time(double seconds)=>TimeSpan.FromSeconds(double.IsFinite(seconds)?Math.Max(0,seconds):0).ToString(@"hh\:mm\:ss");
}
