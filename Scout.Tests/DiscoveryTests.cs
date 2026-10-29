using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Scout.Core;

static class DiscoveryTests
{
    public static void PublicSmoke(Action<string,Action> test) {
        test("Live TVmaze search and episode guide",()=>{
            using var http=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(30)};
            var client=new MetadataCatalog(http,"tvmaze");var shows=client.SearchAsync("Victorious",default).GetAwaiter().GetResult();
            var show=shows.Single(s=>s.Name=="Victorious"&&s.Year=="2010");
            var episodes=client.EpisodesAsync(show.Id,default).GetAwaiter().GetResult();Check(episodes.Any(e=>e.Season==1&&e.Number==1));
            Console.WriteLine($"Live catalog: {show.Label}, {episodes.Count} numbered episodes.");
        });
    }
    private static void Check(bool condition){if(!condition)throw new Exception("Assertion failed");}
    private static void Reject(Action action){try{action();}catch(ArgumentException){return;}catch(InvalidDataException){return;}catch(InvalidOperationException){return;}throw new Exception("Expected rejection");}
    private static HttpResponseMessage Response(string json)=>new(HttpStatusCode.OK){Content=new StringContent(json)};
    public static void Run(Action<string,Action> test,string root)
    {
        test("Run selection persists across reload and rejects missing or outside files",()=>{
            var library=Path.Combine(root,"selection-library");Directory.CreateDirectory(library);
            var file=Path.Combine(library,"episode.mkv");File.WriteAllText(file,"fixture");
            var store=new WorkspaceStore(Path.Combine(root,"selection-history"));var run=store.CreateRun(library);
            run.SelectedFiles=VideoSelection.ValidateSaved(library,[file]);store.Save(run);
            var restored=store.ReadAll().Single();Check(restored.SelectedFiles.SequenceEqual([file]));
            Reject(()=>VideoSelection.ValidateSaved(library,[file,file]));
            Reject(()=>VideoSelection.ValidateSaved(library,[Path.Combine(root,"outside.mkv")]));
            File.Delete(file);
            try {VideoSelection.ValidateSaved(library,restored.SelectedFiles);throw new Exception("Missing source must not silently expand scope");}catch(FileNotFoundException){}
        });
        test("Identification prepares missing provider guide and reuses it without another request",()=>{
            var path=Path.Combine(root,"auto-guide","catalog.json");var identity=new SeriesIdentity("tvdb","152721","Victorious");
            var provider=new PreparationCatalog();
            var episodes=EpisodeGuidePreparation.EnsureAsync(path,identity,provider,default).GetAwaiter().GetResult();
            Check(episodes.Count==1&&provider.Calls==1&&SavedGuide.Identity(path,"")==identity);
            EpisodeGuidePreparation.EnsureAsync(path,identity,provider,default).GetAwaiter().GetResult();Check(provider.Calls==1);
            Reject(()=>EpisodeGuidePreparation.EnsureAsync(path,new("tvdb","12","Other"),provider,default).GetAwaiter().GetResult());
        });
        test("Identification refuses missing identity and invalid provider guides",()=>{
            var path=Path.Combine(root,"invalid-auto-guide.json");
            Reject(()=>EpisodeGuidePreparation.EnsureAsync(path,null,null,default).GetAwaiter().GetResult());
            Reject(()=>EpisodeGuidePreparation.EnsureAsync(path,new("tvdb","12","Show"),new DiscoveryCatalog(),default).GetAwaiter().GetResult());
            Check(!File.Exists(path));
        });
        test("Saved guides retain identity and reject another show",()=>{
            var path=Path.Combine(root,"saved-guide.json");
            var identity=new SeriesIdentity("tvdb","152721","Victorious");
            Data.Save(path,new{series_identity=identity,episodes=new[]{new Episode(1,1,1,"Pilot")}});
            Check(SavedGuide.Identity(path,"Ignored")==identity);
            Check(SavedGuide.Episodes(path,identity).Single().Title=="Pilot");
            Reject(()=>SavedGuide.Episodes(path,new("tvdb","123","Other")));
            Reject(()=>SavedGuide.Episodes(path,new("tmdb","152721","Other")));
        });
        test("Legacy saved guides require TVDB provenance and an ID",()=>{
            var path=Path.Combine(root,"legacy-guide.json");
            var episodes=new[]{new Episode(1,1,1,"Pilot")};
            Data.Save(path,new{series_id=152721,source="https://thetvdb.com/series/victorious/allseasons/official",episodes});
            Check(SavedGuide.Identity(path,"Victorious").Key=="tvdb:152721");
            Data.Save(path,new{series_id=152721,source="https://thetvdb.com.example.org/series/victorious",episodes});
            Reject(()=>SavedGuide.Identity(path,"Victorious"));
            Data.Save(path,new{episodes});Reject(()=>SavedGuide.Identity(path,"Victorious"));
        });
        test("Provider tokens require a real selected identity",()=>{
            var naming=new NamingOptions{FileTemplate="{series} [{provider}-{id}] - {code} - {title}",SeasonTemplate="{provider}-{id} Season {season:00}"};naming.Validate();
            var series=new SeriesIdentity("tvdb","152721","Victorious");var episode=new Episode(1,1,1,"Pilot");
            Check(naming.FileName(series.Name,episode,series)=="Victorious [tvdb-152721] - S01E01 - Pilot");
            Check(naming.Folder(series.Name,episode,series)=="tvdb-152721 Season 01");Reject(()=>naming.FileName(series.Name,episode));
            naming.FileTemplate="{series} {{{provider}-{id}}} - {code} - {title}";naming.Validate();
            Check(naming.FileName(series.Name,episode,series)=="Victorious {tvdb-152721} - S01E01 - Pilot");
            Check(NamingOptions.Inspect("Victorious {tvdb-152721} - S01E01 - Pilot.mkv",series.Name,episode,series).Correct);
            Check(!NamingOptions.Inspect("Victorious {tmdb-152721} - S01E01 - Pilot.mkv",series.Name,episode,series).Correct);
            Reject(()=>new NamingOptions{FileTemplate="{{title}} {code}"}.Validate());
            Reject(()=>naming.FileName("Bad",episode,new("tvdb","../escape","Bad")));
            var store=new WorkspaceStore(Path.Combine(root,"identity"));store.SaveIdentity(root,series);Check(store.LoadIdentity(root)==series);
        });
        test("TVDB uses API TVDB ID, never search object ID",()=>{
            var calls=new List<string>();var handler=new TestHandler(r=>{
                calls.Add(r.RequestUri!.Host+r.RequestUri.PathAndQuery);
                if(r.RequestUri.AbsolutePath.EndsWith("login"))return Response("""{"data":{"token":"test-token"}}""");
                Check(r.Headers.Authorization?.Parameter=="test-token");
                if(r.RequestUri.AbsolutePath.EndsWith("search"))return Response("""{"data":[{"id":"series-152721","tvdb_id":"152721","type":"series","name":"Victorious","year":"2010"},{"tvdb_id":"55","type":"movie","name":"Wrong"}]}""");
                return Response("""{"data":{"id":152721,"name":"Victorious","firstAired":"2010-03-27"}}""");
            });
            using var http=new HttpClient(handler);var client=new MetadataCatalog(http,"tvdb","key","pin");
            Check(client.SearchAsync("Victorious",default).Result.Single().Id=="152721");Check(client.GetAsync("152721",default).Result!.Year=="2010");
            Check(calls.Count==3&&calls.All(x=>x.StartsWith("api4.thetvdb.com/")));Check(handler.Requests[0].Text("pin")=="pin");
        });
        test("TVDB episode guide follows pages without trusting next URL",()=>{
            var handler=new TestHandler(r=>r.RequestUri!.AbsolutePath.EndsWith("login")?Response("""{"data":{"token":"token"}}"""):
                r.RequestUri.Query.Contains("page=0")?Response("""{"data":{"episodes":[{"id":1,"seasonNumber":1,"number":1,"name":"One"}]},"links":{"next":"https://untrusted.invalid/"}}"""):
                Response("""{"data":{"episodes":[{"id":2,"seasonNumber":1,"number":2,"name":"Two"}]},"links":{"next":null}}"""));
            using var http=new HttpClient(handler);Check(new MetadataCatalog(http,"tvdb","key").EpisodesAsync("123",default).Result.Count==2);
        });
        test("TMDB TV search and season guide use bearer credentials",()=>{
            var handler=new TestHandler(r=>{
                Check(r.RequestUri!.Host=="api.themoviedb.org"&&r.Headers.Authorization?.Parameter=="token");
                if(r.RequestUri.AbsolutePath.Contains("search"))return Response("""{"results":[{"id":31251,"name":"Victorious","first_air_date":"2010-03-27"}]}""");
                if(r.RequestUri.AbsolutePath.Contains("season"))return Response("""{"episodes":[{"id":10,"season_number":1,"episode_number":1,"name":"Pilot"}]}""");
                return Response("""{"id":31251,"name":"Victorious","seasons":[{"season_number":1}]}""");
            });using var http=new HttpClient(handler);var client=new MetadataCatalog(http,"tmdb","token");
            Check(client.SearchAsync("Victorious",default).Result.Single().Id=="31251");Check(client.EpisodesAsync("31251",default).Result.Single().Code=="S01E01");
        });
        test("TVmaze lookup needs no key and skips unnumbered episodes",()=>{
            var handler=new TestHandler(r=>{
                Check(r.Headers.Authorization==null);
                return r.RequestUri!.AbsolutePath.Contains("episodes")?Response("""[{"id":3,"season":1,"number":1,"name":"Pilot","summary":"<p>Hello</p>"},{"id":4,"season":1,"number":null,"name":"Unknown"}]"""):
                    Response("""[{"show":{"id":100,"name":"Show","premiered":"2001-01-01","summary":null}}]""");
            });using var http=new HttpClient(handler);var client=new MetadataCatalog(http,"tvmaze");
            Check(client.SearchAsync("Show",default).Result.Single().Id=="100");Check(client.EpisodesAsync("100",default).Result.Single().Overview.Trim()=="Hello");
        });
        test("Metadata errors and redirects do not expose credentials",()=>{
            using var http=new HttpClient(new TestHandler(_=>new(HttpStatusCode.Unauthorized){Content=new StringContent("secret-key")}));
            try{new MetadataCatalog(http,"tmdb","secret-key").SearchAsync("Show",default).GetAwaiter().GetResult();throw new Exception("Expected failure");}
            catch(HttpRequestException ex){Check(!ex.Message.Contains("secret-key"));}
        });
        test("Filename catalog mode does not guess multi-episode recordings",()=>{
            var catalog=new List<Episode>{new(1,2,1,"One")};
            Check(FilenameCatalogMatcher.Match("Show.S02E01.mkv",catalog)?.Id==1);
            Check(FilenameCatalogMatcher.Match("Show.2x01.mkv",catalog)?.Id==1);
            Check(FilenameCatalogMatcher.Match("Show.S02E01E02.mkv",catalog)==null);
            Check(FilenameCatalogMatcher.Match("Show.S02E01-S02E02.mkv",catalog)==null);
            Check(FilenameCatalogMatcher.Match("title00.mkv",catalog)==null);
            catalog.Add(new(2,2,1,"Ambiguous"));Check(FilenameCatalogMatcher.Match("Show.S02E01.mkv",catalog)==null);
        });
        test("No-AI matching groups exact duplicates and produces safe preview",()=>{
            var media=Path.Combine(root,"no-ai-media");Directory.CreateDirectory(media);
            var a=Path.Combine(media,"Show.S01E01.mkv");var b=Path.Combine(media,"Show-S01E01.mkv");File.WriteAllText(a,"same bytes");File.Copy(a,b);
            var output=Path.Combine(root,"no-ai-output");var series=new SeriesIdentity("tvdb","12","Show");var episodes=new List<Episode>{new(1,1,1,"Pilot")};
            var path=FilenameCatalogMatcher.RunAsync([a,b],media,output,series,episodes,new Progress<string>(),default).GetAwaiter().GetResult();
            var report=Data.Read(path);Check(CollectionResolver.Resolve(report).Single().Copies==2);
            Check(report.Array("files").All(r=>r.Text("matching_method")=="filename_catalog"));
            var plan=PreviewPlanner.Create(report,media,"Show",episodes,identity:series);Check(plan.Items.Count==2&&plan.Items.All(i=>i.Action!="hold"));
            Check(File.ReadAllText(a)=="same bytes"&&File.ReadAllText(b)=="same bytes");
        });
        test("Scopes restrict files to one episode season show or selection",()=>{
            var files=new[]{Path.Combine(root,"Season 01","a.mkv"),Path.Combine(root,"Season 01","b.mkv"),Path.Combine(root,"Season 02","c.mkv")};
            Check(VideoSelection.Scope(files,"episode",files[1]).SequenceEqual([files[1]]));
            Check(VideoSelection.Scope(files,"season",season:Path.GetDirectoryName(files[0])).Count==2);
            Check(VideoSelection.Scope(files,"show").Count==3);
            Check(VideoSelection.Scope(files,"selected",selected:[files[0],files[2],"outside"]).Count==2);
            var nested=Path.Combine(root,"Season 01","Disc 1","episode.mkv");Check(VideoSelection.SeasonFolder(root,nested)==Path.Combine(root,"Season 01"));
            Check(VideoSelection.Scope(files.Append(nested).ToList(),"season",season:Path.Combine(root,"Season 01")).Count==3);
        });
        test("Discovery extracts provider IDs and discards generic rip names",()=>{
            var clue=ShowDiscovery.ParseClue("Victorious {tvdb-152721}","folder");Check(clue.Title=="Victorious"&&clue.Id=="152721"&&clue.Provider=="tvdb");
            Check(ShowDiscovery.ParseClue("Show.Name.S01E01.Title.mkv","file",true).Title=="Show Name");
            Check(ShowDiscovery.ParseClue("title00.mkv","file",true).Title=="");
        });
        test("Discovery ID lookup with agreeing clues makes no model calls",()=>{
            var folder=Path.Combine(root,"Show {tvdb-12}");Directory.CreateDirectory(folder);var file=Path.Combine(folder,"Show.S01E01.mkv");File.WriteAllText(file,"");
            var discovery=new ShowDiscovery(new DiscoveryCatalog(),(_,_,_)=>Task.FromResult("{}"));
            var result=discovery.DiscoverAsync(folder,"probe","ffmpeg",(_,_,_)=>throw new Exception("Unexpected model call"),new Progress<string>(),default,[file]).Result;
            Check(result.Candidates.Single().Series.Id=="12"&&result.Dialogue.Count==0);
        });
        test("Discovery dialogue uses hypotheses then independent catalog verification",()=>{
            var folder=Path.Combine(root,"Unknown");Directory.CreateDirectory(folder);var file=Path.Combine(folder,"title00.mkv");File.WriteAllText(file,"");
            var discovery=new ShowDiscovery(new DiscoveryCatalog(),(exe,_,_)=>Task.FromResult(exe=="probe"?"""{"streams":[{"index":1,"codec_type":"subtitle","codec_name":"subrip"}]}""":"1\n00:00:00,000 --> 00:00:02,000\nHello character\n\n2\n00:01:00,000 --> 00:01:02,000\nDistinctive location\n"));
            int calls=0;
            Task<JsonObject> Infer(List<DiscoveryDialogue> blocks,List<ShowCandidate> candidates,CancellationToken ct){calls++;Check(blocks.Count==2);return Task.FromResult(JsonNode.Parse(candidates.Count==0?"""{"shows":[{"name":"Show","evidence_indices":[0,1]}]}""":"""{"assessment":"supported","key":"tvdb:12","reason":"Distinctive evidence","evidence_indices":[0,1]}""")!.AsObject());}
            var result=discovery.DiscoverAsync(folder,"probe","ffmpeg",Infer,new Progress<string>(),default,[file]).Result;
            Check(calls==2&&result.Candidates.Single().DialogueSupported&&result.Candidates[0].Evidence.Any(e=>e.Contains("Distinctive location")));
        });
        test("Discovery rejects fabricated model evidence and IDs",()=>{
            Reject(()=>ShowDiscovery.Hypotheses(JsonNode.Parse("""{"shows":[{"name":"Show","evidence_indices":[0,100]}]}""")!.AsObject(),2));
            Reject(()=>ShowDiscovery.SupportedCandidate(JsonNode.Parse("""{"assessment":"supported","key":"tvdb:999","evidence_indices":[0,1]}""")!.AsObject(),[new(){Series=new("tvdb","12","Show")}],[new(0,"file",0,"a"),new(1,"file",1,"b")]));
        });
        test("Clue-only discovery never extracts dialogue or calls a model",()=>{
            var folder=Path.Combine(root,"clue-only");Directory.CreateDirectory(folder);var file=Path.Combine(folder,"title00.mkv");File.WriteAllText(file,"");
            var discovery=new ShowDiscovery(new DiscoveryCatalog(),(exe,_,_)=>{Check(exe=="probe");return Task.FromResult("{}");});
            var result=discovery.DiscoverAsync(folder,"probe","ffmpeg",null,new Progress<string>(),default,[file]).Result;
            Check(result.Dialogue.Count==0&&result.Notes.Any(n=>n.Contains("Configure a matching model")));
        });
        test("Mixed-show dialogue evidence does not produce a supported identity",()=>{
            var candidates=new List<ShowCandidate>{new(){Series=new("tvdb","12","Show")}};
            var response=JsonNode.Parse("""{"assessment":"uncertain","key":"","reason":"mixed shows","evidence_indices":[]}""")!.AsObject();
            Check(ShowDiscovery.SupportedCandidate(response,candidates,[new(0,"a",0,"one"),new(1,"b",0,"two")])==null);
        });
        test("Discovery inference schemas parse and never request model IDs in hypotheses",()=>{
            var handler=new TestHandler(_=>Response("""{"choices":[{"finish_reason":"stop","message":{"content":"{\"shows\":[]}"}}]}"""));using var http=new HttpClient(handler);
            var transport=new ModelTransport(http,new());ShowDiscovery.InferAsync(transport,"local","http://127.0.0.1:1234/v1/chat/completions",[new(0,"file",0,"hello")],[],default).GetAwaiter().GetResult();
            Check(!handler.Requests[0]["response_format"]!.ToJsonString().Contains("tvdb_id"));
        });
    }
    private sealed class PreparationCatalog:IMetadataCatalog {
        public int Calls;
        public string Provider=>"tvdb";
        public Task<List<SeriesIdentity>> SearchAsync(string title,CancellationToken ct)=>throw new NotSupportedException();
        public Task<SeriesIdentity?> GetAsync(string id,CancellationToken ct)=>throw new NotSupportedException();
        public Task<List<Episode>> EpisodesAsync(string id,CancellationToken ct){Calls++;return Task.FromResult(new List<Episode>{new(1,1,1,"Pilot")});}
    }
    private sealed class DiscoveryCatalog:IMetadataCatalog
    {
        public string Provider=>"tvdb";
        public Task<List<SeriesIdentity>> SearchAsync(string title,CancellationToken ct)=>Task.FromResult(new List<SeriesIdentity>{new("tvdb","12","Show")});
        public Task<SeriesIdentity?> GetAsync(string id,CancellationToken ct)=>Task.FromResult<SeriesIdentity?>(new("tvdb",id,"Show"));
        public Task<List<Episode>> EpisodesAsync(string id,CancellationToken ct)=>Task.FromResult(new List<Episode>());
    }
}
