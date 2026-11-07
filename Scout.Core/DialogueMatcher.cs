using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
namespace Scout.Core;
public sealed partial class DialogueMatcher(HttpClient client,InferenceOptions? options=null)
{
    private readonly ModelTransport transport=new(client,options??new());
    public string CacheScope=>transport.CacheScope;
    public UsageSnapshot Usage=>transport.Usage;
    public Uri ValidateEndpoint(string endpoint)=>transport.ValidateEndpoint(endpoint);
    public const string RequestRevision="compact-verifier-candidates-v6";
    public const string Version="verified-dialogue-v7-multipart1";
    private static string Asset(string name) {
        using var stream=typeof(DialogueMatcher).Assembly.GetManifestResourceStream("Scout.Core.Assets."+name)??throw new InvalidOperationException("Missing matcher asset.");
        using var reader=new StreamReader(stream);return reader.ReadToEnd();
    }
    public static List<Block> Blocks(IReadOnlyList<Cue> cues) {
        var blocks=new List<Block>();int start=0;
        while(start<cues.Count) {
            int end=start;while(end+1<cues.Count&&end-start<7&&cues[end+1].Start-cues[start].Start<=30)end++;
            blocks.Add(new(blocks.Count,start,end,cues[start].Start,string.Join(" ",cues.Skip(start).Take(end-start+1).Select(c=>c.Text))));start=end+1;
        }
        return blocks;
    }
    public static List<Episode> Rank(IReadOnlyList<Cue> cues,IReadOnlyList<Episode> episodes) {
        var stop=Asset("stopwords.txt").Split(' ').ToHashSet();
        List<string> Tokens(string text)=>Regex.Matches(new string(text.Normalize(NormalizationForm.FormKD).Where(c=>c<128).ToArray()).ToLowerInvariant(),"[a-z]{3,}")
            .Select(m=>m.Value).Where(w=>!stop.Contains(w)).ToList();
        var docs=episodes.Select(e=>Tokens(e.Title+" "+string.Join(" ",new[]{e.Overview}.Concat(e.References?.Select(r=>r.Overview)??[]).Distinct(StringComparer.OrdinalIgnoreCase)))).ToList();
        var df=docs.SelectMany(d=>d.Distinct()).GroupBy(w=>w).ToDictionary(g=>g.Key,g=>g.Count());
        var idf=df.ToDictionary(p=>p.Key,p=>Math.Log((docs.Count+1.0)/(p.Value+1))+1);
        Dictionary<string,double> Vector(IEnumerable<string> words)=>words.Where(idf.ContainsKey).GroupBy(w=>w).ToDictionary(g=>g.Key,g=>(1+Math.Log(g.Count()))*idf[g.Key]);
        var query=Vector(Tokens(string.Join(" ",cues.Where(c=>!Regex.IsMatch(c.Text.TrimStart(),"^[*♪♫]")).Select(c=>c.Text))));
        double qnorm=Math.Sqrt(query.Values.Sum(v=>v*v));
        return docs.Select((d,index)=>{
            var v=Vector(d);double norm=Math.Sqrt(v.Values.Sum(x=>x*x))*qnorm;
            double score=norm==0?0:query.Sum(p=>v.GetValueOrDefault(p.Key)*p.Value)/norm;
            return (Episode:episodes[index],Score:score);
        }).Where(p=>p.Score>0).OrderByDescending(p=>p.Score).Take(5).Select(p=>p.Episode).ToList();
    }
    public static Uri LocalEndpoint(string endpoint) {
        if(!Uri.TryCreate(endpoint,UriKind.Absolute,out var uri)||uri.Scheme is not ("http" or "https")||uri.Host is not ("localhost" or "127.0.0.1" or "[::1]" or "::1")||!string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("Use a local loopback model endpoint.");
        return uri;
    }
    public async Task<JsonObject> IdentifyAsync(IReadOnlyList<Cue> cues,IReadOnlyList<Episode> episodes,string model,string endpoint,CancellationToken ct=default) {
        var initial=await DirectAsync(cues,episodes,model,endpoint,false,ct);
        var ids=initial.Array("candidate_checks").Concat(initial.Array("matches")).Concat(initial.Array("alternatives")).Select(n=>n.Int("episode_id")).ToHashSet();
        foreach(var e in Rank(cues,episodes))ids.Add(e.Id);
        var shortlist=episodes.Where(e=>ids.Contains(e.Id)).ToList();
        if(shortlist.Count==0)throw new InvalidDataException("No verification candidates.");
        var result=await DirectAsync(cues,shortlist,model,endpoint,true,ct);
        if(initial.Text("assessment")==result.Text("assessment")&&initial.Text("coverage")==result.Text("coverage")&&
           initial.Array("matches").Select(m=>m.Int("episode_id")).SequenceEqual(result.Array("matches").Select(m=>m.Int("episode_id")))) {
            result["summary"]=initial["summary"]!.DeepClone();result["summary_source"]="initial_pass";
        }
        result["verification"]=new JsonObject {["candidate_ids"]=JsonSerializer.SerializeToNode(shortlist.Select(e=>e.Id)),["initial_result"]=initial};
        result["method"]=Version;return result;
    }
    public async Task<JsonObject> ReevaluateConflictAsync(IReadOnlyList<Cue> cues,IReadOnlyList<Episode> episodes,string model,string endpoint,CancellationToken ct=default) {
        const string instruction="This is an independent collection-conflict review. Search the ENTIRE supplied catalog afresh. Other recordings having claimed an episode is NOT evidence for or against this recording. Do not rely on previous suggestions or recalled episode knowledge. A shared class, performance, location, or character is not a defining-event match. The actual task, relationship, central action and consequence must agree with the guide. Never call a candidate supported while admitting its defining events are absent or different. If no guide fits, abstain. Cite blocks that actually substantiate each defining event.";
        var first=await DirectAsync(cues,episodes,model,endpoint,true,ct,instruction);
        var second=await DirectAsync(cues,episodes,model,endpoint,true,ct,instruction+" Independently audit the original source against the full guide.");
        string Decision(JsonObject r)=>r.Text("assessment")+":"+r.Text("coverage")+":"+string.Join(",",r.Array("matches").Select(m=>m.Int("episode_id")));
        if(Decision(first)!=Decision(second))throw new InvalidDataException("Independent full-guide conflict reviews disagree. No assignment accepted.");
        second["conflict_verification"]=new JsonObject{["method"]="full-guide-conflict-v1",["catalog_count"]=episodes.Count,["independent_result"]=first};
        return second;
    }
    public static string CompactSchema(JsonNode schema) {
        var compact=schema.DeepClone();
        void Visit(JsonNode? node) {
            if(node is JsonObject obj){obj.Remove("enum");foreach(var child in obj.ToList())Visit(child.Value);}
            else if(node is JsonArray list)foreach(var child in list)Visit(child);
        }
        Visit(compact);return compact.ToJsonString();
    }
    public async Task<JsonObject> DirectAsync(IReadOnlyList<Cue> cues,IReadOnlyList<Episode> episodes,string model,string endpoint,bool verify,CancellationToken ct=default,string reviewContext="") {
        ValidateEndpoint(endpoint);
        if(cues.Count==0)throw new InvalidDataException("No dialogue to identify.");
        if(episodes.Select(e=>e.Title).Distinct().Count()!=episodes.Count)throw new InvalidDataException("Catalog titles must be unique.");
        var blocks=Blocks(cues);
        var prefix=verify?"verify":"initial";
        var schema=JsonNode.Parse(Asset(prefix+"-schema.json"))!;
        void Fill(JsonNode? node) {
            if(node is JsonObject obj) {
                if(verify&&obj.ContainsKey("enum")&&obj["enum"] is JsonArray ids&&ids.Any(n=>n?.ToString()=="EPISODE_ID"))obj["enum"]=JsonSerializer.SerializeToNode(episodes.Select(e=>e.Id));
                else if(obj["enum"] is JsonArray enums&&enums.Any(n=>n?.ToString()=="PLACEHOLDER"))obj["enum"]=JsonSerializer.SerializeToNode(episodes.Select(e=>e.Title));
                else if(obj["enum"] is JsonArray overviews&&overviews.Any(n=>n?.ToString()=="OVERVIEW"))obj["enum"]=JsonSerializer.SerializeToNode(episodes.Select(e=>e.Overview).Distinct());
                if(obj.ContainsKey("maximum"))obj["maximum"]=blocks.Count-1;
                foreach(var child in obj.ToList())Fill(child.Value);
            }else if(node is JsonArray array)foreach(var child in array)Fill(child);
        }
        Fill(schema);
        if(verify)schema["properties"]!["checks"]!["maxItems"]=Math.Min(5,episodes.Count);
        var catalog=JsonSerializer.SerializeToNode(episodes.Select(e=>new {id=e.Id,title=e.Title,overview=e.Overview,season=e.Season,episode=e.Number}));
        var dialogue=JsonSerializer.SerializeToNode(blocks.Select(b=>new {index=b.Index,text=b.SourceText}));
        // Keep the instructions and entire original dialogue byte-identical across passes.
        // Stage-specific catalog/schema/task content follows the shared prefix. No generated answer is sent back.
        var source=new JsonObject {["dialogue_blocks"]=dialogue,["catalog"]=catalog};
        var prompt=Asset("matching-rules.txt");
        string TaskText(bool omitSchema)=>Asset(prefix+"-prompt.txt")+"\n"+reviewContext+
            (omitSchema?"\nReturn JSON matching the supplied structured-output schema.":"\nRequired JSON schema:\n"+CompactSchema(schema));
        source["task"]=TaskText(false);
        bool compactRetry=false;
        JsonObject result=null!;var attempts=new JsonArray();
        for(int attempt=0;attempt<2;attempt++) {
            JsonObject raw;
            try {raw=await transport.CompleteAsync(model,endpoint,prompt,source,schema,ct);}
            catch(ModelContextException) when(!compactRetry) {
                compactRetry=true;
                // The response-format schema still enforces every field and allowed value.
                // Keep the complete original dialogue, catalog, and evidence instructions.
                source["task"]=TaskText(true);
                raw=await transport.CompleteAsync(model,endpoint,prompt,source,schema,ct);
            }
            try {
                result=verify?CompactVerification.Expand(raw,blocks,episodes):raw.DeepClone().AsObject();
                Validate(result,blocks,episodes,verify);break;
            }
            catch(InvalidDataException ex) {
                attempts.Add(new JsonObject {["error"]=ex.Message,["response"]=raw});
                if(attempt==1){ex.Data["model_attempts"]=attempts.ToJsonString();throw;}
                source["task"]=source.Text("task")+"\nThe previous response failed validation: "+ex.Message+" Re-evaluate the original dialogue and supplied catalog. Use season and episode numbers to check adjacency, and order matches by playback evidence. Do not force a match to satisfy validation; abstain if unsupported. Return a fresh complete JSON response.";
            }
        }
        if(compactRetry)result["request_adjustments"]="Context overflow: retried without the redundant schema explanation. Complete dialogue, catalog and structured-output validation were preserved.";
        if(attempts.Count>0)result["validation_retries"]=attempts;
        result["dialogue_hash"]=CollectionResolver.Hash(JsonSerializer.Serialize(cues,Data.Options));
        result["model"]=model;result["method"]=verify?Version:"direct-dialogue-v7-multipart1";
        return result;
    }
    public static void Validate(JsonObject result,IReadOnlyList<Block> blocks,IReadOnlyList<Episode> episodes,bool verify) {
        var assessment=result.Text("assessment");
        if(assessment is not ("supported" or "uncertain" or "non_episode")||result["summary"] is not JsonValue||result["matches"] is not JsonArray||result["candidate_checks"] is not JsonArray||result["alternatives"] is not JsonArray)
            throw new InvalidDataException("Invalid result structure.");
        ModelOutputQuality.ValidateExplanations(result);
        var matches=result.Array("matches");if(matches.Count>2||(assessment=="supported")!=(matches.Count>0))throw new InvalidDataException("At most two episodes in one assignment; assessment must agree.");
        var coverage=result.Text("coverage");
        if(coverage is not ("complete" or "partial" or "combined" or "unknown")||matches.Count==2&&coverage!="combined"||coverage=="combined"&&matches.Count!=2||matches.Count==0&&coverage!="unknown")throw new InvalidDataException("Invalid recording coverage.");
        if(matches.Count==1&&coverage is not ("complete" or "partial"))throw new InvalidDataException("Supported recording needs complete or partial coverage.");
        var matchedTitles=matches.Select(m=>m.Text("episode_title")).ToList();
        if(matchedTitles.Distinct().Count()!=matchedTitles.Count)throw new InvalidDataException("Repeated episode in assignment.");
        var byTitle=episodes.ToDictionary(e=>e.Title);var checks=new Dictionary<string,JsonObject>();
        List<int> Indices(JsonNode? n) {
            if(n is not JsonArray arr)throw new InvalidDataException("Missing source blocks.");
            var values=new List<int>();
            foreach(var item in arr) {
                if(item is not JsonValue scalar||!scalar.TryGetValue<int>(out var index))throw new InvalidDataException("Source block indices must be integers.");
                values.Add(index);
            }
            if(values.Count>4||values.Distinct().Count()!=values.Count||values.Any(i=>i<0||i>=blocks.Count))throw new InvalidDataException("Invalid source block.");
            return values;
        }
        foreach(var node in result.Array("candidate_checks")) {
            var check=node!.AsObject();var title=check.Text("episode_title");
            if(!byTitle.ContainsKey(title)||!checks.TryAdd(title,check))throw new InvalidDataException("Unknown or repeated candidate.");
            Indices(check["supporting_blocks"]);Indices(check["contradicting_blocks"]);
            if(verify&&check.Text("guide_event")!=byTitle[title].Overview)throw new InvalidDataException("Guide text does not belong to candidate.");
            check["episode_id"]=byTitle[title].Id;
        }
        foreach(var node in matches) {
            var match=node!.AsObject();var title=match.Text("episode_title");
            if(!checks.TryGetValue(title,out var check)||string.IsNullOrWhiteSpace(match.Text("reason")))throw new InvalidDataException("Missing source comparison.");
            var cited=Indices(check["supporting_blocks"]);
            if(cited.Count<2||Indices(check["contradicting_blocks"]).Count!=0)throw new InvalidDataException("Match needs two supporting blocks and no contradiction.");
            match["episode_id"]=byTitle[title].Id;match.Remove("episode_title");
            match["evidence"]=JsonSerializer.SerializeToNode(cited.Select(i=>blocks[i]),Data.Options);
        }
        if(matches.Count==2) {
            var first=episodes.Single(e=>e.Id==matches[0].Int("episode_id"));var second=episodes.Single(e=>e.Id==matches[1].Int("episode_id"));
            if(first.Season!=second.Season||second.Number!=first.Number+1)throw new InvalidDataException("Combined episodes must be consecutive in one season and playback order.");
            if(Indices(checks[first.Title]["supporting_blocks"]).Max()>=Indices(checks[second.Title]["supporting_blocks"]).Min())throw new InvalidDataException("Combined episodes need distinct ordered dialogue evidence.");
        }
        var selected=matches.Select(n=>n.Int("episode_id")).ToHashSet();
        foreach(var node in result.Array("alternatives")) {
            var alt=node!.AsObject();var title=alt.Text("episode_title");
            if(!byTitle.ContainsKey(title)||selected.Contains(byTitle[title].Id))throw new InvalidDataException("Invalid rejected alternative.");
            alt["episode_id"]=byTitle[title].Id;alt.Remove("episode_title");
        }
    }
}
