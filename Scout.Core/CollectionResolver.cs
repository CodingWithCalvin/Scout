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
            var entries=batch.ToList();var claims=new Dictionary<int,Episode>();var votes=new List<int?>();
            bool invalid=false,blocked=false;
            foreach(var row in entries) {
                var candidates=row.Array("candidates");
                bool bad=(row.Text("matching_method")=="reasoning"||row["reasoning"] is not null)&&candidates.Count>1;
                invalid|=bad;
                bool error=!string.IsNullOrEmpty(row.Text("error"))||row.Bool("source_changed");blocked|=error;
                var chosen=(bad?candidates:candidates.Take(1)).Select(c=>Data.Episode(c!["episode"]!)).ToList();
                foreach(var e in chosen)claims[e.Id]=e;
                votes.Add(chosen.Count==1&&!error?chosen[0].Id:null);
            }
            var status=invalid?"invalid_model_output":blocked?"source_or_model_error":
                entries.Count>1&&votes.Distinct().Count()>1?"unresolved_conflict":
                votes.Distinct().Count()==1&&votes[0]!=null?"suggested":"unassigned";
            groups.Add(new RecordingGroup {Id=Hash(string.Join("\n",entries.Select(r=>r.Text("path")).Order(StringComparer.Ordinal)))[..16],
                Rows=entries,Claims=claims.Values.ToList(),Status=status,Episode=status=="suggested"?claims.Values.Single():null});
        }
        foreach(var claim in groups.SelectMany(g=>g.Claims.Select(e=>(e.Id,Group:g))).GroupBy(p=>p.Id)) {
            var contenders=claim.Select(p=>p.Group).Distinct().ToList();
            if(contenders.Count>1||contenders.Any(g=>g.Status=="unresolved_conflict"))
                foreach(var g in contenders){g.Status="unresolved_conflict";g.Episode=null;}
        }
        return groups;
    }
}
