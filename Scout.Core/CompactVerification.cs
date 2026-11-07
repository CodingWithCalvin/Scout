using System.Text.Json.Nodes;

namespace Scout.Core;

/// <summary>Maps the model's compact decisions to the saved review format; never changes its decisions.</summary>
public static class CompactVerification
{
    public static JsonObject Expand(JsonObject raw,IReadOnlyList<Block> blocks,IReadOnlyList<Episode> episodes)
    {
        if(raw.Count!=4||raw["checks"] is not JsonArray checks||checks.Count>5||raw["note"] is not JsonValue noteValue||!noteValue.TryGetValue<string>(out var note))
            throw new InvalidDataException("Invalid compact verification structure.");
        if(note.Length>0)ModelOutputQuality.ValidateText(raw["note"],"note",240);
        if(checks.Count==0&&string.IsNullOrWhiteSpace(note))throw new InvalidDataException("An abstention needs an explanation.");
        var byId=episodes.ToDictionary(e=>e.Id);var seen=new HashSet<int>();
        var result=new JsonObject{["assessment"]=raw["assessment"]?.DeepClone(),["coverage"]=raw["coverage"]?.DeepClone(),
            ["summary"]=note,["candidate_checks"]=new JsonArray(),["matches"]=new JsonArray(),["alternatives"]=new JsonArray()};
        foreach(var node in checks) {
            if(node is not JsonObject check||check.Count!=5||check["episode_id"] is not JsonValue idValue||!idValue.TryGetValue<int>(out var id)||!byId.TryGetValue(id,out var episode))
                throw new InvalidDataException("Unknown verification candidate; use only an episode ID from the supplied catalog.");
            if(!seen.Add(id))
                throw new InvalidDataException("Repeated verification candidate. Return only ONE check per episode ID, combining its main story and subplots into that check. Checks are per episode, never per event.");
            var verdict=check.Text("verdict");
            if(verdict is not ("supported" or "contradicted" or "insufficient_evidence"))throw new InvalidDataException("Invalid verification verdict.");
            ModelOutputQuality.ValidateText(check["reason"],"verification reason",240);
            var support=Indices(check["supporting_blocks"]);var contradict=Indices(check["contradicting_blocks"]);
            if(support.Intersect(contradict).Any())throw new InvalidDataException("One block cannot both support and contradict a candidate.");
            if(verdict=="contradicted"&&contradict.Count==0)throw new InvalidDataException("Contradiction needs an original dialogue block.");
            var reason=check.Text("reason");
            result.Array("candidate_checks").Add(new JsonObject{["episode_title"]=episode.Title,["supporting_blocks"]=check["supporting_blocks"]!.DeepClone(),
                ["contradicting_blocks"]=check["contradicting_blocks"]!.DeepClone(),["event_comparison"]=reason,["guide_event"]=episode.Overview,["verdict"]=verdict});
            if(verdict=="supported")result.Array("matches").Add(new JsonObject{["episode_title"]=episode.Title,["reason"]=reason});
            else if(result.Array("alternatives").Count<2)result.Array("alternatives").Add(new JsonObject{["episode_title"]=episode.Title,["reason_against"]=reason});
        }
        if(string.IsNullOrWhiteSpace(result.Text("summary")))
            result["summary"]=string.Join(" ",checks.Take(2).Select(c=>c.Text("reason")));
        // The existing validator enforces assessment/coverage agreement, evidence counts,
        // contradictory assignments, and ordered consecutive combined episodes.
        ValidateCopy();
        result["verification_decision"]=raw.DeepClone();result["summary_source"]="verification_decision";
        result["guide_event_source"]="catalog_by_episode_id";
        return result;

        List<int> Indices(JsonNode? node) {
            if(node is not JsonArray values||values.Count>4)throw new InvalidDataException("Invalid verification evidence.");
            var indices=new List<int>();
            foreach(var value in values) {
                if(value is not JsonValue scalar||!scalar.TryGetValue<int>(out var index)||index<0||index>=blocks.Count||indices.Contains(index))
                    throw new InvalidDataException("Invalid verification block index.");
                indices.Add(index);
            }
            return indices;
        }
        void ValidateCopy()=>DialogueMatcher.Validate(result.DeepClone().AsObject(),blocks,episodes,true);
    }
}
