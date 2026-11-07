using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;
using Scout.Core;

namespace Scout.Desktop;

public sealed class NamingTemplateEditor : TextEditor {
 private CompletionWindow? completion;
 public NamingTemplateEditor() {
  FontFamily=new FontFamily("Consolas");FontSize=15;
  Background=Brush("#142030");Foreground=Brush("#E9F1FC");
  BorderBrush=Brush("#425570");BorderThickness=new Thickness(1);Padding=new Thickness(12);
  WordWrap=true;ShowLineNumbers=false;MinHeight=110;MaxHeight=240;
  HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled;VerticalScrollBarVisibility=ScrollBarVisibility.Auto;
  Options.EnableHyperlinks=false;Options.EnableEmailHyperlinks=false;
  Options.ConvertTabsToSpaces=true;
  TextArea.TextView.LineTransformers.Add(new TemplateColorizer());
  TextArea.SelectionBrush=Brush("#3A6190");
  TextArea.TextEntered+=(_,e)=>{
   if(completion==null&&e.Text.Length>0&&(char.IsLetter(e.Text[0])||e.Text is "{" or "." or "("))
    ShowSuggestions();
  };
  TextArea.TextEntering+=(_,e)=>{
   // Completion is explicit (Tab/Enter/click), never an invisible correction.
   if(completion!=null&&e.Text.Any(c=>!char.IsLetterOrDigit(c)&&c!=':'))completion.Close();
  };
  PreviewKeyDown+=(_,e)=>{
   if(e.Key==Key.Space&&Keyboard.Modifiers==ModifierKeys.Control) {
    ShowSuggestions();e.Handled=true;
   } else if(e.Key==Key.Enter&&completion==null) e.Handled=true;
  };
  IsVisibleChanged+=(_,_)=>{if(!IsVisible)completion?.Close();};
  IsEnabledChanged+=(_,_)=>{if(!IsEnabled)completion?.Close();};
  Unloaded+=(_,_)=>completion?.Close();
 }
 public void ShowSuggestions() {
  if(!IsEnabled)return;
  completion?.Close();Focus();TextArea.Focus();
  var context=NamingEditorLanguage.Complete(Text,CaretOffset);
  if(context==null||context.Items.Count==0)return;
  var window=new CompletionWindow(TextArea) {
   StartOffset=context.Start,EndOffset=CaretOffset,Width=360,MaxHeight=260,
   Background=Brush("#172536"),Foreground=Brush("#E9F1FC"),
   BorderBrush=Brush("#78B8FF"),CloseWhenCaretAtBeginning=false
  };
  completion=window;
  window.CompletionList.Background=Brush("#172536");
  window.CompletionList.Foreground=Brush("#E9F1FC");
  window.CompletionList.ListBox.Background=Brush("#172536");
  window.CompletionList.ListBox.Foreground=Brush("#E9F1FC");
  foreach(var item in context.Items)window.CompletionList.CompletionData.Add(new Suggestion(item,context.Kind));
  window.Closed+=(_,_)=>{if(ReferenceEquals(completion,window))completion=null;};
  window.Show();
  window.CompletionList.SelectItem(Text[context.Start..CaretOffset]);
 }
 private static SolidColorBrush Brush(string color) {
  var brush=new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));brush.Freeze();return brush;
 }
 private sealed class Suggestion(NamingSuggestion suggestion,string kind):ICompletionData {
  public ImageSource? Image=>null;
  public string Text=>suggestion.Name;
  public object Content=>suggestion.Insert;
  private readonly TextBlock description=new(){Text=suggestion.Description,TextWrapping=TextWrapping.Wrap,MaxWidth=340,Padding=new Thickness(8)};
  public object Description=>description;
  public double Priority=>0;
  public void Complete(TextArea area,ISegment segment,EventArgs args) {
   int end=segment.EndOffset;
   while(end<area.Document.TextLength&&(char.IsLetterOrDigit(area.Document.GetCharAt(end))||area.Document.GetCharAt(end)==':'))end++;
   bool existingArguments=kind=="function"&&end<area.Document.TextLength&&area.Document.GetCharAt(end)=='(';
   string insert=existingArguments?suggestion.Name:suggestion.Insert;
   int caret=segment.Offset+insert.Length;
   if(end==area.Document.TextLength) {
    if(kind=="selector") {insert+=")";caret++;}
    insert+="}";
   }
   using(area.Document.RunUpdate())area.Document.Replace(segment.Offset,end-segment.Offset,insert);
   if(!existingArguments&&suggestion.SelectionStart>=0) {
    int start=segment.Offset+suggestion.SelectionStart;
    area.Selection=Selection.Create(area,start,start+suggestion.SelectionLength);
    area.Caret.Offset=start+suggestion.SelectionLength;
   } else {area.Selection=Selection.Create(area,caret,caret);area.Caret.Offset=caret;}
  }
 }
 private sealed class TemplateColorizer:DocumentColorizingTransformer {
  private static readonly IReadOnlyDictionary<string,Brush> Colors=new Dictionary<string,Brush> {
   ["token"]=Brush("#80C5FF"),["function"]=Brush("#CFADFF"),["string"]=Brush("#F0C38A"),
   ["selector"]=Brush("#88D5D2"),["punctuation"]=Brush("#A8B8CD"),["unknown"]=Brush("#FF9292")
  };
  protected override void ColorizeLine(DocumentLine line) {
   foreach(var span in NamingEditorLanguage.Highlight(CurrentContext.Document.GetText(line))) {
    var brush=Colors[span.Kind];
    ChangeLinePart(line.Offset+span.Start,line.Offset+span.Start+span.Length,element=>element.TextRunProperties.SetForegroundBrush(brush));
   }
  }
 }
}
