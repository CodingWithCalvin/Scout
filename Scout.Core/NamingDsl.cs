using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Globalization;
namespace Scout.Core;
public static class NamingDsl {
 private const string Pattern=@"\{\{|\}\}|\{([^{}]+)\}";
 private static readonly string[] Names=NamingEditorLanguage.Tokens.Select(t=>t.Name).ToArray();
 private static readonly Dictionary<string,int> Functions=NamingEditorLanguage.Functions.ToDictionary(f=>f.Name,f=>NamingEditorLanguage.FunctionArity(f.Name));
 private enum RemovalTarget { Punctuation, Symbols, Diacritics, Whitespace, Quotes }
 private sealed record Operation(string Name,string[] Args,RemovalTarget? Target=null);
 private sealed record Expression(string Token,List<Operation> Operations);
 private static List<string> Split(string text,char delimiter,bool chain=false) {
  var values=new List<string>();int start=0;bool quoted=false,escaped=false;
  for(int i=0;i<text.Length;i++) {
   var c=text[i];if(escaped){escaped=false;continue;}if(quoted&&c=='\\'){escaped=true;continue;}if(c=='"'){quoted=!quoted;continue;}
   if(!quoted&&(c==delimiter||chain&&c=='|')){values.Add(text[start..i].Trim());start=i+1;}
  }
  if(quoted)throw new ArgumentException("Unclosed quoted string in naming expression.");values.Add(text[start..].Trim());return values;
 }
 private static Expression Parse(string text) {
  var pieces=Split(text,'.',chain:true);if(!Names.Contains(pieces[0]))throw new ArgumentException("Unknown naming token: "+pieces[0]);
  var ops=new List<Operation>();
  foreach(var piece in pieces.Skip(1)) {
   var match=Regex.Match(piece,@"^([a-zA-Z]+)(?:\((.*)\))?$");
   if(!match.Success||!Functions.TryGetValue(match.Groups[1].Value,out var count))throw new ArgumentException("Unknown naming function: "+piece);
   string[] args=[];RemovalTarget? target=null;
   if(match.Groups[1].Value=="remove"&&!match.Groups[2].Value.TrimStart().StartsWith('"')) {
    var selector=match.Groups[2].Value.Trim();
    if(!Regex.IsMatch(selector,@"^[a-zA-Z]+$")||!Enum.TryParse<RemovalTarget>(selector,true,out var parsed)||!Enum.IsDefined(parsed))throw new ArgumentException("Unknown remove selector. Use punctuation, symbols, diacritics, whitespace, quotes, or a quoted literal.");
    target=parsed;args=[selector];
   }

   if(target==null&&match.Groups[2].Value.Trim().Length>0) {
    try{args=Split(match.Groups[2].Value,',').Select(a=>JsonSerializer.Deserialize<string>(a)??throw new ArgumentException("Function arguments must be quoted strings.")).ToArray();}
    catch(JsonException){throw new ArgumentException("Function arguments must be quoted strings, for example replaceWhitespace(\".\").");}
   }
   if(args.Length!=count)throw new ArgumentException(match.Groups[1].Value+" expects "+count+" quoted argument(s).");
   if((match.Groups[1].Value=="replace"||match.Groups[1].Value=="remove"&&target==null)&&args[0].Length==0)throw new ArgumentException("replace requires a nonempty search string.");
   if(match.Groups[1].Value=="join"&&pieces[0]!="titles")throw new ArgumentException("join applies to {titles}.");
   if(pieces[0] is "code" or "season" or "season:00" or "episode" or "episode:00" or "id" or "part" or "parts")throw new ArgumentException("Formatting functions apply to text tokens; keep episode numbers and IDs intact.");
   ops.Add(new(match.Groups[1].Value,args,target));
  }
  return new(pieces[0],ops);
 }
 private const string QuoteCharacters="'\u2019\u2018\"\u201c\u201d";
 private static readonly JsonSerializerOptions ReadableStrings=new(){Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping};
 public static string Quote(string value)=>JsonSerializer.Serialize(value,ReadableStrings);
 public static string Editable(string template) {
  Validate(template);
  return Regex.Replace(template,Pattern,m=>{
   if(!m.Groups[1].Success)return m.Value;
   var expression=Parse(m.Groups[1].Value);var ops=expression.Operations;
   // Compact the old six literal quote removals without changing their meaning.
   for(int i=0;i+6<=ops.Count;i++) {
    var sequence=ops.Skip(i).Take(6).ToList();
    if(sequence.All(o=>o.Name=="remove"&&o.Target==null&&o.Args[0].Length==1)&&sequence.Select(o=>o.Args[0][0]).ToHashSet().SetEquals(QuoteCharacters)) {
     ops.RemoveRange(i,6);ops.Insert(i,new("remove",["quotes"],RemovalTarget.Quotes));
    }
   }
   return "{"+expression.Token+string.Concat(ops.Select(o=>"."+o.Name+"("+(o.Target is {} target?target.ToString().ToLowerInvariant():string.Join(", ",o.Args.Select(Quote)))+")"))+"}";
  });
 }
 private static List<string> Components(string template) {
  if(string.IsNullOrWhiteSpace(template)||template.Length>1024)throw new ArgumentException("Path template must contain 1–1024 characters.");
  var parts=new List<string>();var current=new StringBuilder();int offset=0;
  void Literal(string text) {foreach(var c in text)if(c is '\\' or '/'){parts.Add(current.ToString());current.Clear();}else current.Append(c);}
  foreach(Match m in Regex.Matches(template,Pattern)){Literal(template[offset..m.Index]);current.Append(m.Value);offset=m.Index+m.Length;}
  Literal(template[offset..]);parts.Add(current.ToString());
  if(parts.Count is <2 or >8||parts.Any(p=>string.IsNullOrWhiteSpace(p)||p.Trim() is "." or ".."))throw new ArgumentException("Use relative folders followed by a filename; empty components and . or .. paths are not allowed.");
  return parts;
 }
 public static void Validate(string template) {
  var parts=Components(template);var tokens=new List<List<string>>();
  foreach(var part in parts) {
   var names=new List<string>();tokens.Add(names);
   var literal=Regex.Replace(part,Pattern,m=>{if(m.Value is "{{" or "}}")return "";names.Add(Parse(m.Groups[1].Value).Token);return "";});
   if(literal.IndexOfAny("{}<>:\"|?*".ToCharArray())>=0||literal.Any(char.IsControl))throw new ArgumentException("Invalid literal filename characters in path template.");
  }
  if(!tokens.Take(tokens.Count-1).Any(t=>t.Contains("season")||t.Contains("season:00")))throw new ArgumentException("Folder components must include a season token.");
  var file=tokens[^1];if(!file.Any(t=>t is "title" or "titles")||!(file.Contains("code")||file.Any(t=>t is "season" or "season:00")&&file.Any(t=>t is "episode" or "episode:00")))throw new ArgumentException("Filename needs {title} or {titles}, plus {code} or season and episode tokens.");
 }
 private static string Remove(string value,RemovalTarget target)=>target switch {
  RemovalTarget.Quotes=>new string(value.Where(c=>!QuoteCharacters.Contains(c)).ToArray()),
  RemovalTarget.Punctuation=>Regex.Replace(value,@"\p{P}",""),
  RemovalTarget.Symbols=>Regex.Replace(value,@"\p{S}",""),
  RemovalTarget.Whitespace=>Regex.Replace(value,@"\s+",""),
  RemovalTarget.Diacritics=>new string(value.Normalize(NormalizationForm.FormD).Where(c=>CharUnicodeInfo.GetUnicodeCategory(c)!=UnicodeCategory.NonSpacingMark).ToArray()).Normalize(NormalizationForm.FormC),
  _=>throw new ArgumentException("Unknown removal selector.")
 };
 private static string Transform(string value,string name,string[] a)=>name switch {
  "removePunctuation"=>Remove(value,RemovalTarget.Punctuation),
  "replacePunctuation"=>Regex.Replace(value,@"[\p{P}\p{S}]+",_=>a[0]),
  "replaceWhitespace"=>Regex.Replace(value.Trim(),@"\s+",_=>a[0]),
  "removeDiacritics"=>Remove(value,RemovalTarget.Diacritics),
  "lowercase"=>value.ToLowerInvariant(),"uppercase"=>value.ToUpperInvariant(),"trim"=>value.Trim(),
  "replace"=>value.Replace(a[0],a[1],StringComparison.Ordinal),
  "prefix"=>value.Length==0?"":a[0]+value,"suffix"=>value.Length==0?"":value+a[0],
  _=>throw new ArgumentException("Unknown naming function.")
 };
 public static (string Folder,string File) Render(string template,string series,IReadOnlyList<Episode> episodes,int part=0,int parts=0,SeriesIdentity? identity=null) {
  Validate(template);var first=episodes[0];var components=Components(template);
  var code=first.Code+(episodes.Count==2?$"-E{episodes[1].Number:00}":"");
  string Token(Match m) {
   if(m.Value=="{{")return "{";if(m.Value=="}}")return "}";var expression=Parse(m.Groups[1].Value);var token=expression.Token;
   if(token is "provider" or "id"){if(identity==null)throw new ArgumentException("Select a metadata identity for provider and ID tokens.");identity.Validate();}
   var values=token=="titles"?episodes.Select(e=>e.Title).ToList():new List<string>{token switch {
    "series"=>series,"title"=>string.Join(" and ",episodes.Select(e=>e.Title)),"code"=>code,
    "season"=>first.Season.ToString(),"season:00"=>first.Season.ToString("00"),"episode"=>first.Number.ToString()+(episodes.Count==2?$"-E{episodes[1].Number:00}":""),"episode:00"=>first.Number.ToString("00")+(episodes.Count==2?$"-E{episodes[1].Number:00}":""),
    "provider"=>identity!.Provider,"id"=>identity!.Id,"part"=>parts>0?part.ToString():"","parts"=>parts>0?parts.ToString():"","partSuffix"=>parts>0?$" (Part {part} of {parts})":"",_=>""
   }};
   foreach(var op in expression.Operations)values=op.Name=="join"?[string.Join(op.Args[0],values)]:values.Select(v=>op.Name=="remove"?(op.Target is {} target?Remove(v,target):v.Replace(op.Args[0],"",StringComparison.Ordinal)):Transform(v,op.Name,op.Args)).ToList();
   return string.Join(" and ",values);
  }
  var rendered=components.Select(c=>PreviewPlanner.SafeName(Regex.Replace(c,Pattern,Token))).ToArray();
  if(parts>0&&!Regex.Matches(components[^1],Pattern).Any(m=>m.Groups[1].Success&&Parse(m.Groups[1].Value).Token is "part" or "parts" or "partSuffix"))rendered[^1]=PreviewPlanner.SafeName(rendered[^1]+$" (Part {part} of {parts})");
  return (Path.Combine(rendered[..^1]),rendered[^1]);
 }
}
