using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
namespace Scout.Core;

public sealed record SeriesIdentity(string Provider,string Id,string Name,string Year="",string Overview="")
{
    public string Key=>Provider+":"+Id;
    public string Label=>$"{Name} {(Year.Length>0?"("+Year+") ":"")}· {Provider}-{Id}";
    public string Url=>Provider switch {
        "tvdb"=>$"https://thetvdb.com/dereferrer/series/{Id}",
        "tmdb"=>$"https://www.themoviedb.org/tv/{Id}",
        "tvmaze"=>$"https://www.tvmaze.com/shows/{Id}",_=>""};
    public void Validate() {
        if(Provider is not ("tvdb" or "tmdb" or "tvmaze")||!long.TryParse(Id,out var id)||id<=0||string.IsNullOrWhiteSpace(Name))
            throw new InvalidDataException("A catalog-verified series provider, positive ID and name are required.");
    }
}

public interface IMetadataCatalog
{
    string Provider {get;}
    Task<List<SeriesIdentity>> SearchAsync(string title,CancellationToken ct);
    Task<SeriesIdentity?> GetAsync(string id,CancellationToken ct);
    Task<List<Episode>> EpisodesAsync(string id,CancellationToken ct);
}

// Fixed provider hosts: API credentials never go to model endpoints or arbitrary URLs.
public sealed class MetadataCatalog(HttpClient http,string provider,string? credential=null,string? pin=null):IMetadataCatalog
{
    public string Provider=>provider;
    private string? token;
    private string Root=>provider switch {"tvdb"=>"https://api4.thetvdb.com/v4/","tmdb"=>"https://api.themoviedb.org/3/","tvmaze"=>"https://api.tvmaze.com/",_=>throw new ArgumentException("Unknown metadata provider.")};
    private async Task<JsonNode?> Request(string path,CancellationToken ct)
    {
        if(provider!="tvmaze"&&string.IsNullOrWhiteSpace(credential))throw new InvalidOperationException($"Configure {provider.ToUpperInvariant()} credentials in Settings > Show discovery first.");
        if(provider=="tvdb"&&token==null) {
            var login=new JsonObject{["apikey"]=credential};if(!string.IsNullOrWhiteSpace(pin))login["pin"]=pin;
            using var response=await http.PostAsJsonAsync(Root+"login",login,ct);
            Check(response);var body=JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            token=body?["data"].Text("token");if(string.IsNullOrWhiteSpace(token))throw new InvalidDataException("TVDB login returned no token.");
        }
        using var request=new HttpRequestMessage(HttpMethod.Get,Root+path);
        if(provider!="tvmaze")request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",provider=="tvdb"?token:credential);
        using var result=await http.SendAsync(request,ct);
        if(result.StatusCode==HttpStatusCode.NotFound)return null;
        Check(result);return JsonNode.Parse(await result.Content.ReadAsStringAsync(ct));
    }
    private static void Check(HttpResponseMessage response) {
        if(!response.IsSuccessStatusCode)throw new HttpRequestException($"Metadata service returned HTTP {(int)response.StatusCode}. Check credentials, provider availability and request limits.",null,response.StatusCode);
    }
    private SeriesIdentity? Read(JsonNode? node,bool search=false) {
        if(node==null)return null;
        if(provider=="tvdb"&&search&&node.Text("type")!="series")return null;
        string id=node[provider=="tvdb"&&search?"tvdb_id":"id"]?.ToString()??"";
        var year=node[provider=="tmdb"?"first_air_date":provider=="tvmaze"?"premiered":search?"year":"firstAired"]?.ToString()??"";
        if(year.Length>4)year=year[..4];
        var overview=WebUtility.HtmlDecode(Regex.Replace(node.Text(provider=="tvmaze"?"summary":"overview"),"<[^>]*>"," "));
        var identity=new SeriesIdentity(provider,id,node.Text("name"),year,overview);
        try{identity.Validate();return identity;}catch(InvalidDataException){return null;}
    }
    public async Task<List<SeriesIdentity>> SearchAsync(string title,CancellationToken ct) {
        if(string.IsNullOrWhiteSpace(title)||title.Length>200)throw new ArgumentException("Search titles must contain 1–200 characters.");
        var query=Uri.EscapeDataString(title);
        var body=await Request(provider switch {"tvdb"=>"search?type=series&limit=8&query="+query,"tmdb"=>"search/tv?include_adult=false&query="+query,_=>"search/shows?q="+query},ct);
        var rows=provider=="tvmaze"?body as JsonArray:body?[provider=="tvdb"?"data":"results"] as JsonArray;
        return (rows??[]).Select(n=>Read(provider=="tvmaze"?n?["show"]:n,true)).OfType<SeriesIdentity>().DistinctBy(s=>s.Key).Take(8).ToList();
    }
    public async Task<SeriesIdentity?> GetAsync(string id,CancellationToken ct) {
        if(!long.TryParse(id,out var number)||number<=0)throw new ArgumentException("Invalid series ID.");
        var body=await Request((provider switch {"tvdb"=>"series/","tmdb"=>"tv/",_=>"shows/"})+number,ct);
        var result=Read(provider=="tvdb"?body?["data"]:body);
        if(result!=null&&result.Id!=number.ToString())throw new InvalidDataException("Catalog returned a different series ID.");
        return result;
    }
    public async Task<List<Episode>> EpisodesAsync(string id,CancellationToken ct) {
        if(!long.TryParse(id,out var number)||number<=0)throw new ArgumentException("Invalid series ID.");
        var result=new List<Episode>();
        void Add(JsonArray? rows) {
            if(rows==null)throw new InvalidDataException("Provider returned no episode list.");
            foreach(var row in rows) {
                var season=row?[provider=="tvdb"?"seasonNumber":provider=="tmdb"?"season_number":"season"];
                var episode=row?[provider=="tmdb"?"episode_number":"number"];
                if(season==null||episode==null)continue;
                var name=row.Text("name");if(string.IsNullOrWhiteSpace(name))continue;
                result.Add(new(row.Int("id"),season.GetValue<int>(),episode.GetValue<int>(),name,
                    WebUtility.HtmlDecode(Regex.Replace(row.Text(provider=="tvmaze"?"summary":"overview"),"<[^>]*>"," "))));
            }
        }
        if(provider=="tvmaze")Add(await Request($"shows/{number}/episodes?specials=1",ct) as JsonArray);
        else if(provider=="tmdb") {
            var series=await Request($"tv/{number}",ct)??throw new InvalidDataException("Series not found.");
            foreach(var season in series.Array("seasons").Select(s=>s.Int("season_number")).Distinct()) {
                var body=await Request($"tv/{number}/season/{season}",ct);Add(body?["episodes"] as JsonArray);
            }
        }else {
            for(int page=0;page<1000;page++) {
                var body=await Request($"series/{number}/episodes/default?page={page}",ct)??throw new InvalidDataException("Episode guide not found.");
                Add(body["data"]?["episodes"] as JsonArray);
                if(body["links"]?["next"]==null||string.IsNullOrWhiteSpace(body["links"]!["next"]!.ToString()))break;
                if(page==999)throw new InvalidDataException("Episode guide exceeded the page limit.");
            }
        }
        if(result.Count==0||result.Any(e=>e.Id<=0||e.Season<0||e.Number<0)||result.Select(e=>e.Id).Distinct().Count()!=result.Count)
            throw new InvalidDataException("Provider returned an empty or invalid episode guide.");
        return result;
    }
}
