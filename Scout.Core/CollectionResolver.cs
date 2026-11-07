using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
namespace Scout.Core;
public static class CollectionResolver
{
    public static string Hash(string text)=>Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static string? FingerprintKey(JsonNode? fp) {
        if(fp.Text("algorithm")=="full-file-sha256-v1")return Regex.IsMatch(fp.Text("sha256"),"^[0-9a-f]{64}$")?"file:"+fp.Text("sha256"):null;
        if(fp.Text("algorithm")!="primary-packets-sha256-v1") return null;
        var video=fp.Text("video");var audio=fp.Text("audio");
        return Regex.IsMatch(video,"^[0-9a-f]{64}$")&&Regex.IsMatch(audio,"^[0-9a-f]{64}$")?video+audio:null;
    }
    public static List<RecordingGroup> Resolve(JsonObject report) {
        var rows=report.Array("files").Select(n=>n!.AsObject()).ToList();
        if(rows.Select(r=>Path.GetFullPath(r.Text("path"))).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=rows.Count)
            throw new InvalidDataException("Duplicate input path.");
        var groups=new List<RecordingGroup>();
        foreach(var batch in rows.GroupBy(r=>FingerprintKey(r["primary_fingerprint"])??Path.GetFullPath(r.Text("path")).ToUpperInvariant())) {
            var entries=batch.ToList();var claims=new Dictionary<int,Episode>();var votes=new List<string?>();
            bool invalid=false,blocked=false;
            foreach(var row in entries) {
                var candidates=row.Array("candidates");
                bool combined=MultipartEvidence.Combined(row);
                bool bad=(candidates.Count>1||row["reasoning"].Text("coverage")=="combined")&&!combined||row["reasoning"].Text("coverage")=="partial"&&!MultipartEvidence.Partial(row);
                invalid|=bad;
                bool error=!string.IsNullOrEmpty(row.Text("error"))||row.Bool("source_changed")||row.Text("relocation_error")!=""||row.Bool("move_pending")||row.Bool("conflict_review_pending")||row.Text("conflict_review_error")!="";blocked|=error;
                var chosen=(bad||combined?candidates:candidates.Take(1)).Select(c=>Data.Episode(c!["episode"]!)).ToList();
                foreach(var e in chosen)claims[e.Id]=e;
                votes.Add(chosen.Count>0&&!error?string.Join(",",chosen.Select(e=>e.Id))+(MultipartEvidence.Partial(row)?":partial":":complete"):null);
            }
            var status=invalid?"invalid_model_output":blocked?"source_or_model_error":
                entries.Count>1&&votes.Distinct().Count()>1?"unresolved_conflict":
                votes.Distinct().Count()==1&&votes[0]!=null?(entries.All(MultipartEvidence.Partial)?"partial_unverified":"suggested"):"unassigned";
            groups.Add(new RecordingGroup {Id=Hash(string.Join("\n",entries.Select(r=>r.Text("path")).Order(StringComparer.Ordinal)))[..16],
                Rows=entries,Claims=claims.Values.ToList(),Status=status,Episodes=status=="suggested"?claims.Values.ToList():[]});
        }
        foreach(var claim in groups.SelectMany(g=>g.Claims.Select(e=>(e.Id,Group:g))).GroupBy(p=>p.Id)) {
            var contenders=claim.Select(p=>p.Group).Distinct().ToList();
            bool verifiedSplit=false;
            if(contenders.Count==2&&contenders.All(g=>g.Status=="partial_unverified"&&g.Claims.Count==1)) {
                var episode=contenders[0].Claims.Single();
                foreach(var link in report.Array("split_assignments").OfType<JsonObject>()) {
                    var a=contenders[0];var b=contenders[1];
                    if(!MultipartEvidence.SplitLink(link,a,b,episode)){(a,b)=(b,a);if(!MultipartEvidence.SplitLink(link,a,b,episode))continue;}
                    var aFirst=link["verification"].Int("first_recording")==0;
                    a.Part=aFirst?1:2;b.Part=aFirst?2:1;
                    foreach(var g in contenders){g.Status="suggested_split";g.Episodes=[episode];g.PartCount=2;g.SplitEvidence=link.DeepClone().AsObject();}
                    verifiedSplit=true;break;
                }
            }
            if(!verifiedSplit&&(contenders.Count>1||contenders.Any(g=>g.Status=="unresolved_conflict")))
                foreach(var g in contenders){g.Status="unresolved_conflict";g.Episodes=[];}

        }
        foreach(var g in groups.Where(g=>g.Status=="unresolved_conflict"))
            g.CompetingRecordings=groups.Where(other=>other!=g&&other.Claims.Any(e=>g.Claims.Any(c=>c.Id==e.Id))).SelectMany(other=>other.Rows).Select(r=>r.Text("path")).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return groups;
    }
}
