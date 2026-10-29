using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
namespace Scout.Core;

public static class FilenameCatalogMatcher
{
    public static Episode? Match(string path,IReadOnlyList<Episode> catalog) {
        var name=Path.GetFileNameWithoutExtension(path);
        var matches=Regex.Matches(name,@"(?i)(?<![a-z0-9])(?:S(?<s>\d{1,3})[ ._-]*E(?<e>\d{1,3})|(?<s>\d{1,3})x(?<e>\d{1,3}))(?!\d)");
        if(matches.Count!=1)return null;
        var m=matches[0];
        if(Regex.IsMatch(name[(m.Index+m.Length)..],@"(?i)^[ ._-]*(?:E\d|\d)"))return null;
        var season=int.Parse(m.Groups["s"].Value);var episode=int.Parse(m.Groups["e"].Value);
        var found=catalog.Where(e=>e.Season==season&&e.Number==episode).ToList();return found.Count==1?found[0]:null;
    }
    public static async Task<string> RunAsync(IReadOnlyList<string> paths,string root,string output,SeriesIdentity identity,IReadOnlyList<Episode> catalog,IProgress<string> progress,CancellationToken ct) {
        identity.Validate();
        if(PreviewPlanner.Within(output,root))throw new InvalidOperationException("Run storage must be outside the media folder.");
        if(paths.Count==0||paths.Any(p=>!PreviewPlanner.Within(p,root))||paths.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=paths.Count)throw new ArgumentException("Select distinct source files within this Scout.");
        var rows=new JsonArray();Directory.CreateDirectory(output);
        Data.Save(Path.Combine(output,"catalog.json"),new{series_identity=identity,episodes=catalog});
        foreach(var path in paths) {
            ct.ThrowIfCancellationRequested();progress.Report("Matching filename and checking exact duplicates: "+Path.GetFileName(path));
            var row=new JsonObject{["path"]=Path.GetFullPath(path),["matching_method"]="filename_catalog",["candidates"]=new JsonArray()};
            try {
                var episode=Match(path,catalog);
                if(episode==null)row["reasoning"]=new JsonObject{["summary"]="No unique season/episode number in this filename and catalog. Leave unresolved; dialogue identification is optional."};
                else {
                    var size=new FileInfo(path).Length;var mtime=PreviewPlanner.MtimeNs(path);var sampled=PreviewPlanner.SampledDigest(path);
                    await using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,65536,true);
                    var digest=Convert.ToHexStringLower(await SHA256.HashDataAsync(stream,ct));
                    if(size!=new FileInfo(path).Length||mtime!=PreviewPlanner.MtimeNs(path)||sampled!=PreviewPlanner.SampledDigest(path))throw new IOException("Source changed during matching.");
                    row["primary_fingerprint"]=new JsonObject{["algorithm"]="full-file-sha256-v1",["sha256"]=digest,["size"]=size,["mtime_ns"]=mtime,["sampled_digest"]=sampled};
                    row["candidates"]=new JsonArray(new JsonObject{["episode"]=JsonSerializer.SerializeToNode(episode,Data.Options),["method"]="filename_catalog",["evidence"]=new JsonArray()});
                    row["reasoning"]=new JsonObject{["summary"]=$"Filename/catalog match: {episode.Code} — {episode.Title}. Uses {identity.Provider} episode numbering. Video content and dialogue were not verified."};
                }
            }catch(OperationCanceledException){throw;}catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){row["error"]=ex.Message;row["candidates"]=new JsonArray();}
            rows.Add(row);
            Data.Save(Path.Combine(output,"report.json"),new JsonObject{["files"]=rows.DeepClone(),["series_identity"]=JsonSerializer.SerializeToNode(identity,Data.Options),["pipeline"]="filename-catalog-v1",["manual_corrections"]=false});
        }
        return Path.Combine(output,"report.json");
    }
}
