using System.IO;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Scout.Core;
namespace Scout.Desktop;

public partial class MainWindow
{
    private string? discoveryRun;
    private bool restoringSetup;
    private void PersistSetup() {
        if(restoringSetup||openingWorkspace||workspace==null||ShowProvider==null)return;
        var setup=workspace.RestoreSetup();setup.Mode="discover";
        if(workspace.Status=="Draft"&&setup.MetadataProvider!=SelectedMetadataProvider) {
            setup.MetadataProvider=SelectedMetadataProvider;
            workspace.SeriesIdentity=null;workspace.Catalog="";catalogPath="";SeriesName.Text="";ShowIdentity.Text="Identify the series with the selected provider.";
            ShowCandidatePanel.Visibility=Visibility.Collapsed;ShowChoices.ItemsSource=null;ShowEvidence.Clear();
            ShowDiscoveryStatus.Text="Provider changed. Identify the series again.";
        }
        setup.MetadataProvider=SelectedMetadataProvider;
        if(ShowChoices.SelectedItem is ShowCandidate candidate)setup.CandidateKey=candidate.Series.Key;
        try{workspaces.Save(workspace);}catch(Exception ex){Status.Text="Could not save setup: "+ex.Message;}
        RefreshScoutActions();
    }
    private void SetupSelectionChanged(object sender,SelectionChangedEventArgs e) {
        if(ReferenceEquals(sender,e.Source))PersistSetup();
    }
    private void SetupTextChanged(object sender,TextChangedEventArgs e)=>PersistSetup();
    private string SelectedMetadataProvider=>ShowProvider.SelectedIndex switch{1=>"tmdb",2=>"tvmaze",_=>"tvdb"};
    private MetadataCatalog CatalogClient(HttpClient http,string provider)=>new(http,provider,provider=="tvmaze"?null:settingsStore.ReadKey("metadata|"+provider),provider=="tvdb"?settingsStore.ReadKey("metadata|tvdb-pin"):null);
    private static HttpClient MetadataHttp()=>new(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(60)};
    private List<string>? ChooseVideos() {
        if(workspace==null)throw new InvalidOperationException("Create or select a Scout first.");
        var picker=new VideoSelectionWindow(workspace.Library,workspace.SelectedFiles){Owner=this,Title="Choose episodes to identify"};
        if(picker.ShowDialog()!=true)return null;
        var paths=VideoSelection.ValidateSaved(workspace.Library,picker.SelectedFiles);
        if(workspace.Status=="Draft"){workspace.SelectedFiles=paths;workspaces.Save(workspace);RefreshSelection();}
        return paths;
    }
    private void RefreshSelection() {
        if(SelectionSummary==null)return;
        var paths=workspace?.SelectedFiles;
        SelectionSummary.Text=paths==null||paths.Count==0?"Episodes to identify: not selected yet. Choose one, a season, or the full show.":paths.Count==1?"Identify 1 video: "+Path.GetFileName(paths[0]):$"Identify {paths.Count} videos in this Scout.";
        SelectionSummary.ToolTip=paths==null?null:string.Join("\n",paths);
        DiscoverySelectionSummary.ToolTip=workspace==null?null:string.Join("\n",workspace.DiscoveryFiles);
        DiscoverySelectionSummary.Text=workspace?.DiscoveryFiles.Count>0?$"Discovery sample: {workspace.DiscoveryFiles.Count} video(s). This does not limit episode identification.":"Scout checks file hints first. AI assistance can check dialogue from up to two videos. Episode selection happens later.";
    }
    private void RefreshDiscovery() {
        if(ShowIdentity==null)return;
        RefreshSelection();
        ShowIdentity.Text=workspace?.SeriesIdentity?.Label??"No catalog identity selected.";
        if(discoveryRun==workspace?.Id)return;
        restoringSetup=true;
        try {
            discoveryRun=workspace?.Id;ShowCandidatePanel.Visibility=Visibility.Collapsed;ShowChoices.ItemsSource=null;ShowEvidence.Clear();ShowDiscoveryStatus.Text="Find the series from file hints, then confirm it or ask AI to check.";
            var setup=workspace?.RestoreSetup()??new ScoutSetup();
            ShowProvider.SelectedIndex=setup.MetadataProvider switch{"tmdb"=>1,"tvmaze"=>2,_=>0};
            if(workspace==null)return;
            var path=Path.Combine(workspaces.DirectoryFor(workspace),"discovery.json");
            if(ScoutStorage.Exists(path))try{var saved=JsonSerializer.Deserialize<DiscoveryResult>(ScoutStorage.ReadText(path),Data.Options)!;if(saved.Provider==SelectedMetadataProvider)DisplayDiscovery(saved);}catch(JsonException){ShowDiscoveryStatus.Text="Saved discovery could not be read. Run discovery again.";}
        }finally{restoringSetup=false;}
    }

