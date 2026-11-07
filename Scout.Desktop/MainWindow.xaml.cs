using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.Json;
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
    private ScoutPause? pauseRequest;
    public MainWindow() {
        InitializeComponent();outputPath=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),"Scout");
        LoadSettings();workspaces.ReadAll(true);RefreshHistory();ShowPage("Session");Closing+=OnClosing;
    }
    private string currentPage="Session";
    private void ShowPage(string name) {
        if(name=="Settings"){NamingChanged(this,new RoutedEventArgs());RefreshManagedRuntime();}
        GuideStatus.Text=ScoutStorage.Exists(catalogPath)?"Saved with this Scout.":"No episode guide saved yet. Select a catalog show and download its guide, or import a JSON guide.";
        if(name=="Session"&&workspace!=null)name=operation!=null&&!settingsDownload?"Activity":"Library";
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
        if(name=="Preview"&&operation==null)RestorePreview();
        currentPage=name;RefreshScoutActions();
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
        FileCount.Text=report.Array("files").Count.ToString();EpisodeCount.Text=groups.SelectMany(g=>g.Episodes).Select(e=>e.Id).Distinct().Count().ToString();
        DuplicateCount.Text=groups.Sum(g=>g.Copies-1).ToString();ReviewCount.Text=groups.Count(g=>g.Category=="Unresolved").ToString();
        var paths=groups.SelectMany(g=>g.Rows).Select(r=>r.Text("path")).ToList();
        if(paths.Count>0) {
            var root=Path.GetDirectoryName(Path.GetFullPath(paths[0]))!;
            while(paths.Any(p=>!PreviewPlanner.Within(p,root)))root=Path.GetDirectoryName(root)??throw new InvalidDataException("Files do not share a library root.");
            if(Path.GetFileName(root).StartsWith("Season ",StringComparison.OrdinalIgnoreCase)||Path.GetFileName(root).Equals("Specials",StringComparison.OrdinalIgnoreCase))root=Path.GetDirectoryName(root)!;
            LibraryPath.Text=root;SeriesName.Text=Path.GetFileName(root).Split(" {")[0];
        }
        if(workspace!=null){workspace.SelectedFiles=RecordingRelocation.MapSelection(loaded,workspace.SelectedFiles.Concat(loaded.Array("files").Select(r=>r.Text("path"))));LibraryPath.Text=workspace.Library;
            if(loaded["series_identity"]!=null)workspace.SeriesIdentity=loaded["series_identity"]!.Deserialize<SeriesIdentity>(Data.Options);
            if(workspace.SeriesIdentity!=null)SeriesName.Text=workspace.SeriesIdentity.Name;
        }
        outputPath=Path.GetDirectoryName(reportPath)!;catalogPath="";
        var catalog=Path.Combine(outputPath,"catalog.json");if(ScoutStorage.Exists(catalog))catalogPath=catalog;
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
    private void ClearEvidence() {
        Evidence.Document.Blocks.Clear();EvidenceAssessment.Text="Select a recording to review";EvidenceSuggestion.Text="";EvidenceFilename.Text="";EvidenceFilename.ToolTip=null;
    }
    private void ResultSelected(object sender,SelectionChangedEventArgs e) {
        if(Evidence==null)return;ClearEvidence();
        if(ResultsGrid.SelectedItem is not RecordingGroup group)return;
        var review=EvidenceReview.Create(group);EvidenceAssessment.Text=review.Assessment;EvidenceSuggestion.Text=review.Suggestion;
        EvidenceFilename.Text=string.Join("\n",group.Rows.Select(r=>Path.GetFileName(r.Text("path"))));
        EvidenceFilename.ToolTip=string.Join("\n",group.Rows.Select(r=>r.Text("path")));
        foreach(var section in review.Sections) {
            if(section.Heading=="Recording"&&group.Rows.Count==1)continue;
            Evidence.Document.Blocks.Add(new System.Windows.Documents.Paragraph(new System.Windows.Documents.Run(section.Heading)){FontWeight=FontWeights.SemiBold,Foreground=System.Windows.Media.Brushes.LightSteelBlue,Margin=new Thickness(0,16,0,6)});
            Evidence.Document.Blocks.Add(new System.Windows.Documents.Paragraph(new System.Windows.Documents.Run(section.Text)){Margin=new Thickness(0,0,0,10),LineHeight=22});
        }
        Evidence.ScrollToHome();
    }
    private void BrowseLibrary(object sender,RoutedEventArgs e)=>NewWorkspace(sender,e);
    private void BrowseCatalog(object sender,RoutedEventArgs e) {
        if(operation!=null||workspace==null)return;
        if(PickFile("Import episode guide") is not string path)return;
        try {
            Data.Catalog(path);
            if(!EnsureDraftScout())return;
            catalogPath=workspaces.SaveCatalog(workspace!,path);SaveDraft();
            RecordEvent("Episode guide saved for this Scout.");ShowPage("Library");
        }catch(Exception ex){Error(ex);}
    }
    private void BrowseDialogue(object sender,RoutedEventArgs e){
        if(workspace==null||operation!=null)return;
        if(PickFile("Use saved dialogue") is not string path)return;
        try {Data.Read(path);if(!EnsureDraftScout())return;dialoguePath=path;SaveDraft();}catch(Exception ex){Error(ex);}
    }
    private void SetBusy(bool value) {
        RestartRunButton.IsEnabled=!value;DeleteRunButton.IsEnabled=!value;UndoDeleteButton.IsEnabled=!value;HistoryList.IsEnabled=!value;SettingsTabs.IsEnabled=!value;SettingsSaveButton.IsEnabled=!value;LibraryPage.IsEnabled=!value;
        CancelButton.IsEnabled=value;Progress.IsIndeterminate=value;Progress.Visibility=value?Visibility.Visible:Visibility.Collapsed;
        RefreshScoutActions();RefreshMoveSelection();
        ProcessingActionText.Text=pauseRequest==null?"Cancel processing":"Pause Scout";
        System.Windows.Automation.AutomationProperties.SetName(CancelButton,ProcessingActionText.Text);
        if(value||workspace?.Status!="Paused")PauseNotice.Visibility=Visibility.Collapsed;
        if(!value)OpenPendingTaskbarScout();
    }
    private void Log(string line){ActivityLog.AppendText(line+Environment.NewLine);ActivityLog.ScrollToEnd();Status.Text=pauseRequest?.IsRequested==true?"Pause requested — finishing and saving the current file.":line;RecordEvent(line);}
    private async void Identify(object sender,RoutedEventArgs e)=>await IdentifyRunAsync(false);
    private async void RestartRun(object sender,RoutedEventArgs e)=>await IdentifyRunAsync(true);
    private async void ReanalyzeScout(object sender,RoutedEventArgs e)=>await IdentifyRunAsync(true,true);
    private async void ReviewIssues(object sender,RoutedEventArgs e)=>await IdentifyRunAsync(true,false,true);
    private async void IdentifyNewFiles(object sender,RoutedEventArgs e)=>await IdentifyRunAsync(true,false,false,true);
    private async Task IdentifyRunAsync(bool restart,bool reanalyze=false,bool reviewIssues=false,bool newFilesOnly=false) {
        if(operation!=null)return;
        LocalRuntime? runtime=null;DialogueMatcher? matcher=null;string? usageOutput=null;
        try {
            if(restart&&(workspace==null||string.IsNullOrWhiteSpace(workspace.Model)))throw new InvalidOperationException("Resume Scout is available for saved dialogue-identification runs. Use Identify episodes to start a new Scout.");
            var preferences=SaveSettingsCore();
            if(string.IsNullOrWhiteSpace(preferences.Model))throw new ArgumentException("Choose a model ID in Settings.");
            string? key=preferences.Provider is "openai" or "compatible"?settingsStore.ReadKey(preferences.CredentialId):null;
            ConnectionPolicy.Validate(preferences.Provider,preferences.Endpoint,preferences.AllowCloudDialogue,key);
            if(workspace==null)throw new ArgumentException("Create or select a Scout first.");
            if(!ScoutStorage.Exists(catalogPath)&&workspace.SeriesIdentity==null)throw new ArgumentException("Select a show or import an episode guide before identifying episodes.");
            if(!Directory.Exists(LibraryPath.Text)||PreviewPlanner.Within(outputPath,LibraryPath.Text))throw new ArgumentException("The source folder must exist and must be separate from internal Scout storage.");
            if(!restart&&!EnsureDraftScout())return;
            var selectedFiles=restart?workspace.SelectedFiles.ToList():ChooseVideos();if(selectedFiles==null)return;
            if(selectedFiles.Count==0)throw new InvalidOperationException("This Scout has no saved file selection. Use Identify episodes to start a new Scout.");
            if(restart){outputPath=workspaces.DirectoryFor(workspace!);catalogPath=workspace!.Catalog;dialoguePath=workspace.Dialogue;LibraryPath.Text=workspace.Library;}
            workspace!.SelectedFiles=selectedFiles;SaveDraft();workspace.Report=Path.Combine(outputPath,"report.json");workspace.Preview="";previewHtml=null;PreviewGrid.ItemsSource=null;workspace.Status="Running";workspace.Model=preferences.Model;workspace.Provider=preferences.Provider;
            pauseRequest=new();operation=new();SetBusy(true);ShowPage("Activity");var progress=new Progress<string>(Log);var token=operation.Token;
            var guideTarget=ScoutStorage.Exists(catalogPath)?catalogPath:Path.Combine(outputPath,"catalog.json");
            using(var metadataHttp=MetadataHttp()) {
                var identity=workspace.SeriesIdentity;
                if(!ScoutStorage.Exists(guideTarget))Log("Downloading the episode guide for "+identity!.Label+"…");
                await EpisodeGuidePreparation.EnsureAsync(guideTarget,identity,identity==null?null:CatalogClient(metadataHttp,identity.Provider),token);
            }
            catalogPath=workspaces.SaveCatalog(workspace,guideTarget);
            Log(newFilesOnly?"Scanning for added videos; saved results will be retained.":reviewIssues?"Reviewing unresolved files and competing recordings using saved evidence.":reanalyze?$"Reanalyzing {selectedFiles.Count} videos with the current model; reusing unchanged dialogue and fingerprints.":restart?$"Resuming {selectedFiles.Count} selected videos; reusing successful results for unchanged files.":$"Identifying {selectedFiles.Count} selected videos from dialogue.");
            RecordEvent($"Started with {preferences.Provider} / {preferences.Model}.");
            Log($"Model request timeout: {preferences.EffectiveTimeoutSeconds/60.0:0.#} minutes per request. Timeouts are held for retry; they do not cancel the Scout.");
            var root=LibraryPath.Text;var catalog=catalogPath;var output=outputPath;
            var dialogue=string.IsNullOrWhiteSpace(dialoguePath)?null:dialoguePath;
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
            var settings=new MatchSettings(root,catalog,output,preferences.Model,endpoint,preferences.Ffmpeg,preferences.Ffprobe,dialogue,selectedFiles,workspace.SeriesIdentity,restart,reanalyze,reviewIssues,newFilesOnly);
            using var handler=new HttpClientHandler {AllowAutoRedirect=false};
            using var http=new HttpClient(handler){Timeout=TimeSpan.FromSeconds(preferences.EffectiveTimeoutSeconds)};
            matcher=new DialogueMatcher(http,preferences.Inference(key));
            usageOutput=Path.Combine(output,"usage.json");
            var pipeline=new MediaPipeline(matcher);
            var path=await Task.Run(()=>pipeline.RunAsync(settings,progress,token,pauseRequest),token);
            LoadReport(path);ShowPage("Results");Status.Text="Matching complete. Unresolved files are held for review.";
        }catch(ScoutPausedException){
            if(workspace!=null)workspace.Status="Paused";
            PauseNotice.Text="Scout paused. Progress is saved. Choose Resume Scout to continue.";PauseNotice.Visibility=Visibility.Visible;
            Log("Scout paused at a saved checkpoint. Resume Scout will reuse completed results.");
            Status.Text=PauseNotice.Text;
        }catch(OperationCanceledException) when(operation?.IsCancellationRequested==true){if(workspace!=null)workspace.Status="Cancelled";Log("Cancelled by request. Completed results remain saved; videos are untouched.");}
        catch(OperationCanceledException ex){if(workspace!=null)workspace.Status="Needs attention";var failure=new IOException("A background request was interrupted without a user cancellation. Saved progress is retained; choose Resume Scout to retry unfinished files.",ex);RecordEvent(failure.Message);Error(failure);}
        catch(Exception ex){if(workspace!=null){workspace.Status="Needs attention";RecordEvent(ex.Message);}Error(ex);}
        finally{
            if(runtime!=null)await runtime.DisposeAsync();
            if(matcher!=null) {
                var usage=matcher.Usage;UsageText.Text=$"{usage.Requests} requests · {usage.InputTokens:N0} reported input tokens · {usage.OutputTokens:N0} reported output tokens · {usage.Provider}";
                if(usageOutput!=null)try{Data.Save(usageOutput,usage);}catch(Exception ex){Log("Could not save usage summary: "+ex.Message);}
            }
            operation?.Dispose();operation=null;pauseRequest=null;SetBusy(false);RefreshHistory();
        }
    }
    private async void GeneratePreview(object sender,RoutedEventArgs e) {
        if(operation!=null||report==null)return;
        try {
            if(sender is not Button {Content: "Regenerate preview"}&&RestorePreview()){ShowPage("Preview");Status.Text="Saved move preview restored.";return;}
            if(string.IsNullOrWhiteSpace(SeriesName.Text))throw new ArgumentException("Enter a series name on the Library page.");
            var root=LibraryPath.Text;var series=SeriesName.Text;var output=Path.Combine(outputPath,"move-preview");
            if(PreviewPlanner.Within(output,root))throw new ArgumentException("Preview output must be outside the video library.");
            var catalog=string.IsNullOrWhiteSpace(catalogPath)?[]:Data.Catalog(catalogPath);
            var snapshot=report.DeepClone().AsObject();
            var naming=appSettings.Naming;
            operation=new();SetBusy(true);CancelButton.IsEnabled=false;Status.Text="Checking source files and destinations…";
            if(reportPath!=null&&MoveExecutor.HasPending(snapshot)) {
                var recoveryProgress=new Progress<string>(Log);
                await Task.Run(()=>MoveExecutor.RecoverAsync(reportPath,root,recoveryProgress,CancellationToken.None,appSettings.Ffmpeg));
                LoadReport(reportPath);snapshot=report!.DeepClone().AsObject();
            }
            var plan=await Task.Run(()=>PreviewPlanner.Create(snapshot,root,series,catalog,naming:naming,identity:workspace?.SeriesIdentity));
            previewHtml=PreviewPlanner.Write(plan,output);PreviewCache.Save(output,CurrentPreviewSignature(),plan);DisplayPreview(plan);
            if(workspace!=null){workspace.Preview=previewHtml;RecordEvent("Move preview refreshed from saved results and move history.");RefreshHistory();}
            ShowPage("Preview");Status.Text="Preview ready. Select pending files to move or rename.";
        }catch(Exception ex){Error(ex);}
        finally{operation?.Dispose();operation=null;SetBusy(false);}
    }
    private void PreviewSelected(object sender,SelectionChangedEventArgs e) {
        if(PreviewGrid.SelectedItem is PreviewRow selected) { var item=selected.Item; PreviewDetails.Text=item.Source+"\n→ "+(item.Destination??"Leave in place")+"\n"+item.Change+": "+item.Reason+"\n"+string.Join(" ",item.BlockedReasons.Concat(item.Notes))+"\n"+item.FeatureDescription+"\n"+item.LastMoveError; }
    }
    private void OpenPreview(object sender,RoutedEventArgs e) {
        if(RestorePreview()&&previewHtml!=null)try{LaunchPreviewBrowser(ScoutStorage.ExportPreview(previewHtml));}catch(Exception ex){Error(ex);}
    }
    private void Cancel(object sender,RoutedEventArgs e) {
        if(operation==null)return;
        if(pauseRequest==null){operation.Cancel();return;}
        pauseRequest.Request();CancelButton.IsEnabled=false;
        PauseNotice.Text="Pause requested. Scout will finish and save the current file (or verification step), then pause. If no file has started, it will pause before the next one.";
        PauseNotice.Visibility=Visibility.Visible;Log("Pause requested — finishing the current file before stopping.");
    }
    private void OnClosing(object? sender,CancelEventArgs e) {
        if(operation==null)SaveDraft();
        if(operation!=null){e.Cancel=true;MessageBox.Show(this,pauseRequest==null?"Cancel processing and let it stop before closing.":"Choose Pause Scout and wait for the current file to finish before closing.","Processing is running");}
    }
    private void Error(Exception ex){
        Status.Text="Needs attention";
        var message=ex is UnauthorizedAccessException?"Scout cannot access a required folder. Check the source drive and app-data permissions. "+ex.Message:ex.Message;
        try {
            var directory=Environment.GetEnvironmentVariable("SCOUT_SETTINGS")??Path.Combine(SettingsStore.DefaultDirectory,"UI-Trial");
            var log=Path.Combine(directory,"errors.log");ScoutStorage.WriteText(log,(ScoutStorage.Exists(log)?ScoutStorage.ReadText(log):"")+DateTimeOffset.Now+"\n"+ex+"\n\n");
        }catch(IOException){}catch(UnauthorizedAccessException){}catch(Microsoft.Data.Sqlite.SqliteException){}
        MessageBox.Show(this,message,"Scout",MessageBoxButton.OK,MessageBoxImage.Warning);
    }
}

public sealed class PreviewRow(PlanItem item,string source,string destination,Action changed):INotifyPropertyChanged {
    public PlanItem Item=>item;public string Source=>source;public string Destination=>destination;
    public string Category=>item.Category;public string Action=>item.Action;public string Change=>item.Change;
    public string Progress=>item.Action=="completed"?"Completed":item.Action=="hold"?"Held":item.Action=="unchanged"?"Unchanged":item.LastMoveError!=null?"Failed - retry":"Pending";
    public bool CanApply=>item.Action=="rename/move"&&item.BlockedReasons.Count==0;
    private bool selected;
    public bool Selected {get=>selected;set {if(selected==value)return;selected=value&&CanApply;PropertyChanged?.Invoke(this,new(nameof(Selected)));changed();}}
    public event PropertyChangedEventHandler? PropertyChanged;
}
