using System.Text.RegularExpressions;
using System.Text;
using System.Globalization;
namespace Scout.Core;

public sealed class NamingOptions
{
    public string? PathTemplate { get; set; }
    public bool PreserveCorrectNames { get; set; } = true;
    public string FileTemplate { get; set; } = "{series} - {code} - {title}";
    public string SeasonTemplate { get; set; } = "Season {season:00}";
    public string FileWordSeparator { get; set; } = " ";
    public string FolderWordSeparator { get; set; } = " ";
    public string CombinedTitleSeparator { get; set; } = " / ";
    private static readonly string[] Filters=["clean","fold","dots","underscores","lower","upper"];
    private static readonly string[] Tokens = ["series", "code", "title", "season", "season:00", "episode", "episode:00", "provider", "id"];
    private const string TemplatePattern=@"\{\{|\}\}|\{([^{}]+)\}";

    public void Validate()
    {
        if(PathTemplate!=null){NamingDsl.Validate(PathTemplate);return;}
        if(!new[]{" ",".","_","-"}.Contains(FileWordSeparator)||!new[]{" ",".","_","-"}.Contains(FolderWordSeparator))throw new ArgumentException("Choose space, period, underscore or hyphen as the word separator.");
        if(CombinedTitleSeparator==null||CombinedTitleSeparator.Length>40||CombinedTitleSeparator.Any(char.IsControl))throw new ArgumentException("Combined title separator must be at most 40 characters without control characters.");
        ValidateTemplate(FileTemplate); ValidateTemplate(SeasonTemplate);
        if (!HasToken(FileTemplate,"title") || !(HasToken(FileTemplate,"code") ||
            (Has(FileTemplate, "season") && Has(FileTemplate, "episode"))))
            throw new ArgumentException("The filename template needs {title} and {code}, or season and episode tokens.");
        if (!Has(SeasonTemplate, "season")) throw new ArgumentException("The season folder template needs {season} or {season:00}.");
    }
    private static bool HasToken(string template,string token)=>Regex.Matches(template,TemplatePattern).Any(m=>m.Groups[1].Value.Split('|')[0]==token);
    private static bool Has(string template, string token) => HasToken(template,token)||HasToken(template,token+":00");
    private static bool ValidToken(string token) {
        var parts=token.Split('|');
        return Tokens.Contains(parts[0])&&parts.Skip(1).All(Filters.Contains)&&(parts.Length==1||parts[0] is "series" or "title" or "provider");
    }
    private static void ValidateTemplate(string template)
    {
        if (string.IsNullOrWhiteSpace(template) || template.Length > 160) throw new ArgumentException("Naming templates must contain 1–160 characters.");
        var literal = Regex.Replace(template, TemplatePattern, m => m.Value is "{{" or "}}" || ValidToken(m.Groups[1].Value) ? "" : throw new ArgumentException("Unknown naming token: " + m.Value));
        if (literal.IndexOfAny("{}<>:\"/\\|?*".ToCharArray()) >= 0 || literal.Any(char.IsControl) || template.Trim() is "." or "..")
            throw new ArgumentException("Naming templates must be a single Windows filename or folder, without path separators or invalid characters.");
    }
    public string FileName(string series, Episode episode,SeriesIdentity? identity=null) => PathTemplate!=null?NamingDsl.Render(PathTemplate,series,[episode],identity:identity).File:Words(Expand(FileTemplate, series, episode,identity),FileWordSeparator);
    public string AssignmentName(string series,IReadOnlyList<Episode> episodes,int part=0,int parts=0,SeriesIdentity? identity=null) {
        if(episodes.Count is <1 or >2||episodes.Count==2&&(episodes[0].Season!=episodes[1].Season||episodes[1].Number!=episodes[0].Number+1)||parts!=0&&(parts!=2||part is <1 or >2||episodes.Count!=1))throw new ArgumentException("Invalid multipart naming assignment.");
        if(PathTemplate!=null)return NamingDsl.Render(PathTemplate,series,episodes,part,parts,identity).File;
        var first=episodes[0] with {Title=string.Join(CombinedTitleSeparator,episodes.Select(e=>e.Title))};
        var stem=Expand(FileTemplate,series,first,identity,episodes.Count==2?$"-E{episodes[1].Number:00}":"");
        return Words(parts>0?stem+$" (Part {part} of {parts})":stem,FileWordSeparator);
    }
    public static bool EquivalentName(string source,string basename)=>Normalize(Path.GetFileNameWithoutExtension(source))==Normalize(basename);
    public bool EquivalentAssignmentName(string source,string series,IReadOnlyList<Episode> episodes,int part=0,int parts=0,SeriesIdentity? identity=null) {
        if(EquivalentName(source,AssignmentName(series,episodes,part,parts,identity)))return true;
        if(episodes.Count!=2)return false;
        if(PathTemplate!=null) {
            // Correct original combined names remain correct regardless of the output template.
            var conventional=new NamingOptions{CombinedTitleSeparator=" and "};
            if(EquivalentName(source,conventional.AssignmentName(series,episodes,part,parts,identity)))return true;
            conventional.CombinedTitleSeparator=" ";
            return EquivalentName(source,conventional.AssignmentName(series,episodes,part,parts,identity));
        }
        // Only the separator BETWEEN the two verified titles is optional. Never remove
        // "and" from within a title or accept different episode numbers/order.
        var joined=episodes[0] with {Title=episodes[0].Title+" and "+episodes[1].Title};
        return EquivalentName(source,Expand(FileTemplate,series,joined,identity,$"-E{episodes[1].Number:00}"));
    }
    public string Folder(string series, Episode episode,SeriesIdentity? identity=null) => PathTemplate!=null?NamingDsl.Render(PathTemplate,series,[episode],identity:identity).Folder:Words(Expand(SeasonTemplate, series, episode,identity),FolderWordSeparator);
    private static string Words(string value,string separator)=>PreviewPlanner.SafeName(Regex.Replace(value,@"\s+",separator));
    private static string ApplyFilter(string value,string filter)=>filter switch {
        "clean"=>Regex.Replace(Regex.Replace(value.Replace("&"," and "),"['’‘\"“”]",""),@"[^\p{L}\p{Nd}\s]"," ").Trim(),
        "fold"=>new string(value.Normalize(NormalizationForm.FormD).Where(c=>CharUnicodeInfo.GetUnicodeCategory(c)!=UnicodeCategory.NonSpacingMark).ToArray()).Normalize(NormalizationForm.FormC),
        "dots"=>Regex.Replace(value.Trim(),@"\s+","."),
        "underscores"=>Regex.Replace(value.Trim(),@"\s+","_"),
        "lower"=>value.ToLowerInvariant(),"upper"=>value.ToUpperInvariant(),
        _=>throw new ArgumentException("Unknown naming filter: "+filter)
    };
    private static string Expand(string template,string series,Episode e,SeriesIdentity? identity,string episodeSuffix="")=>PreviewPlanner.SafeName(Regex.Replace(template,TemplatePattern,m=>{
        if(m.Value=="{{")return "{";if(m.Value=="}}")return "}";
        var parts=m.Groups[1].Value.Split('|');if(!ValidToken(m.Groups[1].Value))throw new ArgumentException("Unknown naming token or filter: "+m.Value);
        var value=parts[0] switch {
            "provider"=>RequireIdentity(identity).Provider,"id"=>RequireIdentity(identity).Id,
            "series"=>series,"title"=>e.Title,"code"=>e.Code+episodeSuffix,
            "season"=>e.Season.ToString(),"season:00"=>e.Season.ToString("00"),
            "episode"=>e.Number.ToString()+episodeSuffix,"episode:00"=>e.Number.ToString("00")+episodeSuffix,
            _=>throw new ArgumentException("Unknown naming token.")
        };
        foreach(var filter in parts.Skip(1))value=ApplyFilter(value,filter);
        return value;
    }));

