using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
namespace Scout.Core;

public static class Data
{
    public static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy=JsonNamingPolicy.SnakeCaseLower, WriteIndented=true };
    public static JsonObject Read(string path) => JsonNode.Parse(ScoutStorage.ReadText(path))?.AsObject() ?? throw new InvalidDataException("Empty JSON.");
    public static string Text(this JsonNode? node, string key, string fallback="") => node?[key]?.GetValue<string>() ?? fallback;
    public static int Int(this JsonNode? node,string key,int fallback=0) => node?[key]?.GetValue<int>() ?? fallback;
    public static bool Bool(this JsonNode? node,string key) => node?[key]?.GetValue<bool>() ?? false;
    public static JsonArray Array(this JsonNode? node,string key) => node?[key]?.AsArray() ?? [];
    public static void Save(string path,object value) {
        ScoutStorage.WriteText(path,JsonSerializer.Serialize(value,Options));
    }
    public static Episode Episode(JsonNode node) => node.Deserialize<Episode>(Options) ?? throw new InvalidDataException("Invalid episode.");
    public static List<Episode> Catalog(string path) {
        var episodes=Read(path).Array("episodes").Select(n=>Episode(n!)).ToList();
        if(episodes.Count==0 || episodes.Select(e=>e.Id).Distinct().Count()!=episodes.Count || episodes.Any(e=>string.IsNullOrWhiteSpace(e.Title)||e.Season<0||e.Number<0))
            throw new InvalidDataException("Catalog requires unique episode IDs, titles and valid season/episode numbers.");
        return episodes;
    }
}
public sealed record Episode(int Id,int Season,int Number,string Title,string Overview="")
{
    public List<GuideReference>? References {get;init;}
    [JsonIgnore] public string Code=>$"S{Season:00}E{Number:00}";
}
public sealed record GuideReference(string Overview);
public sealed record Cue(double Start,string Text);
public sealed record Block(int Index,int CueIndex,int EndCueIndex,double Start,string SourceText);
public sealed class RecordingGroup
{
    public required string Id {get;init;}
    public required List<JsonObject> Rows {get;init;}
    public List<Episode> Episodes {get;set;}=[];
    public Episode? Episode {get=>Episodes.FirstOrDefault();set=>Episodes=value==null?[]:[value];}
    public int Part {get;set;}
    public int PartCount {get;set;}
    public JsonObject? SplitEvidence {get;set;}
    public bool Combined=>Episodes.Count==2;
    public bool Split=>PartCount==2&&Episode!=null;
    public string Status {get;set;}="unassigned";
    public List<Episode> Claims {get;init;}=[];
    public List<string> CompetingRecordings {get;set;}=[];
    public bool Feature => Status=="unassigned" && Rows.All(r=>r["reasoning"].Text("assessment")=="non_episode");
    public bool Duplicate => Rows.Count>1;
    public string Code=>Combined?$"{Episodes[0].Code}-E{Episodes[1].Number:00}":Episode?.Code ?? "—";
    public string Title=>Combined?string.Join(" / ",Episodes.Select(e=>e.Title)):Split?$"{Episode!.Title} (Part {Part} of {PartCount})":Episode?.Title ?? (Feature?"Non-episode feature":"Unresolved");
    public string Category=>Combined?"Combined episodes":Split?"Split episode":Episode is not null?"Episode":Feature?"Feature":"Unresolved";
    public int Copies=>Rows.Count;
    public string Detail {get {var review=EvidenceReview.Create(this);return review.Assessment+"\n"+review.Suggestion+"\n\n"+string.Join("\n\n",review.Sections.Select(s=>s.Heading+"\n"+s.Text));}}
}
