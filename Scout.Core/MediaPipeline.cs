using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
namespace Scout.Core;
public sealed record MatchSettings(string Root,string Catalog,string Output,string Model,string Endpoint,string Ffmpeg="ffmpeg",string Ffprobe="ffprobe",string? DialogueInput=null);
public sealed class MediaPipeline(DialogueMatcher matcher)
{
    public static async Task<string> ExecuteAsync(string exe,IEnumerable<string> args,CancellationToken ct) {
        var start=new ProcessStartInfo(exe) {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var arg in args)start.ArgumentList.Add(arg);
        using var process=Process.Start(start)??throw new IOException("Cannot start "+exe);
        using var cancel=ct.Register(()=>{try{if(!process.HasExited)process.Kill(true);}catch(InvalidOperationException){}});
        var output=process.StandardOutput.ReadToEndAsync(ct);var error=process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);var stderr=await error;var stdout=await output;
        if(process.ExitCode!=0)throw new IOException(Path.GetFileName(exe)+" failed: "+stderr[..Math.Min(stderr.Length,600)]);
        return stdout;
    }
    public static List<Cue> ParseSrt(string value) {
        var cues=new List<Cue>();
        foreach(var block in Regex.Split(value.Replace("\r","").Trim(),@"\n\s*\n")) {
            var m=Regex.Match(block,@"(\d+):(\d+):(\d+)[,.](\d+)\s*-->[^\n]*\n(.*)",RegexOptions.Singleline);
            if(!m.Success)continue;
            double time=int.Parse(m.Groups[1].Value)*3600+int.Parse(m.Groups[2].Value)*60+int.Parse(m.Groups[3].Value)+int.Parse(m.Groups[4].Value)/1000.0;
            var text=Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(m.Groups[5].Value,@"<[^>]+>|\{[^}]*\}","")),@"\s+"," ").Trim();
            if(text.Length>0)cues.Add(new(time,text));
        }
        return cues;
    }
    public async Task<string> RunAsync(MatchSettings settings,IProgress<string> progress,CancellationToken ct) {
        if(!Directory.Exists(settings.Root))throw new DirectoryNotFoundException("Video folder does not exist.");
        if(PreviewPlanner.Within(settings.Output,settings.Root))throw new InvalidOperationException("Results must be outside the media library.");
        matcher.ValidateEndpoint(settings.Endpoint);
        if(string.IsNullOrWhiteSpace(settings.Model))throw new ArgumentException("Choose a local model ID.");
        var episodes=Data.Catalog(settings.Catalog);
        var cachedRows=settings.DialogueInput is null?new Dictionary<string,JsonObject>(StringComparer.OrdinalIgnoreCase):
            Data.Read(settings.DialogueInput).Array("files").ToDictionary(n=>Path.GetFullPath(n.Text("path")),n=>n!.AsObject(),StringComparer.OrdinalIgnoreCase);
        var paths=Directory.EnumerateFiles(settings.Root,"*",SearchOption.AllDirectories).Where(p=>new[]{".mkv",".mp4",".avi",".m4v"}.Contains(Path.GetExtension(p).ToLowerInvariant())).Order(StringComparer.OrdinalIgnoreCase).ToList();
        if(paths.Count==0)throw new InvalidOperationException("No supported video files.");
        Directory.CreateDirectory(settings.Output);var cache=Path.Combine(settings.Output,"reasoning-cache");Directory.CreateDirectory(cache);
        var rows=new JsonArray();int index=0;
        foreach(var path in paths) {
            ct.ThrowIfCancellationRequested();progress.Report($"[{++index}/{paths.Count}] {Path.GetFileName(path)}");
            var row=new JsonObject {["path"]=Path.GetFullPath(path),["matching_method"]="reasoning",["full_dialogue"]=true,["candidates"]=new JsonArray()};
            try {
                long size=new FileInfo(path).Length,mtime=PreviewPlanner.MtimeNs(path);string sample=PreviewPlanner.SampledDigest(path);
                List<Cue> cues;
                if(cachedRows.TryGetValue(Path.GetFullPath(path),out var cached)&&cached.Bool("full_dialogue")&&cached["dialogue"]?["cues"] is JsonArray saved&&cached["size"]?.GetValue<long>()==size&&cached.Text("sampled_digest")==sample) {
                    cues=saved.Select(n=>new Cue(n!["start"]!.GetValue<double>(),n.Text("text"))).ToList();row["duration"]=cached["duration"]?.DeepClone();
                    progress.Report("Reusing unchanged full-dialogue extraction.");
                } else {
                    var probe=JsonNode.Parse(await ExecuteAsync(settings.Ffprobe,["-v","error","-show_streams","-show_format","-of","json",path],ct))!;
                    row["duration"]=double.Parse(probe["format"].Text("duration","0"),CultureInfo.InvariantCulture);
                    var streams=probe.Array("streams").Where(n=>n.Text("codec_type")=="subtitle"&&new[]{"subrip","ass","ssa","mov_text","webvtt","text"}.Contains(n.Text("codec_name"))).ToList();
                    var subtitle=streams.FirstOrDefault(n=>n?["tags"].Text("language") is "eng" or "en")??streams.FirstOrDefault();
                    if(subtitle is null)throw new InvalidDataException("No readable text subtitles. Speech transcription is not yet available in the .NET build.");
                    cues=ParseSrt(await ExecuteAsync(settings.Ffmpeg,["-nostdin","-v","error","-i",path,"-map","0:"+subtitle.Int("index"),"-f","srt","-"],ct));
                }
                if(cues.Count==0)throw new InvalidDataException("No usable dialogue.");
                row["size"]=size;row["sampled_digest"]=sample;
                row["dialogue"]=new JsonObject {["cues"]=JsonSerializer.SerializeToNode(cues,Data.Options)};
                var key=CollectionResolver.Hash(JsonSerializer.Serialize(new{version=DialogueMatcher.Version,scope=matcher.CacheScope,settings.Endpoint,cues,episodes,settings.Model}));
                var cachePath=Path.Combine(cache,key+".json");
                JsonObject result;
                if(File.Exists(cachePath)){result=Data.Read(cachePath);progress.Report("Reusing exact-input model result.");}
                else{progress.Report("Reading full dialogue and independently verifying guide evidence…");result=await matcher.IdentifyAsync(cues,episodes,settings.Model,settings.Endpoint,ct);Data.Save(cachePath,result);}
                row["reasoning"]=result;
                row["candidates"]=new JsonArray(result.Array("matches").Select(n=>(JsonNode)new JsonObject{
                    ["episode"]=JsonSerializer.SerializeToNode(episodes.Single(e=>e.Id==n.Int("episode_id")),Data.Options),
                    ["method"]="reasoning",["reason"]=n!["reason"]?.DeepClone(),
                    ["evidence"]=new JsonArray(n.Array("evidence").Select(b=>(JsonNode)new JsonObject{["start"]=b!["start"]?.DeepClone(),["text"]=b["source_text"]?.DeepClone()}).ToArray())
                }).ToArray());
                progress.Report("Verifying complete primary video/audio hashes…");
                var hashes=await ExecuteAsync(settings.Ffmpeg,["-nostdin","-v","error","-i",path,"-map","0:v:0","-map","0:a:0","-c","copy","-f","streamhash","-hash","sha256","-"],ct);
                string StreamHash(string type)=>Regex.Match(hashes,@"\d+,"+type+@",SHA256=([0-9a-fA-F]{64})").Groups[1].Value.ToLowerInvariant();
                var video=StreamHash("v");var audio=StreamHash("a");
                if(video.Length!=64||audio.Length!=64)throw new InvalidDataException("Both primary stream hashes are required.");
                if(new FileInfo(path).Length!=size||PreviewPlanner.MtimeNs(path)!=mtime||PreviewPlanner.SampledDigest(path)!=sample)throw new IOException("Media changed while processing.");
                row["primary_fingerprint"]=new JsonObject {["algorithm"]="primary-packets-sha256-v1",["video"]=video,["audio"]=audio,["size"]=size,["mtime_ns"]=mtime,["sampled_digest"]=sample};
                row["status"]="needs_review";
            }catch(OperationCanceledException){throw;}
            catch(UsageLimitException){throw;}
            catch(Exception ex) {row["error"]=ex.Message;row["status"]="error";row["candidates"]=new JsonArray();progress.Report("Held for review: "+ex.Message);}
            rows.Add(row);
            Data.Save(Path.Combine(settings.Output,"report.json"),new JsonObject {["files"]=rows.DeepClone(),["pipeline"]=DialogueMatcher.Version,["manual_corrections"]=false});
        }
        Data.Save(Path.Combine(settings.Output,"catalog.json"),new{episodes});
        return Path.Combine(settings.Output,"report.json");
    }
}
