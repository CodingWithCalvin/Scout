using System.IO;
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
        if(restoringSetup||openingWorkspace||workspace==null||DiscoveryModeTabs==null||ShowSearch==null)return;
        var setup=workspace.RestoreSetup();setup.Mode=DiscoveryModeTabs.SelectedIndex==1?"discover":"known";
        setup.MetadataProvider=SelectedMetadataProvider;setup.Query=ShowSearch.Text;
        if(ShowChoices.SelectedItem is ShowCandidate candidate)setup.CandidateKey=candidate.Series.Key;
        try{workspaces.Save(workspace);}catch(Exception ex){Status.Text="Could not save setup: "+ex.Message;}
    }
    private void SetupSelectionChanged(object sender,SelectionChangedEventArgs e) {
        if(ReferenceEquals(sender,e.Source))PersistSetup();
    }
    private void SetupTextChanged(object sender,TextChangedEventArgs e)=>PersistSetup();
    private string SelectedMetadataProvider=>ShowProvider.SelectedIndex switch{1=>"tmdb",2=>"tvmaze",_=>"tvdb"};
    private MetadataCatalog CatalogClient(HttpClient http,string provider)=>new(http,provider,provider=="tvmaze"?null:settingsStore.ReadKey("metadata|"+provider),provider=="tvdb"?settingsStore.ReadKey("metadata|tvdb-pin"):null);
    private static HttpClient MetadataHttp()=>new(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(60)};
    private List<string>? ChooseVideos(bool discovery=false) {
        if(workspace==null)throw new InvalidOperationException("Create or select a Scout first.");
        var saved=discovery?workspace.DiscoveryFiles:workspace.SelectedFiles;
        List<string> chosen;
        if(discovery) {
            var picker=new DiscoverySampleWindow(workspace.Library,saved){Owner=this};
            if(picker.ShowDialog()!=true)return null;
            chosen=picker.SelectedFiles;
        } else {
            var picker=new VideoSelectionWindow(workspace.Library,saved){Owner=this,Title="Choose episodes to identify"};
            if(picker.ShowDialog()!=true)return null;
            chosen=picker.SelectedFiles;
        }
        var paths=VideoSelection.ValidateSaved(workspace.Library,chosen);
        if(workspace.Status=="Draft"){if(discovery)workspace.DiscoveryFiles=paths;else workspace.SelectedFiles=paths;workspaces.Save(workspace);RefreshSelection();}
        return paths;
    }
    private void RefreshSelection() {
        if(SelectionSummary==null)return;
        var paths=workspace?.SelectedFiles;
        SelectionSummary.Text=paths==null||paths.Count==0?"Episodes to identify: not selected yet. Choose one, a season, or the full show.":paths.Count==1?"Identify 1 video: "+Path.GetFileName(paths[0]):$"Identify {paths.Count} videos in this run.";
        SelectionSummary.ToolTip=paths==null?null:string.Join("\n",paths);
        DiscoverySelectionSummary.Text=workspace?.DiscoveryFiles.Count>0?$"Discovery sample: {workspace.DiscoveryFiles.Count} video(s). This does not limit episode identification.":"Choose a small sample to discover the show. Select episodes to identify separately.";
    }
    private void ChangeSelection(object sender,RoutedEventArgs e) {
        if(operation!=null||workspace==null)return;
        try {
            var paths=ChooseVideos();if(paths==null)return;
            if(workspace.Status!="Draft"||report!=null)NewRun(null,null);
            workspace!.SelectedFiles=paths;workspaces.Save(workspace);RefreshSelection();
            Status.Text=$"Selected {paths.Count} videos. Episode identification will use this selection. The discovery sample is unchanged.";
        }catch(Exception ex){Error(ex);}
    }
    private void RefreshDiscovery() {
        if(ShowIdentity==null)return;
        RefreshSelection();
        ShowIdentity.Text=workspace?.SeriesIdentity?.Label??"No catalog identity selected.";
        if(discoveryRun==workspace?.Id)return;
        restoringSetup=true;
        try {
            discoveryRun=workspace?.Id;ShowCandidatePanel.Visibility=Visibility.Collapsed;ShowChoices.ItemsSource=null;ShowEvidence.Clear();ShowDiscoveryStatus.Text="Search a known title or ID, or discover from selected files.";
            var setup=workspace?.RestoreSetup()??new ScoutSetup();
            DiscoveryModeTabs.SelectedIndex=setup.Mode=="discover"?1:0;
            ShowSearch.Text=setup.Query;
            ShowProvider.SelectedIndex=setup.MetadataProvider switch{"tmdb"=>1,"tvmaze"=>2,_=>0};
            if(workspace==null)return;
            var path=Path.Combine(workspaces.DirectoryFor(workspace),"discovery.json");
            if(File.Exists(path))try{DisplayDiscovery(JsonSerializer.Deserialize<DiscoveryResult>(File.ReadAllText(path),Data.Options)!);}catch(JsonException){ShowDiscoveryStatus.Text="Saved discovery could not be read. Run discovery again.";}
        }finally{restoringSetup=false;}
    }

    private void DisplayDiscovery(DiscoveryResult result) {
        ShowCandidatePanel.Visibility=result.Candidates.Count>0?Visibility.Visible:Visibility.Collapsed;ShowChoices.ItemsSource=result.Candidates;ShowChoices.SelectedItem=result.Candidates.FirstOrDefault(c=>c.Series.Key==workspace?.Setup?.CandidateKey)??result.Candidates.FirstOrDefault();
        ShowDiscoveryStatus.Text=result.Status+"\n"+string.Join(" ",result.Notes);
    }
    private void ShowChoiceChanged(object sender,SelectionChangedEventArgs e) {
        if(ShowEvidence!=null)ShowEvidence.Text=(ShowChoices.SelectedItem as ShowCandidate)?.Detail??"";
        PersistSetup();
    }
    private async void LookupShow(object sender,RoutedEventArgs e) {
        if(operation!=null||workspace==null)return;
        var query=ShowSearch.Text.Trim();if(query.Length==0){Status.Text="Enter a show title or series ID.";return;}
        try {
            operation=new();SetBusy(true);var provider=SelectedMetadataProvider;
            using var http=MetadataHttp();var catalog=CatalogClient(http,provider);
            List<SeriesIdentity> series;
            if((string?)((Button)sender).Tag=="id") {var match=await catalog.GetAsync(query,operation.Token);series=match==null?[]:[match];}
            else series=await catalog.SearchAsync(query,operation.Token);
            var result=new DiscoveryResult{Provider=provider,Status=series.Count>0?"Catalog results. Choose the correct show and year, then use this show.":"No series found in this provider.",Candidates=series.Select(s=>new ShowCandidate{Series=s,Evidence=["Direct catalog lookup. No dialogue or model processing."]}).ToList()};
            Data.Save(Path.Combine(workspaces.DirectoryFor(workspace),"discovery.json"),result);DisplayDiscovery(result);
            Status.Text="Catalog lookup complete. No model requests.";
        }catch(OperationCanceledException){Status.Text="Lookup cancelled.";}catch(Exception ex){Error(ex);}
        finally{operation?.Dispose();operation=null;SetBusy(false);}
    }
    private async void DiscoverShow(object sender,RoutedEventArgs e) {
        if(operation!=null||workspace==null)return;
        LocalRuntime? runtime=null;ModelTransport? transport=null;HttpClient? modelHttp=null;
        try {
            var paths=ChooseVideos(discovery:true);if(paths==null)return;
            if(workspace!.Status!="Draft"||report!=null)CreateRun(workspace.Library);
            workspace.DiscoveryFiles=paths;workspaces.Save(workspace);RefreshSelection();
            var provider=SelectedMetadataProvider;var root=workspace.Library;var directory=workspaces.DirectoryFor(workspace);var settings=appSettings;
            operation=new();SetBusy(true);ShowPage("Activity");var token=operation.Token;IProgress<string> progress=new Progress<string>(Log);
            using var http=MetadataHttp();var catalog=CatalogClient(http,provider);
            async Task<JsonObject> Infer(List<DiscoveryDialogue> blocks,List<ShowCandidate> candidates,CancellationToken ct) {
                var cacheKey=CollectionResolver.Hash(JsonSerializer.Serialize(new{version="show-discovery-v1",settings.Provider,settings.Model,settings.Endpoint,settings.MaxOutputTokens,blocks,candidates=candidates.Select(c=>c.Series)}));
                var cachePath=Path.Combine(directory,"discovery-cache",cacheKey+".json");
                if(File.Exists(cachePath)){progress.Report("Reusing unchanged show-discovery evidence.");return Data.Read(cachePath);}
                if(transport==null) {
                    if(string.IsNullOrWhiteSpace(settings.Model))throw new InvalidOperationException("File clues were inconclusive. Configure a model in Settings to continue with automatic dialogue fallback, or use Known show to look up the show directly.");
                    var key=settings.Provider is "openai" or "compatible"?settingsStore.ReadKey(settings.CredentialId):null;
                    ConnectionPolicy.Validate(settings.Provider,settings.Endpoint,settings.AllowCloudDialogue,key);
                    if(settings.Provider=="managed")runtime=await LocalRuntime.StartAsync(settings,progress,ct);
                    modelHttp=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(settings.RequestTimeoutSeconds)};
                    transport=new(modelHttp,new(){Provider=settings.Provider,ApiKey=key,AllowCloudDialogue=settings.AllowCloudDialogue,MaxOutputTokens=settings.MaxOutputTokens,MaxRequests=settings.MaxRequestsPerRun});
                }
                var response=await ShowDiscovery.InferAsync(transport,settings.Model,settings.Endpoint,blocks,candidates,ct);Data.Save(cachePath,response);return response;
            }
            var result=await Task.Run(()=>new ShowDiscovery(catalog).DiscoverAsync(root,settings.Ffprobe,settings.Ffmpeg,Infer,progress,token,paths,r=>Data.Save(Path.Combine(directory,"discovery.json"),r)),token);
            Data.Save(Path.Combine(directory,"discovery.json"),result);DisplayDiscovery(result);RecordEvent($"Show discovery inspected {paths.Count} selected files using {provider}.");ShowPage("Library");
        }catch(OperationCanceledException){Log("Show discovery cancelled. No identity changed.");}
        catch(Exception ex){Error(ex);}
        finally {
            if(transport!=null&&workspace!=null)Data.Save(Path.Combine(workspaces.DirectoryFor(workspace),"discovery-usage.json"),transport.Usage);
            if(runtime!=null)await runtime.DisposeAsync();modelHttp?.Dispose();operation?.Dispose();operation=null;SetBusy(false);
        }
    }
    private void UseDiscoveredShow(object sender,RoutedEventArgs e) {
        if(operation!=null||workspace==null||ShowChoices.SelectedItem is not ShowCandidate selected)return;
        try {
            var identity=selected.Series;identity.Validate();
            var selection=workspace.SelectedFiles.ToList();
            if(workspace.Status!="Draft"||report!=null)CreateRun(workspace.Library);
            workspace.SelectedFiles=selection;
            if(workspace!.SeriesIdentity?.Key!=identity.Key){catalogPath="";workspace.Catalog="";}
            workspace.SeriesIdentity=identity;workspaces.SaveIdentity(workspace.Library,identity);workspace.Name=identity.Name;SeriesName.Text=identity.Name;SaveDraft();
            RecordEvent("Selected catalog identity: "+identity.Label);RefreshHistory();RefreshDiscovery();
            Status.Text="Show selected. Match filenames without AI, or identify episodes from dialogue.";
        }catch(Exception ex){Error(ex);}
    }
    private void ImportKnownGuide(object sender,RoutedEventArgs e) {
        if(operation!=null||workspace==null)return;
        if(PickFile("Use saved episode guide (no AI)") is not string path)return;
        try {
            var identity=SavedGuide.Identity(path,SeriesName.Text.Trim());
            if(workspace.Status!="Draft"||report!=null)CreateRun(workspace.Library);
            catalogPath=workspaces.SaveCatalog(workspace!,path);
            workspace!.SeriesIdentity=identity;workspace.Name=identity.Name;SeriesName.Text=identity.Name;
            workspaces.SaveIdentity(workspace.Library,identity);SaveDraft();RefreshHistory();RefreshDiscovery();
            ShowCandidatePanel.Visibility=Visibility.Collapsed;ShowChoices.ItemsSource=null;ShowEvidence.Clear();
            ShowDiscoveryStatus.Text="Saved guide selected: "+identity.Label+". No live provider lookup; no model requests.";
            RecordEvent(ShowDiscoveryStatus.Text);
        }catch(Exception ex){Error(ex);}
    }
    private async void MatchKnownFilenames(object sender,RoutedEventArgs e) {
        if(operation!=null||workspace==null)return;
        try {
            var identity=workspace.SeriesIdentity??throw new InvalidOperationException("Look up and select a catalog show first.");
            var savedGuide=catalogPath;
            var savedEpisodes=File.Exists(savedGuide)?SavedGuide.Episodes(savedGuide,identity):null;
            var paths=ChooseVideos();if(paths==null)return;
            if(workspace.Status!="Draft"||report!=null){CreateRun(workspace.Library);workspace!.SeriesIdentity=identity;SeriesName.Text=identity.Name;SaveDraft();}
            workspace.SelectedFiles=paths;workspaces.Save(workspace);RefreshSelection();
            operation=new();SetBusy(true);ShowPage("Activity");workspace.Status="Running";RecordEvent("Filename/catalog matching. No LLM requests; video content is not identified.");
            using var http=MetadataHttp();var catalog=CatalogClient(http,identity.Provider);
            var episodes=savedEpisodes??await catalog.EpisodesAsync(identity.Id,operation.Token);
            RecordEvent(savedEpisodes!=null?"Using this run's saved episode guide. No live provider lookup.":"Downloaded episode guide from "+identity.Provider+".");
            var output=workspaces.DirectoryFor(workspace);catalogPath=Path.Combine(output,"catalog.json");workspace.Catalog=catalogPath;
            var token=operation.Token;IProgress<string> progress=new Progress<string>(Log);var root=workspace.Library;
            var path=await Task.Run(()=>FilenameCatalogMatcher.RunAsync(paths,root,output,identity,episodes,progress,token),token);
            LoadReport(path);Status.Text="Filename/catalog matching complete. Content was not verified. Generate a move preview to review proposed changes.";
        }catch(OperationCanceledException){if(workspace!=null){workspace.Status="Cancelled";RecordEvent("Filename matching cancelled.");}}
        catch(Exception ex){if(workspace!=null){workspace.Status="Needs attention";RecordEvent("Filename matching did not complete.");}Error(ex);}
        finally{operation?.Dispose();operation=null;SetBusy(false);}
    }
    private async void DownloadGuide(object sender,RoutedEventArgs e) {
        if(operation!=null||workspace==null)return;
        try {
            var identity=workspace.SeriesIdentity??throw new InvalidOperationException("Select a catalog show first.");
            if(workspace.Status!="Draft"||report!=null){CreateRun(workspace.Library);workspace!.SeriesIdentity=identity;SeriesName.Text=identity.Name;SaveDraft();}
            operation=new();SetBusy(true);
            using var http=MetadataHttp();var episodes=await CatalogClient(http,identity.Provider).EpisodesAsync(identity.Id,operation.Token);
            catalogPath=Path.Combine(workspaces.DirectoryFor(workspace),"catalog.json");Data.Save(catalogPath,new{series_identity=identity,episodes});SaveDraft();
            RecordEvent($"Saved {episodes.Count} episode records from {identity.Provider}. No model requests.");ShowPage("Library");
        }catch(OperationCanceledException){Status.Text="Guide download cancelled.";}catch(Exception ex){Error(ex);}
        finally{operation?.Dispose();operation=null;SetBusy(false);}
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
