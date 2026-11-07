using System.Text.RegularExpressions;
namespace Scout.Core;

public sealed record NamingSuggestion(string Name,string Insert,string Description,int SelectionStart=-1,int SelectionLength=0);
public sealed record NamingCompletion(int Start,int End,string Kind,IReadOnlyList<NamingSuggestion> Items);
public sealed record NamingSyntaxSpan(int Start,int Length,string Kind);

public static class NamingEditorLanguage {
 public static readonly IReadOnlyList<NamingSuggestion> Tokens=[
  new("series","series","Official series name. Example: My Show"),
  new("code","code","Episode code, including combined ranges. Example: S01E01-E02"),
  new("title","title","Episode title; combined titles joined with and."),
  new("titles","titles","Individual titles. Use .join(\" and \") to combine them."),
  new("season:00","season:00","Two-digit season. Example: 01"),
  new("episode:00","episode:00","Two-digit episode, including a combined range."),
  new("season","season","Season number without padding."),
  new("episode","episode","Episode number without padding."),
  new("provider","provider","Metadata provider. Example: tvdb"),
  new("id","id","Series ID from the selected metadata provider."),
  new("partSuffix","partSuffix","Adds (Part 1 of 2) with a leading space for split recordings; otherwise empty."),
  new("part","part","Split recording number; otherwise empty."),
  new("parts","parts","Total number of split recordings; otherwise empty.")
 ];
 public static readonly IReadOnlyList<NamingSuggestion> Functions=[
  new("remove","remove(punctuation)","Remove a character category or a quoted literal. Example: .remove(punctuation)",7,11),
  new("replaceWhitespace","replaceWhitespace(\".\")","Replace spaces with your separator. Example: Opening Night becomes Opening.Night",19,1),
  new("replacePunctuation","replacePunctuation(\" \")","Replace runs of punctuation and symbols.",20,1),
  new("replace","replace(\"&\", \"and\")","Replace literal text; both arguments are quoted.",9,1),
  new("join","join(\" and \")","Join individual titles; available on titles only.",6,5),
  new("lowercase","lowercase()","Convert text to lowercase."),
  new("uppercase","uppercase()","Convert text to uppercase."),
  new("trim","trim()","Remove leading and trailing whitespace."),
  new("prefix","prefix(\"text\")","Prepend text when the value is not empty.",8,4),
  new("suffix","suffix(\"text\")","Append text when the value is not empty.",8,4),
  new("removePunctuation","removePunctuation()","Shortcut for remove(punctuation)."),
  new("removeDiacritics","removeDiacritics()","Shortcut for remove(diacritics).")
 ];
 public static readonly IReadOnlyList<NamingSuggestion> Selectors=[
  new("punctuation","punctuation","Remove punctuation such as commas, apostrophes and periods."),
  new("symbols","symbols","Remove symbols such as currency and mathematical symbols."),
  new("diacritics","diacritics","Remove combining accents. Example: café becomes cafe."),
  new("whitespace","whitespace","Remove spaces and other whitespace."),
  new("quotes","quotes","Remove straight and curly quotation marks and apostrophes.")
 ];
 public static int FunctionArity(string name)=>name switch {
  "replace"=>2,"lowercase" or "uppercase" or "trim" or "removePunctuation" or "removeDiacritics"=>0,_=>1
 };
 // Tolerant lexical context for an unfinished expression. Strings and escaped braces
 // are never interpreted as completion triggers.
 public static NamingCompletion? Complete(string text,int caret) {
  if(caret<0||caret>text.Length)return null;
  int open=-1,separator=-1,paren=-1,depth=0;bool quoted=false,escape=false;
  for(int i=0;i<caret;i++) {
   char c=text[i];
   if(open<0) {
    if(c=='{'&&i+1<text.Length&&text[i+1]=='{'){i++;continue;}
    if(c=='{'){open=i;separator=paren=-1;depth=0;}
    continue;
   }
   if(escape){escape=false;continue;}
   if(quoted&&c=='\\'){escape=true;continue;}
   if(c=='"'){quoted=!quoted;continue;}
   if(quoted)continue;
   if(c=='}'){open=-1;continue;}
   if(c=='('){paren=i;depth++;}
   else if(c==')')depth--;
   else if(depth==0&&c is '.' or '|')separator=i;
  }
  if(open<0||quoted||depth<0)return null;
  string token=Regex.Match(text[(open+1)..caret],@"^[A-Za-z]+(?::00)?").Value;
  int start;string kind;IReadOnlyList<NamingSuggestion> items;
  if(depth==1&&separator>=0&&text[(separator+1)..paren].Trim()=="remove") {
   start=paren+1;kind="selector";items=Selectors;
  } else if(depth!=0)return null;
  else if(separator>=0) {
   if(token is "code" or "season" or "season:00" or "episode" or "episode:00" or "id" or "part" or "parts")return null;
   start=separator+1;kind="function";items=Functions.Where(f=>f.Name!="join"||token=="titles").ToArray();
  } else {start=open+1;kind="token";items=Tokens;}
  while(start<caret&&char.IsWhiteSpace(text[start]))start++;
  var prefix=text[start..caret];
  if(!Regex.IsMatch(prefix,kind=="token"?@"^[A-Za-z:0-9]*$":@"^[A-Za-z]*$"))return null;
  int end=caret;while(end<text.Length&&(char.IsLetterOrDigit(text[end])||text[end]==':'))end++;
  return new(start,end,kind,items.Where(s=>s.Name.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)).ToArray());
 }
 public static IReadOnlyList<NamingSyntaxSpan> Highlight(string text) {
  var spans=new List<NamingSyntaxSpan>();
  foreach(Match expression in Regex.Matches(text,@"\{\{|\}\}|\{[^{}]*\}?")) {
   if(expression.Value is "{{" or "}}")continue;
   var source=expression.Value;
   foreach(Match part in Regex.Matches(source,"\"(?:\\\\.|[^\"\\\\])*\"?|[A-Za-z]+(?::00)?|[{}().,:|]|[0-9]+")) {
    string value=part.Value,kind="punctuation";
    if(value.StartsWith('"'))kind="string";
    else if(Tokens.Any(t=>t.Name==value))kind="token";
    else if(Functions.Any(f=>f.Name==value))kind="function";
    else if(Selectors.Any(s=>s.Name==value))kind="selector";
    else if(char.IsLetter(value[0]))kind="unknown";
    spans.Add(new(expression.Index+part.Index,part.Length,kind));
   }
  }
  return spans;
 }
}
