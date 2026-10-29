namespace Scout.Core;
public sealed record LocalModelFile(string Name,string Path);
public static class LocalModels {
 public static IReadOnlyList<LocalModelFile> Find(string directory,string configuredFile) {
  var paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  if(Directory.Exists(directory))foreach(var path in Directory.EnumerateFiles(directory,"*.gguf",new EnumerationOptions{RecurseSubdirectories=true,IgnoreInaccessible=true,AttributesToSkip=FileAttributes.ReparsePoint}))paths.Add(System.IO.Path.GetFullPath(path));
  if(File.Exists(configuredFile)&&System.IO.Path.GetExtension(configuredFile).Equals(".gguf",StringComparison.OrdinalIgnoreCase))paths.Add(System.IO.Path.GetFullPath(configuredFile));
  return paths.Order(StringComparer.OrdinalIgnoreCase).Select(p=>new LocalModelFile(System.IO.Path.GetFileNameWithoutExtension(p),p)).ToList();
 }
}
