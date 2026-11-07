using System.IO;
using System.Windows;
using Scout.Core;
namespace Scout.Desktop;
public partial class MainWindow {
 private async void LocateMovedFiles(object sender,RoutedEventArgs e) {
  if(operation!=null||workspace==null||!ScoutStorage.Exists(workspace.Report))return;
  try {
   var root=PickFolder("Select the folder now containing this Scout's videos");if(root==null)return;
   if(PreviewPlanner.Within(workspaces.DirectoryFor(workspace),root))throw new InvalidOperationException("Source videos and Scout data must be separate.");
   var snapshot=Data.Read(workspace.Report);operation=new();SetBusy(true);ShowPage("Activity");var token=operation.Token;var progress=new Progress<string>(Log);
   var changes=await Task.Run(()=>RecordingRelocation.ReconnectAsync(snapshot,root,appSettings.Ffmpeg,progress,token),token);
   // Commit paths and evidence together only after verification finishes.
   Data.Save(workspace.Report,snapshot);workspace.Library=root;
   workspace.SelectedFiles=RecordingRelocation.MapSelection(snapshot,workspace.SelectedFiles);workspace.Preview="";workspaces.Save(workspace);
   RecordEvent($"Reconnected {changes.Count} moved recording(s). Saved analysis retained; media unchanged.");
   OpenSavedRun(workspace);ShowPage("Results");
   Status.Text=$"Reconnected {changes.Count} recording(s). Unverified or ambiguous locations remain held.";
  }catch(OperationCanceledException){Status.Text="Location scan cancelled. No path updates committed.";}
  catch(Exception ex){Error(ex);}
  finally{operation?.Dispose();operation=null;SetBusy(false);RefreshHistory();}
 }
}
