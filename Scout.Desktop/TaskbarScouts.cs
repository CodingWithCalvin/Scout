using System.IO;
using System.Windows;
using System.Windows.Shell;
using Scout.Core;
namespace Scout.Desktop;
public partial class MainWindow {
 private string? pendingTaskbarScout;
 private string? taskbarStatus;
 private void RefreshTaskbarScouts(IReadOnlyList<RunWorkspace> history) {
  try {
   var exe=Environment.ProcessPath;
   if(string.IsNullOrEmpty(exe)||!Path.GetFileName(exe).Equals("Scout.exe",StringComparison.OrdinalIgnoreCase))return;
   var list=new JumpList {ShowRecentCategory=false,ShowFrequentCategory=false};
   var review=history.Where(s=>s.Status=="Ready for review"&&ScoutStorage.Exists(s.Report)).Take(6).ToList();
   var ids=review.Select(s=>s.Id).ToHashSet();
   var recent=history.Where(s=>!ids.Contains(s.Id)).Take(4);
   foreach(var (scout,category) in review.Select(s=>(s,"Ready for review")).Concat(recent.Select(s=>(s,"Recent Scouts")))) {
    list.JumpItems.Add(new JumpTask {Title=scout.Name+" · "+scout.Updated.ToLocalTime().ToString("MMM d, HH:mm"),Description=scout.Status+" — "+scout.Library,
     CustomCategory=category,ApplicationPath=exe,Arguments="--scout "+scout.Id,WorkingDirectory=Path.GetDirectoryName(exe),IconResourcePath=exe,IconResourceIndex=0});
   }
   JumpList.SetJumpList(Application.Current,list);
  }catch(Exception ex) when(ex is System.Runtime.InteropServices.COMException or InvalidOperationException or UnauthorizedAccessException or IOException) {
   System.Diagnostics.Debug.WriteLine("Jump List unavailable: "+ex.GetType().Name);
  }
 }
 public void OpenTaskbarScout(string id) {
  if(WindowState==WindowState.Minimized)WindowState=WindowState.Normal;
  Show();Activate();
  if(string.IsNullOrEmpty(id))return;
  if(id.Length!=32||!id.All(Uri.IsHexDigit)){Status.Text="Invalid Scout shortcut.";return;}
  if(operation!=null) {pendingTaskbarScout=id;Status.Text="Scout will open after the current activity finishes or pauses.";return;}
  var chosen=workspaces.ReadAll().FirstOrDefault(s=>s.Id.Equals(id,StringComparison.OrdinalIgnoreCase));
  if(chosen==null){Status.Text="This Scout is no longer in history.";RefreshHistory();return;}
  OpenSavedRun(chosen);
  if(report!=null)ShowPage("Results");
 }
 private void OpenPendingTaskbarScout() {
  if(operation!=null||pendingTaskbarScout==null)return;
  var id=pendingTaskbarScout;pendingTaskbarScout=null;
  Dispatcher.BeginInvoke(new Action(()=>OpenTaskbarScout(id)));
 }
}
