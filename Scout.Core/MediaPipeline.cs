using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
namespace Scout.Core;
public sealed record MatchSettings(string Root,string Catalog,string Output,string Model,string Endpoint,string Ffmpeg="ffmpeg",string Ffprobe="ffprobe",string? DialogueInput=null,IReadOnlyList<string>? SelectedFiles=null,SeriesIdentity? SeriesIdentity=null,bool Restart=false,bool Reanalyze=false,bool ReviewIssues=false,bool NewFilesOnly=false);
public sealed class MediaPipeline(DialogueMatcher matcher,Func<string,IEnumerable<string>,CancellationToken,Task<string>>? execute=null)
{
    private Task<string> RunTool(string exe,IEnumerable<string> args,CancellationToken ct)=>(execute??ExecuteAsync)(exe,args,ct);
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
    public async Task<string> RunAsync(MatchSettings settings,IProgress<string> progress,CancellationToken ct,ScoutPause? pause=null) {
        pause?.Checkpoint();ct.ThrowIfCancellationRequested();
        if(!Directory.Exists(settings.Root))throw new DirectoryNotFoundException("Video folder does not exist.");
        if(PreviewPlanner.Within(settings.Output,settings.Root))throw new InvalidOperationException("Results must be outside the media library.");
        matcher.ValidateEndpoint(settings.Endpoint);
        if(string.IsNullOrWhiteSpace(settings.Model))throw new ArgumentException("Choose a local model ID.");
        var episodes=Data.Catalog(settings.Catalog);
        var reportPath=Path.Combine(settings.Output,"report.json");
        var continuing=settings.Restart||settings.Reanalyze||settings.ReviewIssues||settings.NewFilesOnly;
        var previous=continuing&&ScoutStorage.Exists(reportPath)?Data.Read(reportPath):new JsonObject();
        MoveExecutor.RequireSettled(previous);
        var relocations=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        if(continuing&&previous.Array("files").Count>0) {
            relocations=await RecordingRelocation.ReconnectAsync(previous,settings.Root,settings.Ffmpeg,progress,ct);
            Data.Save(reportPath,previous);
        }
        var dialogueInput=continuing&&ScoutStorage.Exists(reportPath)?reportPath:settings.DialogueInput;
        var cachedRows=dialogueInput is null?new Dictionary<string,JsonObject>(StringComparer.OrdinalIgnoreCase):
            Data.Read(dialogueInput).Array("files").ToDictionary(n=>Path.GetFullPath(n.Text("path")),n=>n!.AsObject(),StringComparer.OrdinalIgnoreCase);
        var paths=RecordingRelocation.MapSelection(previous,(settings.SelectedFiles??VideoSelection.Enumerate(settings.Root)).Concat(previous.Array("files").Select(r=>r.Text("path"))));
        var issues=settings.ReviewIssues||settings.Restart&&!settings.Reanalyze&&previous["issue_review"].Bool("active");
        var issuePaths=settings.ReviewIssues?IssueReview.Select(previous):previous["issue_review"].Array("paths").Select(p=>p!.GetValue<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if(settings.ReviewIssues)foreach(var path in paths.Where(p=>!cachedRows.ContainsKey(Path.GetFullPath(p))))issuePaths.Add(Path.GetFullPath(path));
        if(issues) {
            if(issuePaths.Count==0){progress.Report("No issues found. Saved results are unchanged.");return reportPath;}
            paths=issuePaths.Order(StringComparer.OrdinalIgnoreCase).ToList();
        }
        var incremental=settings.NewFilesOnly||settings.Restart&&!settings.Reanalyze&&!settings.ReviewIssues&&previous["incremental_review"].Bool("active");
        if(settings.NewFilesOnly&&previous["incremental_review"].Bool("active"))
            throw new InvalidOperationException("Resume Scout to finish the pending new files before scanning for more.");
        if(incremental) {
            paths=settings.NewFilesOnly
                ?VideoSelection.Enumerate(settings.Root).Where(p=>!cachedRows.ContainsKey(Path.GetFullPath(p))).ToList()
                :RecordingRelocation.MapSelection(previous,previous["incremental_review"].Array("paths").Select(p=>p!.GetValue<string>()));
            if(paths.Count==0){progress.Report("No new video files found. Saved analysis is unchanged.");return reportPath;}
        }
        var scoped=issues||incremental;
        if(paths.Any(p=>(!continuing&&!File.Exists(p))||!PreviewPlanner.Within(p,settings.Root))||paths.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=paths.Count)throw new InvalidDataException("Selected files must be distinct existing videos within this Scout.");
        if(paths.Count==0)throw new InvalidOperationException("No supported video files.");
        ScoutStorage.EnsureDirectory(settings.Output);var cache=Path.Combine(settings.Output,"reasoning-cache");ScoutStorage.EnsureDirectory(cache);
        var selected=paths.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rows=new JsonArray(previous.Array("files").Where(r=>scoped||selected.Contains(Path.GetFullPath(r.Text("path")))).Select(r=>r!.DeepClone()).ToArray());int index=0;
        var generation=settings.Reanalyze||settings.ReviewIssues?Guid.NewGuid().ToString("N"):previous.Text("analysis_generation");
        var review=issues?new JsonObject{["active"]=true,["paths"]=JsonSerializer.SerializeToNode(paths,Data.Options)}:null;
        var incrementalReview=incremental?new JsonObject{["active"]=true,["paths"]=JsonSerializer.SerializeToNode(paths,Data.Options)}:null;
        var retainedSplits=previous.Array("split_assignments").DeepClone().AsArray();
        if(settings.Reanalyze)retainedSplits.Clear();
        if(settings.ReviewIssues) {
            var keys=rows.OfType<JsonObject>().Where(r=>selected.Contains(Path.GetFullPath(r.Text("path")))).Select(r=>CollectionResolver.FingerprintKey(r["primary_fingerprint"])).ToHashSet();
            foreach(var link in retainedSplits.ToList())if(keys.Contains(link.Text("fingerprint_a"))||keys.Contains(link.Text("fingerprint_b")))retainedSplits.Remove(link);
        }
        void SaveCheckpoint()=>Data.Save(reportPath,new JsonObject {["files"]=rows.DeepClone(),["moves"]=previous.Array("moves").DeepClone(),["analysis_generation"]=generation,["issue_review"]=review?.DeepClone(),["incremental_review"]=incrementalReview?.DeepClone(),["split_assignments"]=retainedSplits.DeepClone(),["series_identity"]=JsonSerializer.SerializeToNode(settings.SeriesIdentity,Data.Options),["pipeline"]=DialogueMatcher.Version,["manual_corrections"]=false});
        if(settings.Reanalyze||settings.ReviewIssues) {
            ct.ThrowIfCancellationRequested();
            Data.Save(Path.Combine(settings.Output,"analysis-history",generation,"report.json"),previous);
            var statuses=CollectionResolver.Resolve(previous).SelectMany(g=>g.Rows.Select(r=>(Path:Path.GetFullPath(r.Text("path")),g.Status))).ToDictionary(x=>x.Path,x=>x.Status,StringComparer.OrdinalIgnoreCase);
            foreach(var old in rows.OfType<JsonObject>().Where(r=>!issues||selected.Contains(Path.GetFullPath(r.Text("path"))))) {
                if(issues)old["issue_review_reason"]=statuses.GetValueOrDefault(Path.GetFullPath(old.Text("path")),"unfinished");
                old.Remove("reasoning");old.Remove("conflict_review");old.Remove("conflict_review_pending");old.Remove("conflict_review_error");
                old["candidates"]=new JsonArray();old["error"]="Awaiting fresh analysis";old["status"]="analysis_pending";
            }
            SaveCheckpoint();
        }
        if(incremental)SaveCheckpoint();
        void SaveRow(JsonObject row) {
            var existing=rows.FirstOrDefault(r=>string.Equals(Path.GetFullPath(r.Text("path")),Path.GetFullPath(row.Text("path")),StringComparison.OrdinalIgnoreCase));
            if(existing!=null)rows[rows.IndexOf(existing)]=row;else rows.Add(row);
            SaveCheckpoint();
        }
        foreach(var path in paths) {
            pause?.Checkpoint();ct.ThrowIfCancellationRequested();progress.Report($"[{++index}/{paths.Count}] {Path.GetFileName(path)}");
            if(cachedRows.TryGetValue(Path.GetFullPath(path),out var relocated)&&relocated.Text("relocation_error")!="") {SaveRow(relocated.DeepClone().AsObject());progress.Report(relocated.Text("relocation_error"));continue;}
            if(!settings.Reanalyze&&!settings.ReviewIssues&&settings.Restart&&cachedRows.TryGetValue(Path.GetFullPath(path),out var completed)&&RunRecovery.CanReuse(completed,episodes)) {
                progress.Report("Reusing successful model result and verified source fingerprints.");continue;
            }
            var row=new JsonObject {["path"]=Path.GetFullPath(path),["matching_method"]="reasoning",["full_dialogue"]=true,["candidates"]=new JsonArray()};
            if(issues)row["issue_review_reason"]=rows.FirstOrDefault(r=>string.Equals(Path.GetFullPath(r.Text("path")),path,StringComparison.OrdinalIgnoreCase)).Text("issue_review_reason","unfinished or competing recording");
            try {
                long size=new FileInfo(path).Length,mtime=PreviewPlanner.MtimeNs(path);string sample=PreviewPlanner.SampledDigest(path);
                List<Cue> cues;
                if(cachedRows.TryGetValue(Path.GetFullPath(path),out var cached)&&cached.Bool("full_dialogue")&&cached["dialogue"]?["cues"] is JsonArray saved&&cached["size"]?.GetValue<long>()==size&&cached.Text("sampled_digest")==sample&&(cached["source_mtime_ns"]?.GetValue<long>()??cached["primary_fingerprint"]?["mtime_ns"]?.GetValue<long>())==mtime) {
                    cues=saved.Select(n=>new Cue(n!["start"]!.GetValue<double>(),n.Text("text"))).ToList();row["duration"]=cached["duration"]?.DeepClone();
                    progress.Report("Reusing unchanged full-dialogue extraction.");
                } else {
                    var probe=JsonNode.Parse(await RunTool(settings.Ffprobe,["-v","error","-show_streams","-show_format","-of","json",path],ct))!;
                    row["duration"]=double.Parse(probe["format"].Text("duration","0"),CultureInfo.InvariantCulture);
                    var streams=probe.Array("streams").Where(n=>n.Text("codec_type")=="subtitle"&&new[]{"subrip","ass","ssa","mov_text","webvtt","text"}.Contains(n.Text("codec_name"))).ToList();
                    var subtitle=streams.FirstOrDefault(n=>n?["tags"].Text("language") is "eng" or "en")??streams.FirstOrDefault();
                    if(subtitle is null)throw new InvalidDataException("No readable text subtitles. Speech transcription is not yet available in the .NET build.");
                    cues=ParseSrt(await RunTool(settings.Ffmpeg,["-nostdin","-v","error","-i",path,"-map","0:"+subtitle.Int("index"),"-f","srt","-"],ct));
                }
                if(cues.Count==0)throw new InvalidDataException("No usable dialogue.");
                row["size"]=size;row["sampled_digest"]=sample;row["source_mtime_ns"]=mtime;
                row["dialogue"]=new JsonObject {["cues"]=JsonSerializer.SerializeToNode(cues,Data.Options)};
                if(cached!=null&&RunRecovery.SourceUnchanged(cached))row["primary_fingerprint"]=cached["primary_fingerprint"]!.DeepClone();
                var key=CollectionResolver.Hash(JsonSerializer.Serialize(new{generation,issues,version=DialogueMatcher.Version,revision=DialogueMatcher.RequestRevision,scope=matcher.CacheScope,settings.Endpoint,cues,episodes,settings.Model}));
                var cachePath=Path.Combine(cache,key+".json");
                JsonObject result;
                if(ScoutStorage.Exists(cachePath)){result=Data.Read(cachePath);progress.Report("Reusing exact-input model result.");}
                else{progress.Report("Reading full dialogue and independently verifying guide evidence…");result=issues?await matcher.ReevaluateConflictAsync(cues,episodes,settings.Model,settings.Endpoint,ct):await matcher.IdentifyAsync(cues,episodes,settings.Model,settings.Endpoint,ct);Data.Save(cachePath,result);}
                row["reasoning"]=result;
                row["candidates"]=new JsonArray(result.Array("matches").Select(n=>(JsonNode)new JsonObject{
                    ["episode"]=JsonSerializer.SerializeToNode(episodes.Single(e=>e.Id==n.Int("episode_id")),Data.Options),
                    ["method"]="reasoning",["reason"]=n!["reason"]?.DeepClone(),
                    ["evidence"]=new JsonArray(n.Array("evidence").Select(b=>(JsonNode)new JsonObject{["start"]=b!["start"]?.DeepClone(),["text"]=b["source_text"]?.DeepClone()}).ToArray())
                }).ToArray());
                if(cached!=null&&RunRecovery.SourceUnchanged(cached)) {
                    row["primary_fingerprint"]=cached["primary_fingerprint"]!.DeepClone();
                    progress.Report("Reusing verified primary video/audio fingerprints for unchanged media.");
                } else {
                progress.Report("Verifying complete primary video/audio hashes…");
                var hashes=await RunTool(settings.Ffmpeg,["-nostdin","-v","error","-i",path,"-map","0:v:0","-map","0:a:0","-c","copy","-f","streamhash","-hash","sha256","-"],ct);
                string StreamHash(string type)=>Regex.Match(hashes,@"\d+,"+type+@",SHA256=([0-9a-fA-F]{64})").Groups[1].Value.ToLowerInvariant();
                var video=StreamHash("v");var audio=StreamHash("a");
                if(video.Length!=64||audio.Length!=64)throw new InvalidDataException("Both primary stream hashes are required.");
                row["primary_fingerprint"]=new JsonObject {["algorithm"]="primary-packets-sha256-v1",["video"]=video,["audio"]=audio,["size"]=size,["mtime_ns"]=mtime,["sampled_digest"]=sample};
                }
                if(new FileInfo(path).Length!=size||PreviewPlanner.MtimeNs(path)!=mtime||PreviewPlanner.SampledDigest(path)!=sample)throw new IOException("Media changed while processing.");
                row["recording_id"]=CollectionResolver.Hash(CollectionResolver.FingerprintKey(row["primary_fingerprint"])!);
                if(cached!=null&&CollectionResolver.FingerprintKey(cached["primary_fingerprint"])==CollectionResolver.FingerprintKey(row["primary_fingerprint"]))
                    foreach(var field in new[]{"original_path","path_history","duplicate_of"})if(cached[field]!=null)row[field]=cached[field]!.DeepClone();
                row["status"]="needs_review";
            }catch(OperationCanceledException){throw;}
            catch(UsageLimitException){throw;}
            catch(Exception ex) {if(ex is ModelRequestTimeoutException timeout){row["error_code"]="model_timeout";row["timeout_seconds"]=timeout.TimeoutSeconds;}if(ex.Data["model_attempts"] is string attempts)row["model_attempts"]=JsonNode.Parse(attempts);row["error"]=ex.Message;row["status"]="error";row["candidates"]=new JsonArray();progress.Report("Held for review: "+ex.Message);}
            SaveRow(row);
            pause?.Checkpoint();
        }
        pause?.Checkpoint();
        // Reconcile only two distinct partial recordings claiming the same catalog episode.
        // Fingerprint duplicates are collapsed before any model comparison.
        var finalReport=Data.Read(Path.Combine(settings.Output,"report.json"));
        var splitAssignments=scoped?retainedSplits.DeepClone().AsArray():new JsonArray();finalReport["split_assignments"]=splitAssignments;
        var groups=CollectionResolver.Resolve(finalReport);
        foreach(var contenders in groups.Where(g=>g.Claims.Count==1).GroupBy(g=>g.Claims[0].Id)) {
            var pair=contenders.ToList();
            if(scoped&&!pair.SelectMany(g=>g.Rows).Any(r=>selected.Contains(Path.GetFullPath(r.Text("path")))))continue;
            if(pair.Count!=2||pair.Any(g=>g.Rows.Any(r=>!MultipartEvidence.Partial(r)||r.Text("error")!=""||r.Bool("source_changed")||CollectionResolver.FingerprintKey(r["primary_fingerprint"])==null)))continue;
            pause?.Checkpoint();ct.ThrowIfCancellationRequested();progress.Report("Checking whether two recordings are complementary parts of "+pair[0].Claims[0].Code+"…");
            try {
                var savedLink=(settings.Reanalyze||settings.ReviewIssues?new JsonArray():previous.Array("split_assignments")).OfType<JsonObject>().FirstOrDefault(l=>MultipartEvidence.SplitLink(l,pair[0],pair[1],pair[0].Claims[0])||MultipartEvidence.SplitLink(l,pair[1],pair[0],pair[0].Claims[0]));
                var link=savedLink?.DeepClone().AsObject()??await matcher.VerifySplitAsync(pair[0].Rows[0],pair[1].Rows[0],pair[0].Claims[0],settings.Model,settings.Endpoint,ct);
                if(link!=null&&!splitAssignments.Any(existing=>JsonNode.DeepEquals(existing,link)))splitAssignments.Add(link);
            }catch(OperationCanceledException){throw;}
            catch(UsageLimitException){throw;}
            catch(Exception ex){progress.Report("Split verification unresolved: "+ex.Message);}
            Data.Save(Path.Combine(settings.Output,"report.json"),finalReport);
            pause?.Checkpoint();
        }
        Data.Save(Path.Combine(settings.Output,"report.json"),finalReport);
        await ConflictReview.RunAsync(finalReport,episodes,matcher,settings.Model,settings.Endpoint,()=>Data.Save(reportPath,finalReport),progress,ct,pause,allowedPaths:scoped?selected:null);
        if(incremental){finalReport["incremental_review"]!["active"]=false;Data.Save(reportPath,finalReport);}
        if(issues){finalReport["issue_review"]!["active"]=false;Data.Save(reportPath,finalReport);}
        Data.Save(Path.Combine(settings.Output,"catalog.json"),new{series_identity=settings.SeriesIdentity,episodes});
        return Path.Combine(settings.Output,"report.json");
    }
}
