using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
namespace Scout.Core;
public sealed class PlanItem
{
    public required string Source {get;init;}
    public string? Destination {get;init;}
    public required string Category {get;init;}
    public Episode? Episode {get;init;}
    public required string GroupId {get;init;}
    public string? PrimarySource {get;init;}
    public string Action {get;set;}="hold";
    public List<string> BlockedReasons {get;init;}=[];
    public List<string> Notes {get;init;}=[];
    public string? FeatureDescription {get;init;}
}
public sealed record MovePlan(string LibraryRoot,string Series,List<PlanItem> Items,List<Episode> NotIdentified)
{
    public string Mode=>"preview_only";
    public bool MediaFilesModified=>false;
    public Dictionary<string,int> Summary=>Items.GroupBy(i=>i.Category).ToDictionary(g=>g.Key,g=>g.Count());
    public Dictionary<string,int> Actions=>Items.GroupBy(i=>i.Action).ToDictionary(g=>g.Key,g=>g.Count());
}
public static class PreviewPlanner
{
    public static bool Same(string a,string b)=>string.Equals(Path.GetFullPath(a),Path.GetFullPath(b),StringComparison.OrdinalIgnoreCase);
    public static bool Within(string path,string root)=>Same(path,root)||Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
    public static string SafeName(string value) {
        var clean=Regex.Replace(Regex.Replace(value,@"[<>:""/\\|?*\x00-\x1f]"," "),@"\s+"," ").Trim(' ','.');
        if(clean.Length>180) clean=clean[..180].TrimEnd(' ','.');
        if(clean.Length==0)clean="Untitled";
        if(Regex.IsMatch(clean,@"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])($|\.)",RegexOptions.IgnoreCase))clean="_"+clean;
        return clean;
    }
    public static string SampledDigest(string path) {
        using var stream=File.OpenRead(path);using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(stream.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        foreach(long pos in new[]{0L,Math.Max(0,stream.Length/2-32768),Math.Max(0,stream.Length-65536)}.Distinct().Order()) {
            stream.Position=pos;byte[] bytes=new byte[Math.Min(65536,stream.Length-pos)];stream.ReadExactly(bytes);hash.AppendData(bytes);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
    public static long MtimeNs(string path)=>(File.GetLastWriteTimeUtc(path).Ticks-DateTime.UnixEpoch.Ticks)*100;
    private static bool HasReparseAncestor(string path) {
        for(var current=Path.GetFullPath(path);current!=null;current=Path.GetDirectoryName(current))
            if((File.Exists(current)||Directory.Exists(current))&&(File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)return true;
        return false;
    }
    public static MovePlan Create(JsonObject report,string root,string series,IReadOnlyList<Episode>? catalog=null,bool checkSources=true) {
        root=Path.GetFullPath(root);series=SafeName(series);
        if(string.IsNullOrWhiteSpace(series))throw new ArgumentException("Series name required.");
        var items=new List<PlanItem>();
        foreach(var group in CollectionResolver.Resolve(report)) {
            var e=group.Episode;string folder=e is null?"Extras":$"Season {e.Season:00}";
            string basename=SafeName(e is null?series+" - Non-episode feature":$"{series} - {e.Code} - {e.Title}");
            string Canonical(JsonObject r)=>Path.Combine(root,folder,basename+Path.GetExtension(r.Text("path")).ToLowerInvariant());
            var rows=group.Rows.OrderByDescending(r=>e!=null&&Same(r.Text("path"),Canonical(r)))
                .ThenByDescending(r=>r.Array("candidates").FirstOrDefault()?["filename_evidence"].Bool("number_match")==true&&r.Array("candidates").FirstOrDefault()?["filename_evidence"].Bool("title_match")==true)
                .ThenBy(r=>r.Text("path"),StringComparer.OrdinalIgnoreCase).ToList();
            for(int index=0;index<rows.Count;index++) {
                var row=rows[index];var source=Path.GetFullPath(row.Text("path"));var suffix=Path.GetExtension(source).ToLowerInvariant();
                var token=CollectionResolver.Hash(source.ToLowerInvariant())[..10];bool duplicate=index>0;
                string? target=e is not null ? (duplicate?Path.Combine(root,"_Duplicates",folder,basename+$" [duplicate-{token}]"+suffix):Canonical(row)):
                    group.Feature?Path.Combine(duplicate?Path.Combine(root,"_Duplicates","Extras"):Path.Combine(root,"Extras"),basename+$" [{token}]"+suffix):null;
                var item=new PlanItem {Source=source,Destination=target,Episode=e,GroupId=group.Id,PrimarySource=group.Duplicate?rows[0].Text("path"):null,
                    Category=target is null?"unresolved":duplicate?"duplicate":e is not null?"episode":"feature",
                    FeatureDescription=group.Feature?row["reasoning"].Text("summary"):null,
                    Action=target is null?"hold":Same(source,target)?"unchanged":"rename/move"};
                if(!Within(source,root))item.BlockedReasons.Add("Source is outside the selected library.");
                if(target is not null&&(!Within(target,root)||HasReparseAncestor(target)||HasReparseAncestor(source)))
                    item.BlockedReasons.Add("Path leaves the library or traverses a junction/symbolic link; review required.");
                if(duplicate&&target is not null)item.Notes.Add("Identical primary video/audio. Retain this copy; other tracks and metadata can differ.");
                if(group.Duplicate)item.Notes.Add("Primary chosen by canonical path, filename agreement, then path order; not a quality ranking.");
                if((row["duration"]?.GetValue<double>()??0)>2100)item.Notes.Add("Long recording: leading episode only; additional segments may be present.");
                if(target is null)item.Notes.Add("Unresolved match: leave in place.");
                if(target is not null&&checkSources) {
                    var fp=row["primary_fingerprint"];
                    try {
                        if(!File.Exists(source))item.BlockedReasons.Add("Source is unavailable.");
                        else if(CollectionResolver.FingerprintKey(fp) is null)item.BlockedReasons.Add("No verified source fingerprint.");
                        else if(new FileInfo(source).Length!=fp!["size"]!.GetValue<long>() || MtimeNs(source)!=fp["mtime_ns"]!.GetValue<long>() || SampledDigest(source)!=fp.Text("sampled_digest"))
                            item.BlockedReasons.Add("Source changed since matching; rerun identification.");
                    }catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or InvalidOperationException){item.BlockedReasons.Add("Cannot verify source: "+ex.Message);}
                }
                if(item.BlockedReasons.Count>0)item.Action="hold";items.Add(item);
            }
        }
        foreach(var collision in items.Where(i=>i.Destination!=null).GroupBy(i=>i.Destination!,StringComparer.OrdinalIgnoreCase).Where(g=>g.Count()>1))
            foreach(var i in collision){i.BlockedReasons.Add("Multiple proposed files share this destination.");i.Action="hold";}
        var sources=items.ToDictionary(i=>i.Source,StringComparer.OrdinalIgnoreCase);
        foreach(var i in items.Where(i=>i.Action=="rename/move")) {
            if(!File.Exists(i.Destination)&&!Directory.Exists(i.Destination))continue;
            if(sources.TryGetValue(i.Destination!,out var occupant)&&occupant.Action=="rename/move")i.Notes.Add("Destination occupied by another planned source: staged moves would be required.");
            else{i.BlockedReasons.Add("Destination already exists and will not be vacated.");i.Action="hold";}
        }
        bool changed;
        do {
            changed=false;
            foreach(var i in items.Where(i=>i.Action=="rename/move")) {
                if(sources.TryGetValue(i.Destination!,out var other)&&other!=i&&other.Action!="rename/move") {
                    i.BlockedReasons.Add("Destination occupied by a file that is not moving.");i.Action="hold";changed=true;
                }
            }
        }while(changed);
        var assigned=items.Where(i=>i.Episode!=null).Select(i=>i.Episode!.Id).ToHashSet();
        return new(root,series,items,(catalog??[]).Where(e=>e.Season>0&&!assigned.Contains(e.Id)).ToList());
    }
    public static string Write(MovePlan plan,string output) {
        if(Within(output,plan.LibraryRoot))throw new InvalidOperationException("Preview output must be outside the video library.");
        Directory.CreateDirectory(output);Data.Save(Path.Combine(output,"plan.json"),plan);
        var h=new StringBuilder("<!doctype html><html lang=en><meta charset=utf-8><meta name=viewport content='width=device-width'><title>Scout move preview</title><style>body{font:15px Segoe UI,system-ui;background:#111722;color:#eaf0f7;margin:40px}table{width:100%;border-collapse:collapse;table-layout:fixed}td,th{text-align:left;padding:16px;border-bottom:1px solid #344258;overflow-wrap:anywhere;vertical-align:top}h1{font-size:32px}p,a{color:#80dfca}</style><h1>Rename and move preview</h1><p>Preview only. No media files or folders changed. Duplicate copies are retained.</p>");
        string E(string? s)=>WebUtility.HtmlEncode(s??"");
        foreach(var group in plan.Items.GroupBy(i=>i.Category)) {
            h.Append("<h2>").Append(E(group.Key)).Append(" · ").Append(group.Count()).Append("</h2><table><tr><th>Current file</th><th>Proposed location</th><th>Action / evidence</th></tr>");
            foreach(var i in group)h.Append("<tr><td>").Append(E(i.Source)).Append("</td><td>").Append(E(i.Destination??"Leave in place")).Append("</td><td>").Append(E(i.Action)).Append("<br>").Append(E(string.Join(" ",i.BlockedReasons.Concat(i.Notes)))).Append("<br>").Append(E(i.FeatureDescription)).Append("</td></tr>");
            h.Append("</table>");
        }
        h.Append("<h2>Not identified in this scan</h2><p>This does not prove absence: combined recordings may contain additional episodes.</p><ul>");
        foreach(var e in plan.NotIdentified)h.Append("<li>").Append(E(e.Code+" — "+e.Title)).Append("</li>");
        h.Append("</ul><a href='plan.json'>Machine-readable plan</a></html>");
        var path=Path.Combine(output,"preview.html");File.WriteAllText(path,h.ToString());return path;
    }
}
