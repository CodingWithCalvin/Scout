using System.IO;
using System.Windows;
using System.Windows.Controls;
using Scout.Core;
namespace Scout.Desktop;
public partial class MainWindow {
 private readonly WorkspaceStore workspaces=new(Environment.GetEnvironmentVariable("SCOUT_WORKSPACES")??Environment.GetEnvironmentVariable("EPISCOUT_WORKSPACES")??Path.Combine(SettingsStore.DefaultDirectory,"UI-Trial","Workspaces"));
 private RunWorkspace? workspace;
 private bool openingWorkspace;
 private void RefreshHistory(){
  var history=workspaces.ReadAll().Where(x=>!string.IsNullOrWhiteSpace(x.Library)).ToList();
  openingWorkspace=true;
  try {
   HistoryList.ItemsSource=history;
   HistoryList.SelectedItem=history.FirstOrDefault(x=>x.Id==workspace?.Id);
  } finally {openingWorkspace=false;}
  RefreshTaskbarScouts(history);
  WorkspaceHeading.Text=workspace?.Name??"Your next Scout";
  WorkspaceSource.Text=workspace?.Library??"One Scout. One source folder. One process.";
  RefreshScoutActions();
  RefreshDiscovery();
  NamingChanged(this,new RoutedEventArgs());
 }
 private void RecordEvent(string text) {
  if(workspace==null)return;
  workspace.Events.Add(DateTime.Now.ToString("HH:mm")+"  "+text);workspaces.Save(workspace);
  SessionLog.Text=string.Join("\n\n",workspace.Events);WorkspaceHeading.Text=workspace.Name;WorkspaceSource.Text=workspace.Library;
  var state=workspace.Id+":"+workspace.Status;
  if(taskbarStatus!=state){taskbarStatus=state;RefreshTaskbarScouts(workspaces.ReadAll());}
 }
 private void SaveDraft() {
  if(workspace==null||workspace.Status!="Draft")return;
  workspace.Name=string.IsNullOrWhiteSpace(SeriesName.Text)?"New Scout":SeriesName.Text.Trim();
  workspace.Catalog=catalogPath;workspace.Dialogue=dialoguePath;
  workspaces.Save(workspace);
 }
 private void NewWorkspace(object? sender,RoutedEventArgs? e) {
  if(operation!=null)return;
  try {
   if(PickFolder("Choose the source video folder for this Scout") is not string folder)return;
   CreateRun(folder);
  }catch(Exception ex){Error(ex);}
 }
 private void CreateRun(string folder) {
  PersistSetup();SaveDraft();
  var inherited=workspace!=null&&string.Equals(Path.GetFullPath(workspace.Library),Path.GetFullPath(folder),StringComparison.OrdinalIgnoreCase)?workspace.RestoreSetup() with {}:null;
  var sample=inherited!=null?workspace!.DiscoveryFiles.ToList():new List<string>();
  var created=workspaces.CreateRun(folder);created.Setup=inherited;created.SeriesIdentity=null;created.DiscoveryFiles=[];workspaces.Save(created);workspace=created;
  report=null;reportPath=null;previewHtml=null;groups=[];
  SeriesName.Text="";LibraryPath.Text=workspace.Library;LibraryPath.IsReadOnly=true;catalogPath="";dialoguePath="";
  outputPath=workspaces.DirectoryFor(workspace);
  ResultsGrid.ItemsSource=null;PreviewGrid.ItemsSource=null;ClearEvidence();ActivityLog.Clear();
  FileCount.Text=EpisodeCount.Text=DuplicateCount.Text=ReviewCount.Text="—";
  PreviewSummary.Text="Generate a preview from this Scout's results.";PreviewDetails.Clear();
  SessionLog.Text="";RecordEvent("New Scout created for "+workspace.Library);
  RefreshHistory();ShowPage("Library");
 }
 private bool EnsureDraftScout() {
  if(workspace!=null&&workspace.Status=="Draft"&&report==null)return true;
  Status.Text="This Scout already has a saved process. Use Resume Scout to continue it, or New Scout to start another.";
  return false;
 }
 private void OpenWorkspace(object sender,SelectionChangedEventArgs e) {
  if(openingWorkspace||HistoryList.SelectedItem is not RunWorkspace chosen)return;
  if(operation!=null){Status.Text="Wait for this Scout to stop before switching.";return;}
  OpenSavedRun(chosen);
 }
 private void OpenSavedRun(RunWorkspace chosen) {
  try {
   SaveDraft();workspace=chosen;openingWorkspace=true;
   report=null;reportPath=null;previewHtml=null;groups=[];ResultsGrid.ItemsSource=null;PreviewGrid.ItemsSource=null;ClearEvidence();
   PreviewSummary.Text="Generate a preview from these results.";PreviewDetails.Clear();
   FileCount.Text=EpisodeCount.Text=DuplicateCount.Text=ReviewCount.Text="—";
   if(ScoutStorage.Exists(chosen.Report))LoadReport(chosen.Report);
   SeriesName.Text=chosen.Name;LibraryPath.Text=chosen.Library;catalogPath=chosen.Catalog;dialoguePath=chosen.Dialogue;
   outputPath=workspaces.DirectoryFor(chosen);
   RestorePreview();
   SessionLog.Text=string.Join("\n\n",chosen.Events);ActivityLog.Text=string.Join("\n",chosen.Events);
   WorkspaceHeading.Text=chosen.Name;WorkspaceSource.Text=chosen.Library;ShowPage("Session");
  }catch(Exception ex){Error(ex);}finally{openingWorkspace=false;RefreshHistory();}
 }
 private RunWorkspace? deletedRun;
 private void DeleteRun(object sender,RoutedEventArgs e) {
  if(operation!=null||workspace==null)return;
  if(MessageBox.Show(this,"Delete this Scout from history?\n\nYou can undo this deletion. Saved data is retained for recovery; source videos are never changed.","Delete Scout",MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)!=MessageBoxResult.Yes)return;
  try {
   var removed=workspace;workspaces.DeleteRun(removed);deletedRun=removed;workspace=null;
   report=null;reportPath=null;previewHtml=null;groups=[];catalogPath="";dialoguePath="";outputPath="";
   ResultsGrid.ItemsSource=null;PreviewGrid.ItemsSource=null;ClearEvidence();ActivityLog.Clear();SessionLog.Text="";
   var remaining=workspaces.ReadAll();var next=remaining.FirstOrDefault(x=>string.Equals(x.Library,removed.Library,StringComparison.OrdinalIgnoreCase))??remaining.FirstOrDefault();
   if(next!=null)OpenSavedRun(next);else {RefreshHistory();ShowPage("Session");}
   UndoDeleteButton.Visibility=Visibility.Visible;Status.Text="Scout deleted from history. Source videos unchanged.";
  }catch(Exception ex){Error(ex);}
 }
 private void UndoDelete(object sender,RoutedEventArgs e) {
  if(operation!=null||deletedRun==null)return;
  try {workspaces.RestoreRun(deletedRun);OpenSavedRun(deletedRun);deletedRun=null;UndoDeleteButton.Visibility=Visibility.Collapsed;Status.Text="Scout restored.";}catch(Exception ex){Error(ex);}
 }
 private void FinishWorkspace(object sender,RoutedEventArgs e) {
  if(operation!=null||workspace==null)return;
  SaveDraft();workspace.Status="Finished";RecordEvent("Scout finished. Results and previews remain available in history.");
  RefreshHistory();ShowPage("Session");
 }
 private void ImportWorkspace(string path) {
  Data.Read(path);
  var rows=Data.Read(path).Array("files").Select(x=>x?.AsObject().Text("path")).Where(x=>!string.IsNullOrWhiteSpace(x)).Select(x=>Path.GetFullPath(x!)).ToList();
  if(rows.Count==0)throw new InvalidDataException("A report needs source files to create a Scout.");
  var root=Path.GetDirectoryName(rows[0])!;
  while(rows.Any(x=>!PreviewPlanner.Within(x,root)))root=Path.GetDirectoryName(root)??throw new InvalidDataException("No shared source folder.");
  if(Path.GetFileName(root).StartsWith("Season ",StringComparison.OrdinalIgnoreCase)||Path.GetFileName(root).Equals("Specials",StringComparison.OrdinalIgnoreCase))root=Path.GetDirectoryName(root)!;
  CreateRun(root);
  var directory=workspaces.DirectoryFor(workspace!);
  ScoutStorage.Copy(path,Path.Combine(directory,"report.json"));
  var catalog=Path.Combine(Path.GetDirectoryName(path)!,"catalog.json");
  if(ScoutStorage.Exists(catalog))ScoutStorage.Copy(catalog,Path.Combine(directory,"catalog.json"));
  workspace!.Report=Path.Combine(directory,"report.json");workspace.Status="Ready for review";
 }
 private void MinimizeWindow(object sender,RoutedEventArgs e)=>SystemCommands.MinimizeWindow(this);
 private void ToggleMaximize(object sender,RoutedEventArgs e) {
  if(WindowState==WindowState.Maximized)SystemCommands.RestoreWindow(this);else SystemCommands.MaximizeWindow(this);
 }
 private void CloseWindow(object sender,RoutedEventArgs e)=>Close();
}
