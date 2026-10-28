using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
namespace Scout.Core;
public sealed class DialogueMatcher(HttpClient client,InferenceOptions? options=null)
{
    private readonly ModelTransport transport=new(client,options??new());
    public string CacheScope=>transport.CacheScope;
    public UsageSnapshot Usage=>transport.Usage;
    public Uri ValidateEndpoint(string endpoint)=>transport.ValidateEndpoint(endpoint);
    public const string Version="verified-dialogue-v6-dotnet1";
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
        result["verification"]=new JsonObject {["candidate_ids"]=JsonSerializer.SerializeToNode(shortlist.Select(e=>e.Id)),["initial_result"]=initial};
        result["method"]=Version;return result;
    }
    public async Task<JsonObject> DirectAsync(IReadOnlyList<Cue> cues,IReadOnlyList<Episode> episodes,string model,string endpoint,bool verify,CancellationToken ct=default) {
        ValidateEndpoint(endpoint);
        if(cues.Count==0)throw new InvalidDataException("No dialogue to identify.");
        if(episodes.Select(e=>e.Title).Distinct().Count()!=episodes.Count)throw new InvalidDataException("Catalog titles must be unique.");
        var blocks=Blocks(cues);
        var prefix=verify?"verify":"initial";
        var schema=JsonNode.Parse(Asset(prefix+"-schema.json"))!;
        void Fill(JsonNode? node) {
            if(node is JsonObject obj) {
                if(obj["enum"] is JsonArray enums&&enums.Any(n=>n?.ToString()=="PLACEHOLDER"))obj["enum"]=JsonSerializer.SerializeToNode(episodes.Select(e=>e.Title));
                else if(obj["enum"] is JsonArray overviews&&overviews.Any(n=>n?.ToString()=="OVERVIEW"))obj["enum"]=JsonSerializer.SerializeToNode(episodes.Select(e=>e.Overview).Distinct());
                if(obj.ContainsKey("maximum"))obj["maximum"]=blocks.Count-1;
                foreach(var child in obj.ToList())Fill(child.Value);
            }else if(node is JsonArray array)foreach(var child in array)Fill(child);
        }
        Fill(schema);
        var catalog=JsonSerializer.SerializeToNode(episodes.Select(e=>new {title=e.Title,overview=e.Overview}));
        var dialogue=JsonSerializer.SerializeToNode(blocks.Select(b=>new {index=b.Index,text=b.SourceText}));
        var source=verify?new JsonObject {["dialogue_blocks"]=dialogue,["catalog"]=catalog}:new JsonObject {["catalog"]=catalog,["dialogue_blocks"]=dialogue};
        var result=await transport.CompleteAsync(model,endpoint,Asset(prefix+"-prompt.txt")+"\nRequired JSON schema:\n"+schema.ToJsonString(),source,schema,ct);
        Validate(result,blocks,episodes,verify);
        result["model"]=model;result["method"]=verify?Version:"direct-dialogue-v5.2-dotnet1";
        return result;
    }
    public static void Validate(JsonObject result,IReadOnlyList<Block> blocks,IReadOnlyList<Episode> episodes,bool verify) {
        var assessment=result.Text("assessment");
        if(assessment is not ("supported" or "uncertain" or "non_episode")||result["summary"] is not JsonValue||result["matches"] is not JsonArray||result["candidate_checks"] is not JsonArray||result["alternatives"] is not JsonArray)
            throw new InvalidDataException("Invalid result structure.");
        var matches=result.Array("matches");if(matches.Count>1||(assessment=="supported")!=(matches.Count==1))throw new InvalidDataException("One episode maximum; assessment must agree.");
        var byTitle=episodes.ToDictionary(e=>e.Title);var checks=new Dictionary<string,JsonObject>();
        List<int> Indices(JsonNode? n) {
            if(n is not JsonArray arr)throw new InvalidDataException("Missing source blocks.");
            var values=arr.Select(v=>v!.GetValue<int>()).ToList();
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
        var selected=matches.Select(n=>n.Int("episode_id")).ToHashSet();
        foreach(var node in result.Array("alternatives")) {
            var alt=node!.AsObject();var title=alt.Text("episode_title");
            if(!byTitle.ContainsKey(title)||selected.Contains(byTitle[title].Id))throw new InvalidDataException("Invalid rejected alternative.");
            alt["episode_id"]=byTitle[title].Id;alt.Remove("episode_title");
        }
    }
}
