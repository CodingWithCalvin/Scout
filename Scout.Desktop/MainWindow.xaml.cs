using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Scout.Core;
using Microsoft.Win32;
namespace Scout.Desktop;
public partial class MainWindow : Window
{
    private string catalogPath="",outputPath="",dialoguePath="";
    private List<RecordingGroup> groups=[];
    private JsonObject? report;
    private string? reportPath,previewHtml;
    private CancellationTokenSource? operation;
    public MainWindow() {
        InitializeComponent();outputPath=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),"Scout");
        LoadSettings();workspaces.ReadAll(true);RefreshHistory();ShowPage("Session");Closing+=OnClosing;
    }
    private void ShowPage(string name) {
        GuideStatus.Text=File.Exists(catalogPath)?"Saved with this run. New runs reuse a private copy of this guide.":"No episode guide saved yet. Import a guide to begin; automatic guide fetching is not available yet.";
        if(name=="Session"&&workspace!=null)name="Library";
        foreach(var item in new[]{(NavLibrary,"Library"),(NavResults,"Results"),(NavPreview,"Preview"),(NavActivity,"Activity")})
            item.Item1.Style=(Style)FindResource(item.Item2==name?"ScoutTabActive":"ScoutTab");
        NavSettings.Style=(Style)FindResource(name=="Settings"?"NavActive":"NavButton");
        ScoutHeader.Visibility=name=="Settings"?Visibility.Collapsed:Visibility.Visible;
        ScoutTabs.Visibility=ScoutRunControls.Visibility=workspace==null?Visibility.Collapsed:Visibility.Visible;
        ScoutWelcome.Visibility=workspace==null?Visibility.Visible:Visibility.Collapsed;
        SessionPage.Visibility=name=="Session"?Visibility.Visible:Visibility.Collapsed;
        SettingsPage.Visibility=name=="Settings"?Visibility.Visible:Visibility.Collapsed;
        LibraryPage.Visibility=name=="Library"?Visibility.Visible:Visibility.Collapsed;
        ResultsPage.Visibility=name=="Results"?Visibility.Visible:Visibility.Collapsed;
        PreviewPage.Visibility=name=="Preview"?Visibility.Visible:Visibility.Collapsed;
        ActivityPage.Visibility=name=="Activity"?Visibility.Visible:Visibility.Collapsed;
    }
    private void Navigate(object sender,RoutedEventArgs e)=>ShowPage((string)((Button)sender).Tag);
    private string? PickFile(string title) {
        var dialog=new OpenFileDialog {Title=title,Filter="JSON files (*.json)|*.json"};
        return dialog.ShowDialog(this)==true?dialog.FileName:null;
    }
    private string? PickFolder(string title) {
        var dialog=new OpenFolderDialog {Title=title};
        return dialog.ShowDialog(this)==true?dialog.FolderName:null;
    }
    private void OpenReport(object sender,RoutedEventArgs e) {
        if(operation!=null)return;
        if(PickFile("Open Scout report") is string path) {
            try{LoadReport(path);}catch(Exception ex){Error(ex);}
        }
    }
    public void LoadReport(string path) {
        var loaded=Data.Read(path);var resolved=CollectionResolver.Resolve(loaded);
        if(!openingWorkspace&&operation==null){ImportWorkspace(path);path=workspace!.Report;}
        report=loaded;groups=resolved;reportPath=Path.GetFullPath(path);previewHtml=null;PreviewGrid.ItemsSource=null;
        PreviewSummary.Text="Generate a preview from these results.";
        FileCount.Text=report.Array("files").Count.ToString();EpisodeCount.Text=groups.Count(g=>g.Episode!=null).ToString();
        DuplicateCount.Text=groups.Sum(g=>g.Copies-1).ToString();ReviewCount.Text=groups.Count(g=>g.Category=="Unresolved").ToString();
        var paths=groups.SelectMany(g=>g.Rows).Select(r=>r.Text("path")).ToList();
        if(paths.Count>0) {
            var root=Path.GetDirectoryName(Path.GetFullPath(paths[0]))!;
            while(paths.Any(p=>!PreviewPlanner.Within(p,root)))root=Path.GetDirectoryName(root)??throw new InvalidDataException("Files do not share a library root.");
            if(Path.GetFileName(root).StartsWith("Season ",StringComparison.OrdinalIgnoreCase)||Path.GetFileName(root).Equals("Specials",StringComparison.OrdinalIgnoreCase))root=Path.GetDirectoryName(root)!;
            LibraryPath.Text=root;SeriesName.Text=Path.GetFileName(root).Split(" {")[0];
        }
        if(workspace!=null)LibraryPath.Text=workspace.Library;
        outputPath=Path.GetDirectoryName(reportPath)!;catalogPath="";
        var catalog=Path.Combine(outputPath,"catalog.json");if(File.Exists(catalog))catalogPath=catalog;
        ResultNote.Text=$"{groups.Count(g=>g.Feature)} non-episode feature(s) · select a row for dialogue evidence";
        if(!openingWorkspace&&workspace!=null) {
            workspace.Report=Path.GetFullPath(path);workspace.Name=SeriesName.Text;workspace.Library=LibraryPath.Text;workspace.Catalog=catalogPath;workspace.Status="Ready for review";
            RecordEvent($"{FileCount.Text} files · {EpisodeCount.Text} episode assignments · {ReviewCount.Text} unresolved groups. Results ready for review.");RefreshHistory();
        }
        FilterRows();ShowPage("Session");Status.Text="Loaded "+SeriesName.Text+" workspace";
    }
    private void FilterRows() {
        if(ResultsGrid==null||Search==null||Filter==null)return;
        string query=Search.Text.Trim();int filter=Filter.SelectedIndex;
        ResultsGrid.ItemsSource=groups.Where(g=>(filter switch{1=>g.Episode!=null,2=>g.Duplicate,3=>g.Feature,4=>g.Category=="Unresolved",_=>true})&&
            (query.Length==0||(g.Code+" "+g.Title+" "+string.Join(" ",g.Rows.Select(r=>r.Text("path")))).Contains(query,StringComparison.OrdinalIgnoreCase))).ToList();
    }
    private void FilterChanged(object sender,SelectionChangedEventArgs e)=>FilterRows();
    private void SearchChanged(object sender,TextChangedEventArgs e)=>FilterRows();
    private void ResultSelected(object sender,SelectionChangedEventArgs e){if(Evidence!=null)Evidence.Text=(ResultsGrid.SelectedItem as RecordingGroup)?.Detail??"";}
    private void BrowseLibrary(object sender,RoutedEventArgs e)=>NewWorkspace(sender,e);
    private void BrowseCatalog(object sender,RoutedEventArgs e) {
        if(operation!=null||workspace==null)return;
        if(PickFile("Import episode guide") is not string path)return;
        try {
            Data.Catalog(path);
            if(workspace.Status!="Draft")NewRun(null,null);
            catalogPath=workspaces.SaveCatalog(workspace!,path);SaveDraft();
            RecordEvent("Episode guide saved for this run.");ShowPage("Library");
        }catch(Exception ex){Error(ex);}
    }
    private void BrowseDialogue(object sender,RoutedEventArgs e){
        if(workspace==null||operation!=null)return;
        if(PickFile("Use saved dialogue") is not string path)return;
        try {Data.Read(path);if(workspace.Status!="Draft")NewRun(null,null);dialoguePath=path;SaveDraft();}catch(Exception ex){Error(ex);}
    }
    private void SetBusy(bool value) {
        DeleteRunButton.IsEnabled=!value;UndoDeleteButton.IsEnabled=!value;HistoryList.IsEnabled=!value;RunHistory.IsEnabled=!value;SettingsPage.IsEnabled=!value;LibraryPage.IsEnabled=!value;
        IdentifyButton.IsEnabled=!value;PreviewButton.IsEnabled=!value;CancelButton.IsEnabled=value;Progress.IsIndeterminate=value;
    }
    private void Log(string line){ActivityLog.AppendText(line+Environment.NewLine);ActivityLog.ScrollToEnd();Status.Text=line;RecordEvent(line);}
    private async void Identify(object sender,RoutedEventArgs e) {
        if(operation!=null)return;
        LocalRuntime? runtime=null;DialogueMatcher? matcher=null;string? usageOutput=null;
        try {
            var preferences=SaveSettingsCore();
            if(string.IsNullOrWhiteSpace(preferences.Model))throw new ArgumentException("Choose a model ID in Settings.");
            string? key=preferences.Provider is "openai" or "compatible"?settingsStore.ReadKey(preferences.CredentialId):null;
            ConnectionPolicy.Validate(preferences.Provider,preferences.Endpoint,preferences.AllowCloudDialogue,key);
            if(!File.Exists(catalogPath))throw new ArgumentException("This Scout needs an episode guide before its first run. Import one in Settings > Usage and tools > Advanced run data.");
            Data.Catalog(catalogPath);
            if(!Directory.Exists(LibraryPath.Text)||PreviewPlanner.Within(outputPath,LibraryPath.Text))throw new ArgumentException("The source folder must exist and must be separate from internal Scout storage.");
            if(workspace==null||workspace.Status!="Draft") {
                var series=SeriesName.Text;var library=LibraryPath.Text;var guide=catalogPath;var cached=dialoguePath;
                CreateRun(library);SeriesName.Text=series;LibraryPath.Text=library;catalogPath=guide;dialoguePath=cached;
            }
            SaveDraft();workspace!.Status="Running";workspace.Model=preferences.Model;workspace.Provider=preferences.Provider;
            catalogPath=workspaces.SaveCatalog(workspace,catalogPath);
            RecordEvent($"Started with {preferences.Provider} / {preferences.Model}.");
            var root=LibraryPath.Text;var catalog=catalogPath;var output=outputPath;
            var dialogue=string.IsNullOrWhiteSpace(dialoguePath)?null:dialoguePath;
            operation=new();SetBusy(true);ShowPage("Activity");var progress=new Progress<string>(Log);var token=operation.Token;
            if(preferences.Provider=="managed") {
                if(!File.Exists(preferences.ModelFile)&&preferences.AutoDownloadMissing) {
                    var profile=ModelDownloads.Profiles.Single(p=>p.Id==preferences.DownloadProfile);
                    using var downloadHttp=new HttpClient{Timeout=TimeSpan.FromHours(12)};
                    preferences.ModelFile=await ModelDownloads.DownloadAsync(profile,preferences,downloadHttp,progress,token);
                    ModelFileEntry.Text=preferences.ModelFile;
                }
                runtime=await LocalRuntime.StartAsync(preferences,progress,token);
            }
            string endpoint=preferences.Provider=="managed"?$"http://127.0.0.1:{new Uri(preferences.LocalEndpoint).Port}/v1/chat/completions":preferences.Endpoint;
            var settings=new MatchSettings(root,catalog,output,preferences.Model,endpoint,preferences.Ffmpeg,preferences.Ffprobe,dialogue);
            using var handler=new HttpClientHandler {AllowAutoRedirect=false};
            using var http=new HttpClient(handler){Timeout=TimeSpan.FromSeconds(preferences.RequestTimeoutSeconds)};
            matcher=new DialogueMatcher(http,new InferenceOptions{Provider=preferences.Provider,ApiKey=key,AllowCloudDialogue=preferences.AllowCloudDialogue,MaxOutputTokens=preferences.MaxOutputTokens,MaxRequests=preferences.MaxRequestsPerRun});
            usageOutput=Path.Combine(output,"usage.json");
            var pipeline=new MediaPipeline(matcher);
            var path=await Task.Run(()=>pipeline.RunAsync(settings,progress,token),token);
            LoadReport(path);Status.Text="Matching complete. Unresolved files are held for review.";
        }catch(OperationCanceledException){if(workspace!=null)workspace.Status="Cancelled";Log("Cancelled. Completed results remain saved; videos are untouched.");}
        catch(Exception ex){if(workspace!=null){workspace.Status="Needs attention";RecordEvent(ex.Message);}Error(ex);}
        finally{
            if(runtime!=null)await runtime.DisposeAsync();
            if(matcher!=null) {
                var usage=matcher.Usage;UsageText.Text=$"{usage.Requests} requests · {usage.InputTokens:N0} reported input tokens · {usage.OutputTokens:N0} reported output tokens · {usage.Provider}";
                if(usageOutput!=null)try{Data.Save(usageOutput,usage);}catch(Exception ex){Log("Could not save usage summary: "+ex.Message);}
            }
            operation?.Dispose();operation=null;SetBusy(false);RefreshHistory();
        }
    }
    private async void GeneratePreview(object sender,RoutedEventArgs e) {
        if(operation!=null||report==null)return;
        try {
            if(string.IsNullOrWhiteSpace(SeriesName.Text))throw new ArgumentException("Enter a series name on the Library page.");
            var root=LibraryPath.Text;var series=SeriesName.Text;var output=Path.Combine(outputPath,"move-preview");
            if(PreviewPlanner.Within(output,root))throw new ArgumentException("Preview output must be outside the video library.");
            var catalog=string.IsNullOrWhiteSpace(catalogPath)?[]:Data.Catalog(catalogPath);
            var snapshot=report.DeepClone().AsObject();
            operation=new();SetBusy(true);CancelButton.IsEnabled=false;Status.Text="Checking source files and destinations…";
            var plan=await Task.Run(()=>PreviewPlanner.Create(snapshot,root,series,catalog));
            previewHtml=PreviewPlanner.Write(plan,output);PreviewGrid.ItemsSource=plan.Items.Select(i=>new PreviewRow(i,Path.GetRelativePath(plan.LibraryRoot,i.Source),i.Destination is null?"Leave in place":Path.GetRelativePath(plan.LibraryRoot,i.Destination))).ToList();
            PreviewSummary.Text=string.Join("   /   ",plan.Summary.Select(p=>$"{p.Value} {p.Key}"))+"   ·   No files changed";
            PreviewDetails.Text=string.Join(Environment.NewLine,plan.NotIdentified.Select(ep=>"Not identified: "+ep.Code+" — "+ep.Title));
            if(workspace!=null){workspace.Preview=previewHtml;RecordEvent("Move preview generated. No media files changed.");RefreshHistory();}
            ShowPage("Preview");Status.Text="Preview ready. No media files or folders changed.";
        }catch(Exception ex){Error(ex);}
        finally{operation?.Dispose();operation=null;SetBusy(false);}
    }
    private void PreviewSelected(object sender,SelectionChangedEventArgs e) {
        if(PreviewGrid.SelectedItem is PreviewRow selected) { var item=selected.Item; PreviewDetails.Text=item.Source+"\n→ "+(item.Destination??"Leave in place")+"\n"+string.Join(" ",item.BlockedReasons.Concat(item.Notes))+"\n"+item.FeatureDescription; }
    }
    private void OpenPreview(object sender,RoutedEventArgs e) {
        if(previewHtml!=null)try{Process.Start(new ProcessStartInfo(previewHtml){UseShellExecute=true});}catch(Exception ex){Error(ex);}
    }
    private void Cancel(object sender,RoutedEventArgs e)=>operation?.Cancel();
    private void OnClosing(object? sender,CancelEventArgs e) {
        if(operation==null)SaveDraft();
        if(operation!=null){e.Cancel=true;MessageBox.Show(this,"Cancel processing and let it stop before closing.","Processing is running");}
    }
    private void Error(Exception ex){Status.Text="Needs attention";MessageBox.Show(this,ex.Message,"Scout",MessageBoxButton.OK,MessageBoxImage.Warning);}
}

public sealed record PreviewRow(PlanItem Item,string Source,string Destination) { public string Category=>Item.Category;public string Action=>Item.Action; }