    public string EditablePathTemplate() {
        if(PathTemplate!=null)return NamingDsl.Editable(PathTemplate);
        string Convert(string template,string separator) {
            var value=Regex.Replace(template,TemplatePattern,m=>{
                if(m.Value is "{{" or "}}")return m.Value;
                var bits=m.Groups[1].Value.Split('|');var token=bits[0];var filters=new List<string>();
                if(token=="title"){token="titles";filters.Add("join("+NamingDsl.Quote(CombinedTitleSeparator)+")");}
                foreach(var filter in bits.Skip(1))filters.Add(filter switch {
                    "clean"=>"replace(\"&\", \" and \")|remove(quotes)|replacePunctuation(\" \")|trim",
                    "fold"=>"remove(diacritics)","dots"=>"replaceWhitespace(\".\")","underscores"=>"replaceWhitespace(\"_\")","lower"=>"lowercase","upper"=>"uppercase",_=>filter
                });
                if(separator!=" "&&bits[0] is "series" or "title" or "provider")filters.Add("replaceWhitespace("+NamingDsl.Quote(separator)+")");
                return "{"+token+(filters.Count==0?"":"|"+string.Join("|",filters))+"}";
            });
            // Legacy separators applied to literal spaces as well as token values.
            return Regex.Replace(value,TemplatePattern+@"|\s+",m=>m.Value.StartsWith('{')||m.Value=="}}"?m.Value:separator);
        }
        var file=Convert(FileTemplate,FileWordSeparator);
        if(FileWordSeparator!=" ")file+="{partSuffix|replaceWhitespace("+NamingDsl.Quote(FileWordSeparator)+")|prefix("+NamingDsl.Quote(FileWordSeparator)+")}";
        return NamingDsl.Editable(Convert(SeasonTemplate,FolderWordSeparator)+"\\"+file);
    }
    private static SeriesIdentity RequireIdentity(SeriesIdentity? identity) { if(identity==null)throw new ArgumentException("Select a catalog show before using {provider} or {id} in naming templates.");identity.Validate();return identity; }

