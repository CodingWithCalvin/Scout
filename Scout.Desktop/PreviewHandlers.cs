using System.IO;
using System.Windows;
using System.Diagnostics;
using Microsoft.Win32;
using Scout.Core;
namespace Scout.Desktop;
public partial class MainWindow {
 private MovePlan? currentMovePlan;
 private string? displayedPreviewReport;
 private void RefreshMoveSelection() {
  if(ApplyMovesButton==null)return;
  int selected=(PreviewGrid.ItemsSource as IEnumerable<PreviewRow>)?.Count(r=>r.Selected&&r.CanApply)??0;
  ApplyMovesButton.Content=$"Move / rename selected ({selected})";
  ApplyMovesButton.IsEnabled=operation==null&&selected>0&&report!=null&&!MoveExecutor.HasPending(report);
  PreviewGrid.IsEnabled=operation==null;
  SelectPendingMovesButton.IsEnabled=ClearMoveSelectionButton.IsEnabled=operation==null&&currentMovePlan!=null;
 }
 private void SelectPendingMoves(object sender,RoutedEventArgs e) {
  if(operation!=null)return;
  foreach(var row in (PreviewGrid.ItemsSource as IEnumerable<PreviewRow>)??[])row.Selected=row.CanApply;
 }
 private void ClearMoveSelection(object sender,RoutedEventArgs e) {
  if(operation!=null)return;
  foreach(var row in (PreviewGrid.ItemsSource as IEnumerable<PreviewRow>)??[])row.Selected=false;
 }
 private async void ApplyMoves(object sender,RoutedEventArgs e) {
  if(operation!=null||currentMovePlan==null||reportPath==null||workspace==null)return;
  var selected=((PreviewGrid.ItemsSource as IEnumerable<PreviewRow>)??[]).Where(r=>r.Selected&&r.CanApply).Select(r=>r.Item.Source).ToArray();
  if(selected.Length==0)return;
  var savedReport=reportPath;var plan=currentMovePlan;
  try {
   operation=new();SetBusy(true);ProcessingActionText.Text="Stop after current move";
   if(PreviewCache.Read(Path.Combine(outputPath,"move-preview"),CurrentPreviewSignature())==null)
    throw new InvalidOperationException("The preview changed or is stale. Regenerate it before moving selected files.");
   var catalog=string.IsNullOrWhiteSpace(catalogPath)?[]:Data.Catalog(catalogPath);
   var token=operation.Token;
   var progress=new Progress<MoveUpdate>(update=>{
    report=update.Report;DisplayPreview(update.Plan);Status.Text=update.Message;RecordEvent(update.Message);
   });
   await Task.Run(()=>MoveExecutor.ApplyAsync(savedReport,plan,selected,catalog,progress,token));
   RecordEvent("Selected move batch finished. Completed moves and remaining files are saved.");
  }catch(OperationCanceledException){RecordEvent("Move batch stopped. Completed moves are retained; remaining files were not moved.");}
  catch(Exception ex){Error(ex);}
  finally {
   try {
    LoadReport(savedReport);
    var catalog=string.IsNullOrWhiteSpace(catalogPath)?[]:Data.Catalog(catalogPath);
    var refreshed=await Task.Run(()=>PreviewPlanner.Create(report!.DeepClone().AsObject(),plan.LibraryRoot,plan.Series,catalog,checkSources:false,naming:appSettings.Naming,identity:workspace.SeriesIdentity));
    var folder=Path.Combine(outputPath,"move-preview");
    previewHtml=PreviewPlanner.Write(refreshed,folder);PreviewCache.Save(folder,CurrentPreviewSignature(),refreshed);DisplayPreview(refreshed);
    workspace.SelectedFiles=RecordingRelocation.MapSelection(report!,workspace.SelectedFiles);
    workspace.Preview=previewHtml;workspaces.Save(workspace);ShowPage("Preview");
    Status.Text=MoveExecutor.HasPending(report!)?"An interrupted move needs reconciliation. Regenerate the preview to verify its location.":"Move results saved. Completed moves and remaining files are shown.";
   }catch(Exception ex){Error(ex);}
   operation?.Dispose();operation=null;SetBusy(false);RefreshHistory();
  }
 }
 private string CurrentPreviewSignature()=>PreviewCache.Signature(report!,LibraryPath.Text,SeriesName.Text,string.IsNullOrWhiteSpace(catalogPath)?[]:Data.Catalog(catalogPath),appSettings.Naming,workspace?.SeriesIdentity);
 private void DisplayPreview(MovePlan plan) {
  var selected=displayedPreviewReport==reportPath?((PreviewGrid.ItemsSource as IEnumerable<PreviewRow>)??[]).Where(r=>r.Selected).Select(r=>r.Item.Source).ToHashSet(StringComparer.OrdinalIgnoreCase):[];
  currentMovePlan=plan;displayedPreviewReport=reportPath;
  var rows=plan.Items.Select(i=>new PreviewRow(i,Path.GetRelativePath(plan.LibraryRoot,i.Source),i.Destination is null?"Leave in place":Path.GetRelativePath(plan.LibraryRoot,i.Destination),RefreshMoveSelection)).ToList();
  foreach(var row in rows)row.Selected=selected.Contains(row.Item.Source);
  PreviewGrid.ItemsSource=rows;RefreshMoveSelection();
  PreviewSummary.Text=string.Join("   /   ",plan.Items.GroupBy(i=>i.Action=="completed"?"Completed":i.Action=="rename/move"?"Pending":i.Action=="hold"?"Held":"Unchanged").Select(g=>$"{g.Count()} {g.Key}"))+"   ·   Only checked files are moved";
  PreviewDetails.Text=string.Join(Environment.NewLine,plan.NotIdentified.Select(ep=>"Not identified: "+ep.Code+" — "+ep.Title));
 }
 private bool RestorePreview() {
  if(report==null||workspace==null||string.IsNullOrWhiteSpace(outputPath))return false;
  try {
   var folder=Path.Combine(outputPath,"move-preview");var plan=PreviewCache.Read(folder,CurrentPreviewSignature());
   if(plan!=null&&!MoveExecutor.HasPending(report)){previewHtml=Path.Combine(folder,"preview.html");DisplayPreview(plan);return true;}
  }catch(Exception ex) when(ex is IOException or ArgumentException or System.Text.Json.JsonException){System.Diagnostics.Debug.WriteLine(ex.Message);}
  currentMovePlan=null;previewHtml=null;PreviewGrid.ItemsSource=null;RefreshMoveSelection();PreviewDetails.Clear();
  PreviewSummary.Text=MoveExecutor.HasPending(report)?"An interrupted move needs reconciliation. Regenerate the preview to verify its destination.":"Generate a move preview. No current cached preview matches these results, naming settings and file locations.";
  if(!string.IsNullOrEmpty(workspace.Preview)){workspace.Preview="";workspaces.Save(workspace);}
  return false;
 }
 private static void LaunchPreviewBrowser(string html) {
  var progId=Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice","ProgId","")?.ToString()??"";
  var preferred=progId.Contains("Firefox",StringComparison.OrdinalIgnoreCase)?"firefox.exe":progId.Contains("Chrome",StringComparison.OrdinalIgnoreCase)?"chrome.exe":"msedge.exe";
  foreach(var name in new[]{preferred,"msedge.exe","chrome.exe","firefox.exe"}.Distinct()) {
   var sub=@"Software\Microsoft\Windows\CurrentVersion\App Paths\"+name;
   var candidates=new List<string?>{Registry.GetValue(@"HKEY_CURRENT_USER\"+sub,"",null)?.ToString(),Registry.GetValue(@"HKEY_LOCAL_MACHINE\"+sub,"",null)?.ToString()};
   var relative=name=="msedge.exe"?@"Microsoft\Edge\Application\msedge.exe":name=="chrome.exe"?@"Google\Chrome\Application\chrome.exe":@"Mozilla Firefox\firefox.exe";
   candidates.AddRange(new[]{Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)}.Select(root=>Path.Combine(root,relative)));
   var exe=candidates.FirstOrDefault(p=>!string.IsNullOrWhiteSpace(p)&&Path.IsPathFullyQualified(p)&&File.Exists(p));
   if(exe==null)continue;
   var start=new ProcessStartInfo(exe){UseShellExecute=false};start.ArgumentList.Add(new Uri(Path.GetFullPath(html)).AbsoluteUri);Process.Start(start);return;
  }
  throw new InvalidOperationException("No supported browser found. Install Edge, Chrome or Firefox to open the full preview.");
 }
}