    private void DisplayDiscovery(DiscoveryResult result) {
        ShowCandidatePanel.Visibility=result.Candidates.Count>0?Visibility.Visible:Visibility.Collapsed;ShowChoices.ItemsSource=result.Candidates;ShowChoices.SelectedItem=result.Candidates.FirstOrDefault(c=>c.Series.Key==(result.ConfirmedSeriesKey??workspace?.Setup?.CandidateKey))??result.Candidates.FirstOrDefault();
        ShowDiscoveryStatus.Text=result.Status+"\n"+string.Join(" ",result.Notes);
    }
    private void ShowChoiceChanged(object sender,SelectionChangedEventArgs e) {
        if(ShowEvidence!=null)ShowEvidence.Text=(ShowChoices.SelectedItem as ShowCandidate)?.Detail??"";
        PersistSetup();
    }
    private async void DiscoverShow(object sender,RoutedEventArgs e) {
        if(operation!=null||workspace==null)return;
        LocalRuntime? runtime=null;ModelTransport? transport=null;HttpClient? modelHttp=null;
        try {
            if(!EnsureDraftScout())return;
            var paths=ShowDiscovery.RandomSample(VideoSelection.Enumerate(workspace.Library));
            if(paths.Count==0)throw new InvalidOperationException("No supported videos were found in this folder.");
            workspace.DiscoveryFiles=paths;workspaces.Save(workspace);RefreshSelection();
            var provider=SelectedMetadataProvider;var root=workspace.Library;var directory=workspaces.DirectoryFor(workspace);var settings=SaveSettingsCore();
            operation=new();SetBusy(true);ShowPage("Activity");var token=operation.Token;IProgress<string> progress=new Progress<string>(Log);
            using var http=MetadataHttp();var catalog=CatalogClient(http,provider);
            var hints=await Task.Run(()=>new ShowDiscovery(catalog).DiscoverAsync(root,settings.Ffprobe,settings.Ffmpeg,null,progress,token,paths,hintsOnly:true),token);
            DisplayDiscovery(hints);
            var choice=new SeriesHintWindow(hints){Owner=this};
            if(choice.ShowDialog()!=true){Log("Series selection cancelled. No model was started.");ShowPage("Library");return;}
            if(!choice.UseAi) {
                var confirmed=ShowDiscovery.ConfirmHint(hints,choice.SelectedCandidate!.Series.Key);
                Data.Save(Path.Combine(directory,"discovery.json"),hints);ApplyDiscoveredShow(confirmed);DisplayDiscovery(hints);
                ShowPage("Library");return;
            }
            if(string.IsNullOrWhiteSpace(settings.Model))throw new InvalidOperationException("Configure a model in Settings to use AI assistance. You can confirm a catalog suggestion without a model.");
            workspace.SeriesIdentity=null;workspace.Catalog="";catalogPath="";SeriesName.Text="";ShowIdentity.Text="Identifying series…";workspaces.Save(workspace);
            var requestNumber=0;
            async Task<JsonObject> Infer(List<DiscoveryDialogue> blocks,List<ShowCandidate> candidates,CancellationToken ct) {
                var options=settings.Inference(outputTokenLimit:ShowDiscovery.OutputTokenLimit(settings.ManagedThinking));
                var cacheKey=ShowDiscovery.CacheKey(settings.Model,settings.Endpoint,options,blocks,candidates);
                var cachePath=Path.Combine(directory,"discovery-cache",cacheKey+".json");
                if(ScoutStorage.Exists(cachePath)){var saved=Data.Read(cachePath);ShowDiscovery.ValidateResponse(saved,candidates,blocks);progress.Report("Reusing unchanged show-discovery evidence.");return saved;}
                if(transport==null) {
                    if(string.IsNullOrWhiteSpace(settings.Model))throw new InvalidOperationException("Configure a model in Settings before identifying the series.");
                    var key=settings.Provider is "openai" or "compatible"?settingsStore.ReadKey(settings.CredentialId):null;
                    ConnectionPolicy.Validate(settings.Provider,settings.Endpoint,settings.AllowCloudDialogue,key);
                    if(settings.Provider=="managed")runtime=await LocalRuntime.StartAsync(settings,progress,ct);
                    modelHttp=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(settings.EffectiveTimeoutSeconds)};
                    transport=new(modelHttp,settings.Inference(key,ShowDiscovery.OutputTokenLimit(settings.ManagedThinking)));
                }
                var timer=Stopwatch.StartNew();var step=++requestNumber;var stage=candidates.Count>0?"verify series":"identify alternatives";
                progress.Report($"AI series check {step}: {stage} · {blocks.Count} dialogue samples, {candidates.Count} catalog candidates.");
                var pending=ShowDiscovery.InferAsync(transport,settings.Model,settings.Endpoint,blocks,candidates,ct);
                using var pulse=new CancellationTokenSource();
                async Task Elapsed() {
                    using var ticker=new PeriodicTimer(TimeSpan.FromSeconds(30));
                    try{while(await ticker.WaitForNextTickAsync(pulse.Token))progress.Report($"AI series check {step}: {stage} · waiting for model response · {timer.Elapsed.TotalSeconds:0}s elapsed.");}
                    catch(OperationCanceledException) when(pulse.IsCancellationRequested){}
                }
                var heartbeat=Elapsed();
                try{var response=await pending;Data.Save(cachePath,response);progress.Report($"AI series check {step} finished in {timer.Elapsed.TotalSeconds:0}s.");return response;}
                finally{pulse.Cancel();await heartbeat;}
            }
            var result=await Task.Run(()=>new ShowDiscovery(catalog).DiscoverAsync(root,settings.Ffprobe,settings.Ffmpeg,Infer,progress,token,paths,r=>Data.Save(Path.Combine(directory,"discovery.json"),r),requireDialogue:true),token);
            Data.Save(Path.Combine(directory,"discovery.json"),result);DisplayDiscovery(result);
            var supported=result.Candidates.Where(c=>c.DialogueSupported).ToList();
            if(supported.Count==1){ApplyDiscoveredShow(supported[0]);result.Status="Series identified. Choose Identify episodes to continue.";Data.Save(Path.Combine(directory,"discovery.json"),result);ShowDiscoveryStatus.Text=result.Status;}
            RecordEvent($"Show discovery inspected {paths.Count} selected files using {provider}.");ShowPage("Library");
        }catch(OperationCanceledException) when(operation?.IsCancellationRequested==true){Log("Series discovery cancelled by request. Identify the series again to continue.");}
        catch(OperationCanceledException ex){Error(new IOException("A series-discovery request was interrupted without a user cancellation. Retry series identification.",ex));}
        catch(Exception ex){Error(ex);}
        finally {
            if(transport!=null&&workspace!=null)Data.Save(Path.Combine(workspaces.DirectoryFor(workspace),"discovery-usage.json"),transport.Usage);
            if(runtime!=null)await runtime.DisposeAsync();modelHttp?.Dispose();operation?.Dispose();operation=null;SetBusy(false);
        }
    }
    private void ApplyDiscoveredShow(ShowCandidate selected) {
        if(workspace==null||!EnsureDraftScout())return;
        var identity=selected.Series;identity.Validate();
        if(workspace.SeriesIdentity?.Key!=identity.Key){catalogPath="";workspace.Catalog="";}
        workspace.SeriesIdentity=identity;workspace.RestoreSetup().CandidateKey=identity.Key;workspaces.SaveIdentity(workspace.Library,identity);workspace.Name=identity.Name;SeriesName.Text=identity.Name;SaveDraft();
        RecordEvent((selected.DialogueSupported?"AI verified catalog identity: ":"User confirmed catalog identity from file hints: ")+identity.Label);RefreshHistory();RefreshDiscovery();
        Status.Text=(selected.DialogueSupported?"Series verified by dialogue.":"Series confirmed from file hints.")+" Choose Identify episodes to select the files to process.";
    }
    private void SaveMetadataKeys() {
        foreach(var (box,id) in new[]{(TvdbKeyEntry,"tvdb"),(TvdbPinEntry,"tvdb-pin"),(TmdbTokenEntry,"tmdb")})
            if(!string.IsNullOrWhiteSpace(box.Password)){settingsStore.SetKey("metadata|"+id,box.Password.Trim());box.Clear();}
        RefreshMetadataKeyStatus();
    }
    private void RefreshMetadataKeyStatus() {
        foreach(var (label,id,kind) in new[]{(TvdbKeyStatus,"tvdb","Key"),(TvdbPinStatus,"tvdb-pin","PIN"),(TmdbTokenStatus,"tmdb","Token")}) {
            try {label.Text=string.IsNullOrWhiteSpace(settingsStore.ReadKey("metadata|"+id))?kind+" not configured":kind+" configured";}
            catch(Exception) {label.Text=kind+" could not be unlocked. Enter a replacement.";}
        }
    }
    private void RemoveMetadataKeys(object sender,RoutedEventArgs e) {
        if(operation!=null)return;
        try {foreach(var id in new[]{"tvdb","tvdb-pin","tmdb"})settingsStore.SetKey("metadata|"+id,null);TvdbKeyEntry.Clear();TvdbPinEntry.Clear();TmdbTokenEntry.Clear();SettingsStatus.Text="Metadata credentials removed.";}catch(Exception ex){Error(ex);}finally{RefreshMetadataKeyStatus();}
    }
}
