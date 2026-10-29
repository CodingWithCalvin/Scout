using System.Text.RegularExpressions;
namespace Scout.Core;

public sealed class NamingOptions
{
    public bool PreserveCorrectNames { get; set; } = true;
    public string FileTemplate { get; set; } = "{series} - {code} - {title}";
    public string SeasonTemplate { get; set; } = "Season {season:00}";
    private static readonly string[] Tokens = ["series", "code", "title", "season", "season:00", "episode", "episode:00", "provider", "id"];
    private const string TemplatePattern=@"\{\{|\}\}|\{([^{}]+)\}";

    public void Validate()
    {
        ValidateTemplate(FileTemplate); ValidateTemplate(SeasonTemplate);
        if (!HasToken(FileTemplate,"title") || !(HasToken(FileTemplate,"code") ||
            (Has(FileTemplate, "season") && Has(FileTemplate, "episode"))))
            throw new ArgumentException("The filename template needs {title} and {code}, or season and episode tokens.");
        if (!Has(SeasonTemplate, "season")) throw new ArgumentException("The season folder template needs {season} or {season:00}.");
    }
    private static bool HasToken(string template,string token)=>Regex.Matches(template,TemplatePattern).Any(m=>m.Groups[1].Value==token);
    private static bool Has(string template, string token) => HasToken(template,token)||HasToken(template,token+":00");
    private static void ValidateTemplate(string template)
    {
        if (string.IsNullOrWhiteSpace(template) || template.Length > 160) throw new ArgumentException("Naming templates must contain 1–160 characters.");
        var literal = Regex.Replace(template, TemplatePattern, m => m.Value is "{{" or "}}" || Tokens.Contains(m.Groups[1].Value) ? "" : throw new ArgumentException("Unknown naming token: " + m.Value));
        if (literal.IndexOfAny("{}<>:\"/\\|?*".ToCharArray()) >= 0 || literal.Any(char.IsControl) || literal.Trim() is "." or "..")
            throw new ArgumentException("Naming templates must be a single Windows filename or folder, without path separators or invalid characters.");
    }
    public string FileName(string series, Episode episode,SeriesIdentity? identity=null) => Expand(FileTemplate, series, episode,identity);
    public string Folder(string series, Episode episode,SeriesIdentity? identity=null) => Expand(SeasonTemplate, series, episode,identity);
    private static string Expand(string template, string series, Episode e,SeriesIdentity? identity) => PreviewPlanner.SafeName(Regex.Replace(template, TemplatePattern, m => m.Value=="{{"?"{":m.Value=="}}"?"}":m.Groups[1].Value switch
    {
        "provider" => RequireIdentity(identity).Provider, "id" => RequireIdentity(identity).Id,
        "series" => series, "title" => e.Title, "code" => e.Code,
        "season" => e.Season.ToString(), "season:00" => e.Season.ToString("00"),
        "episode" => e.Number.ToString(), "episode:00" => e.Number.ToString("00"),
        _ => throw new ArgumentException("Unknown naming token.")
    }));

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
