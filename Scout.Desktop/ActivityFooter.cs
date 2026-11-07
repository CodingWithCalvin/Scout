using System.IO;
using System.Windows;
using Scout.Core;
namespace Scout.Desktop;
public partial class MainWindow {
 private string nextStep="new";
 private void RefreshScoutActions() {
  if(NextStepButton==null||CancelButton==null)return;
  bool busy=operation!=null;
  bool saved=workspace!=null&&!string.IsNullOrWhiteSpace(workspace.Report)&&ScoutStorage.Exists(workspace.Report);
  bool ai=workspace!=null&&workspace.SelectedFiles.Count>0&&!string.IsNullOrWhiteSpace(workspace.Model);
  bool interrupted=workspace?.Status is "Paused" or "Interrupted" or "Cancelled" or "Needs attention" or "Running";
  bool issues=false;
  if(saved&&!busy) {
   try {issues=IssueReview.Select(Data.Read(workspace!.Report),checkSources:false).Count>0;}
   catch(Exception){issues=true;}
  }
  bool resume=ai&&interrupted;
  bool review=ai&&saved&&issues;
  bool reanalyze=ai&&saved;
  (nextStep,string label)=currentPage=="Settings"?("settings","Save settings"):
   workspace==null?("new","New Scout"):
   resume?("resume","Resume Scout"):
   workspace.Status=="Draft"?(workspace.SeriesIdentity==null&&!ScoutStorage.Exists(catalogPath)?("discover","Identify series"):("identify","Identify episodes")):
   review?("review","Review issues"):
   saved?(string.IsNullOrWhiteSpace(workspace.Preview)?("preview","Generate move preview"):
     workspace.Status=="Finished"?("new","New Scout"):("finish","Finish Scout")):("setup","Overview and setup");
  IdentifyButton.Visibility=!busy&&workspace?.Status=="Draft"&&(workspace.SeriesIdentity!=null||ScoutStorage.Exists(catalogPath))&&nextStep!="identify"?Visibility.Visible:Visibility.Collapsed;
  LocateMovedFilesButton.Visibility=saved?Visibility.Visible:Visibility.Collapsed;LocateMovedFilesButton.IsEnabled=!busy;
  DiscoverSeriesButton.IsEnabled=!busy&&workspace?.Status=="Draft";
  ShowProvider.IsEnabled=!busy&&workspace?.Status=="Draft";
  PreviewButton.Visibility=!busy&&saved&&nextStep!="preview"?Visibility.Visible:Visibility.Collapsed;
  NextStepButton.Content=label;NextStepButton.IsEnabled=!busy;
  System.Windows.Automation.AutomationProperties.SetName(NextStepButton,label);
  NextStepButton.Visibility=busy?Visibility.Collapsed:Visibility.Visible;
  CancelButton.Visibility=busy?Visibility.Visible:Visibility.Collapsed;
  RestartRunButton.Visibility=!busy&&resume&&nextStep!="resume"?Visibility.Visible:Visibility.Collapsed;
  ReviewIssuesButton.Visibility=!busy&&review&&nextStep!="review"?Visibility.Visible:Visibility.Collapsed;
  IdentifyNewFilesButton.Visibility=!busy&&reanalyze&&!interrupted?Visibility.Visible:Visibility.Collapsed;
  ReanalyzeButton.Visibility=!busy&&reanalyze?Visibility.Visible:Visibility.Collapsed;
  RestartRunButton.IsEnabled=ReviewIssuesButton.IsEnabled=ReanalyzeButton.IsEnabled=!busy;
 }
 private void NextStep(object sender,RoutedEventArgs e) {
  if(operation!=null)return;
  switch(nextStep) {
   case "new":NewWorkspace(sender,e);break;
   case "settings":SaveSettings(sender,e);break;
   case "resume":RestartRun(sender,e);break;
   case "review":ReviewIssues(sender,e);break;
   case "identify":Identify(sender,e);break;
   case "discover":DiscoverShow(sender,e);break;
   case "preview":GeneratePreview(sender,e);break;
   case "finish":FinishWorkspace(sender,e);break;
   default:ShowPage("Library");break;
  }
 }
}
