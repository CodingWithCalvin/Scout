namespace Scout.Core;
public static class VideoSelection
{
    public static List<string> ValidateSaved(string root,IReadOnlyList<string> selected) {
        if(selected.Count==0)throw new InvalidOperationException("Choose at least one video.");
        var paths=selected.Select(Path.GetFullPath).ToList();
        if(paths.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=paths.Count||paths.Any(p=>!PreviewPlanner.Within(p,root)))
            throw new InvalidDataException("The saved selection is invalid for this Scout. Use Change selection.");
        if(paths.Any(p=>!File.Exists(p)))throw new FileNotFoundException("A selected video is no longer available. Reconnect the source drive or use Change selection.");
        return paths;
    }
    public static string SeasonFolder(string root,string file) {
        var folder=Path.GetDirectoryName(file)!;
        for(var parent=folder;PreviewPlanner.Within(parent,root)&&!PreviewPlanner.Same(parent,root);parent=Path.GetDirectoryName(parent)!)
            if(System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(parent),@"(?i)^(season[ ._-]*\d+|specials)$"))return parent;
        return folder;
    }
    public static List<string> Enumerate(string root)=>Directory.EnumerateFiles(root,"*",new EnumerationOptions{RecurseSubdirectories=true,AttributesToSkip=FileAttributes.ReparsePoint,IgnoreInaccessible=false})
        .Where(p=>new[]{".mkv",".mp4",".avi",".m4v"}.Contains(Path.GetExtension(p).ToLowerInvariant())).Order(StringComparer.OrdinalIgnoreCase).ToList();
    public static List<string> Scope(IReadOnlyList<string> files,string scope,string? episode=null,string? season=null,IReadOnlyList<string>? selected=null)=>scope switch {
        "episode"=>files.Where(p=>string.Equals(p,episode,StringComparison.OrdinalIgnoreCase)).ToList(),
        "season"=>season==null?[]:files.Where(p=>PreviewPlanner.Within(p,season)).ToList(),
        "show"=>files.ToList(),
        "selected"=>files.Where(p=>selected?.Contains(p,StringComparer.OrdinalIgnoreCase)==true).ToList(),
        _=>throw new ArgumentException("Unknown discovery scope.")};
}