    // Compare title typography, not fuzzy title similarity. Episode numbers must agree exactly.
    private static string Normalize(string text) => Regex.Replace(text.Normalize().Replace("&", "and"), @"[^\p{L}\p{Nd}]", "").ToUpperInvariant();
    public static (bool Correct, bool HasNumber) Inspect(string path, string series, Episode e,SeriesIdentity? identity=null)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        var matches = Regex.Matches(stem, @"(?i)(?<![a-z0-9])(?:S(?<s>\d{1,3})[ ._-]*E(?<e>\d{1,3})|(?<s>\d{1,3})x(?<e>\d{1,3}))(?!\d)");
        if (matches.Count != 1) return (false, matches.Count > 0);
        var m = matches[0];
        var prefixText=stem[..m.Index];
        if(identity!=null)prefixText=Regex.Replace(prefixText,@"(?i)[{\[](tvdb|tmdb|tvmaze)[-=: ](\d+)[}\]]",tag=>tag.Groups[1].Value.Equals(identity.Provider,StringComparison.OrdinalIgnoreCase)&&tag.Groups[2].Value==identity.Id?"":tag.Value);
        var prefix = Normalize(prefixText);
        var title = Normalize(stem[(m.Index + m.Length)..]);
        return (int.Parse(m.Groups["s"].Value) == e.Season && int.Parse(m.Groups["e"].Value) == e.Number &&
            (prefix.Length == 0 || prefix == Normalize(series)) && title == Normalize(PreviewPlanner.SafeName(e.Title)), true);
    }
    public static bool CorrectFolder(string path, string series, Episode e, NamingOptions options,SeriesIdentity? identity=null)
    {
        var folder = Path.GetFileName(Path.GetDirectoryName(path));
        if (string.Equals(folder, options.Folder(series, e,identity), StringComparison.OrdinalIgnoreCase)) return true;
        var match = Regex.Match(folder ?? "", @"(?i)^Season[ ._-]*(\d{1,3})$");
        return match.Success && int.Parse(match.Groups[1].Value) == e.Season || e.Season == 0 && string.Equals(folder, "Specials", StringComparison.OrdinalIgnoreCase);
    }
}
